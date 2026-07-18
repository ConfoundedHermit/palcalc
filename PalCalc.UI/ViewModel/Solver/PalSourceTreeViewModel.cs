using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PalCalc.Model;

using PalCalc.SaveReader;
using PalCalc.UI.Localization;
using PalCalc.UI.Model;
using PalCalc.UI.ViewModel.Mapped;
using QuickGraph;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace PalCalc.UI.ViewModel.Solver
{
    public interface IPalSourceTreeNode : INotifyPropertyChanged
    {
        public ILocalizedText Label { get; }

        public bool? IsChecked { get; set; }

        public IEnumerable<IPalSourceTreeNode> Children { get; }

        /// <summary>
        /// Returns the current state of this node as a selection, which will also encompass any child-node selections.
        /// </summary>
        public List<IPalSourceTreeSelection> AsSelection { get; }

        /// <summary>
        /// Updates the state of this node, and its children, so it matches the given selections.
        /// </summary>
        public void ReadFromSelections(List<IPalSourceTreeSelection> selections);
    }

    
    public partial class PlayerSourceTreeNodeViewModel : ObservableObject, IPalSourceTreeNode
    {
        private readonly PlayerInstance player;

        public PlayerSourceTreeNodeViewModel(PlayerInstance player) : this(player, 0) { }

        public PlayerSourceTreeNodeViewModel(PlayerInstance player, int palCount)
        {
            this.player = player;
            Label = new HardCodedText(player.Name);
            CountLabel = LocalizationCodes.LC_SOURCE_PALS_NODE_COUNT.Bind(palCount);
        }

        public PlayerInstance ModelObject => player;

        public ILocalizedText Label { get; }

        /// <summary>
        /// "(N)" style count of Pals directly owned by this player, shown next to the name.
        /// </summary>
        public ILocalizedText CountLabel { get; }

        public IEnumerable<IPalSourceTreeNode> Children => [];


        [ObservableProperty]
        private bool? isChecked = true;

        public List<IPalSourceTreeSelection> AsSelection => IsChecked == true ? [new SourceTreePlayerSelection(player)] : [];

        public void ReadFromSelections(List<IPalSourceTreeSelection> selections)
        {
            var directSelection = selections.OfType<SourceTreePlayerSelection>().Any(s => s.ModelObject.PlayerId == player.PlayerId);
            var allItemsSelection = selections.OfType<SourceTreeAllSelection>().Any();

            IsChecked = directSelection || allItemsSelection;
        }
    }


    public partial class GuildSourceTreeNodeViewModel : ObservableObject, IPalSourceTreeNode
    {
        private int suppressSelectionCount = 0;
        private void SuppressSelectionChangedDuring(Action fn)
        {
            suppressSelectionCount++;
            try { fn(); }
            finally { --suppressSelectionCount; }
        }

        public GuildSourceTreeNodeViewModel(CachedSaveGame source, GuildInstance guild)
        {
            ModelObject = guild;
            Label = new HardCodedText(guild.Name);

            // Count Pals directly owned by each player (party / palbox / dimensional storage).
            // This is a quick "at a glance" figure to help the user pick meaningful sources;
            // it intentionally doesn't try to replicate the full base/cage attribution used
            // by the solver's SourceTreePlayerSelection.Matches.
            var palCountByPlayer = source.OwnedPals
                .Where(p => p.OwnerPlayerId != null)
                .GroupBy(p => p.OwnerPlayerId)
                .ToDictionary(g => g.Key, g => g.Count());

            PlayerNodes = guild.MemberIds
                .Select(pid => new PlayerSourceTreeNodeViewModel(source.PlayersById[pid], palCountByPlayer.GetValueOrDefault(pid, 0)))
                .OrderBy(n => n.ModelObject.Name)
                .ToList();

            var guildPalCount = guild.MemberIds.Sum(pid => palCountByPlayer.GetValueOrDefault(pid, 0));
            CountLabel = LocalizationCodes.LC_SOURCE_PALS_NODE_COUNT.Bind(guildPalCount);

            Children = PlayerNodes.OfType<IPalSourceTreeNode>().ToList();


            foreach (var c in PlayerNodes)
            {
                PropertyChangedEventManager.AddHandler(c, MemberPlayer_CheckedPropertyChanged, nameof(c.IsChecked));
            }
        }

        private void MemberPlayer_CheckedPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            SuppressSelectionChangedDuring(() =>
            {
                if (Children.All(c => c.IsChecked == true))
                    IsChecked = true;
                else if (Children.All(c => c.IsChecked == false))
                    IsChecked = false;
                else
                    IsChecked = null;
            });

            if (suppressSelectionCount == 0)
            {
                OnPropertyChanged(nameof(AsSelection));
                OnPropertyChanged(nameof(IsChecked));
            }
        }

        public GuildInstance ModelObject { get; }

        public ILocalizedText Label { get; }

        /// <summary>
        /// "(N)" style count of Pals owned by all members of this guild, shown next to the name.
        /// </summary>
        public ILocalizedText CountLabel { get; }

        public List<PlayerSourceTreeNodeViewModel> PlayerNodes { get; }

        public IEnumerable<IPalSourceTreeNode> Children { get; }

        private bool? isChecked = true;
        public bool? IsChecked
        {
            get => isChecked;
            set
            {
                if (SetProperty(ref isChecked, value))
                {
                    SuppressSelectionChangedDuring(() =>
                    {
                        if (value == true)
                        {
                            foreach (var c in PlayerNodes)
                                c.IsChecked = true;
                        }
                        else if (value == false)
                        {
                            foreach (var c in PlayerNodes)
                                c.IsChecked = false;
                        }
                    });

                    if (suppressSelectionCount == 0)
                    {
                        OnPropertyChanged(nameof(AsSelection));
                    }
                }
            }
        }

        public List<IPalSourceTreeSelection> AsSelection =>
            Children.All(c => c.IsChecked == true)
                ? [new SourceTreeGuildSelection(ModelObject)]
                : Children.SelectMany(c => c.AsSelection).SkipNull().ToList();

        public void ReadFromSelections(List<IPalSourceTreeSelection> selections)
        {
            SuppressSelectionChangedDuring(() =>
            {
                if (selections.OfType<SourceTreeGuildSelection>().Any(s => s.ModelObject.Id == ModelObject.Id))
                {
                    foreach (var c in PlayerNodes)
                        c.IsChecked = true;
                }
                else
                {
                    foreach (var c in Children)
                        c.ReadFromSelections(selections);
                }
            });

            OnPropertyChanged(nameof(AsSelection));
        }
    }


    public partial class PalSourceTreeViewModel : ObservableObject
    {
        private bool suppressSelectionChanged = false;
        private void SuppressSelectionChangedDuring(Action fn)
        {
            suppressSelectionChanged = true;
            try { fn(); }
            finally { suppressSelectionChanged = false; }
        }

        // for XAML designer view
        public PalSourceTreeViewModel() : this(CachedSaveGame.SampleForDesignerView)
        {

        }

        public CachedSaveGame Save { get; }

        public PalSourceTreeViewModel(CachedSaveGame save)
        {
            Save = save;

            RootNodes = save.Guilds
                .OrderBy(g => g.Name)
                .Select(g => new GuildSourceTreeNodeViewModel(save, g))
                .OfType<IPalSourceTreeNode>()
                .ToList();

            // only subscribe to changes in root nodes for raising change-events, try to avoid massive event
            // cascades/re-triggering
            //
            // assume root nodes will raise events appropriately if children change
            foreach (var node in RootNodes)
            {
                PropertyChangedEventManager.AddHandler(node, Node_SelectionPropertyChanged, nameof(node.AsSelection));
            }

            SelectAllCommand = new RelayCommand(
                execute: () => SetAllChecked(true),
                canExecute: () => !AllNodes.All(n => n.IsChecked == true)
            );

            SelectNoneCommand = new RelayCommand(
                execute: () => SetAllChecked(false),
                canExecute: () => AllNodes.Any(n => n.IsChecked != false)
            );

            RefreshSummary();
        }

        private void SetAllChecked(bool isChecked)
        {
            SuppressSelectionChangedDuring(() =>
            {
                foreach (var node in AllNodes)
                    node.IsChecked = isChecked;
            });

            OnPropertyChanged(nameof(Selections));
            OnPropertyChanged(nameof(HasValidSource));
            RefreshSummary();
        }

        private void Node_SelectionPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (!suppressSelectionChanged)
            {
                OnPropertyChanged(nameof(Selections));
                OnPropertyChanged(nameof(HasValidSource));
                RefreshSummary();
            }
        }

        /// <summary>
        /// Command which selects (checks) every guild and player in the tree.
        /// </summary>
        public IRelayCommand SelectAllCommand { get; }

        /// <summary>
        /// Command which deselects (unchecks) every guild and player in the tree.
        /// </summary>
        public IRelayCommand SelectNoneCommand { get; }

        private ILocalizedText selectionSummary;
        /// <summary>
        /// Short "N of M players selected" summary shown in the section, so the user
        /// can tell at a glance how many players contribute source Pals without
        /// scanning every checkbox.
        /// </summary>
        public ILocalizedText SelectionSummary
        {
            get => selectionSummary;
            private set => SetProperty(ref selectionSummary, value);
        }

        private void RefreshSummary()
        {
            var playerNodes = AllNodes.OfType<PlayerSourceTreeNodeViewModel>().ToList();
            var selectedCount = playerNodes.Count(n => n.IsChecked == true);

            SelectionSummary = LocalizationCodes.LC_SOURCE_PALS_SELECTION_SUMMARY.Bind(
                new
                {
                    Selected = selectedCount,
                    Total = playerNodes.Count
                }
            );

            SelectAllCommand?.NotifyCanExecuteChanged();
            SelectNoneCommand?.NotifyCanExecuteChanged();
        }


        public List<IPalSourceTreeSelection> Selections
        {
            get
            {
                return AllNodes.All(n => n.IsChecked == true)
                    ? [new SourceTreeAllSelection()]
                    : RootNodes.SelectMany(n => n.AsSelection).ToList();
            }
            set
            {
                SuppressSelectionChangedDuring(() =>
                {
                    if (value.OfType<SourceTreeAllSelection>().Any())
                    {
                        foreach (var node in AllNodes)
                            node.IsChecked = true;
                    }
                    else
                    {
                        foreach (var node in RootNodes)
                            node.ReadFromSelections(value);
                    }
                });
                OnPropertyChanged(nameof(Selections));
                OnPropertyChanged(nameof(HasValidSource));
                RefreshSummary();
            }
        }

        public bool HasValidSource => Selections.Any();


        public List<IPalSourceTreeNode> RootNodes { get; }

        private IEnumerable<IPalSourceTreeNode> AllNodes
        {
            get
            {
                IEnumerable<IPalSourceTreeNode> Enumerate(IPalSourceTreeNode node)
                {
                    yield return node;

                    foreach (var child in node.Children.SelectMany(Enumerate))
                    {
                        yield return child;
                    }
                }

                return RootNodes.SelectMany(Enumerate);
            }
        }
    }
}

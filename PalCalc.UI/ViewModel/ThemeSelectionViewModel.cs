using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PalCalc.UI.Localization;
using PalCalc.UI.Model;

namespace PalCalc.UI.ViewModel
{
    /// <summary>
    /// A single selectable theme entry for the top-bar "Theme" menu.
    /// Mirrors <see cref="TranslationLocaleViewModel"/>: it exposes a
    /// localized label, an <see cref="IsSelected"/> flag (checkable menu
    /// item), and a <see cref="SelectCommand"/>.
    ///
    /// Unlike the language switcher, changing the theme applies immediately
    /// (no restart) and persists the choice to <see cref="AppSettings"/>.
    /// </summary>
    public partial class ThemeSelectionViewModel : ObservableObject
    {
        public ThemeSelectionViewModel(AppTheme theme)
        {
            Value = theme;

            Label = theme == AppTheme.Light
                ? LocalizationCodes.LC_THEME_LIGHT.Bind()
                : LocalizationCodes.LC_THEME_DARK.Bind();

            SelectCommand = new RelayCommand(Apply);

            ThemeManager.ThemeChanged += () => OnPropertyChanged(nameof(IsSelected));
        }

        public AppTheme Value { get; }

        public ILocalizedText Label { get; }

        public bool IsSelected
        {
            get => ThemeManager.CurrentTheme == Value;
            set
            {
                if (value && !IsSelected)
                    Apply();
                else
                    OnPropertyChanged();
            }
        }

        public IRelayCommand SelectCommand { get; }

        private void Apply()
        {
            if (ThemeManager.CurrentTheme == Value) return;

            ThemeManager.Apply(Value);

            var settings = AppSettings.Current;
            if (settings != null)
            {
                settings.Theme = Value;
                Storage.SaveAppSettings(settings);
            }
        }
    }
}

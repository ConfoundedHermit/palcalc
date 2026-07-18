using AdonisUI;
using System;
using System.Windows;

namespace PalCalc.UI.Model
{
    /// <summary>
    /// The available UI color schemes. Values map to AdonisUI color-scheme
    /// resource dictionaries (see <see cref="ResourceLocator"/>).
    /// </summary>
    public enum AppTheme
    {
        Dark,
        Light,
    }

    /// <summary>
    /// Runtime color-scheme (theme) switching for the app.
    ///
    /// Pal Calc merges AdonisUI's <c>Dark.xaml</c> color scheme as the FIRST
    /// entry in <c>App.xaml</c>'s merged dictionaries, followed by the
    /// ClassicTheme resources and the Pal Calc design-system dictionaries
    /// (DesignTokens / ControlStyles / StyleAdjustments). Those later
    /// dictionaries intentionally override a handful of AdonisUI colors.
    ///
    /// To preserve that override order across a theme swap, this manager
    /// replaces the color-scheme dictionary IN PLACE (at its existing index)
    /// rather than appending the new scheme to the end (which would let the
    /// raw AdonisUI colors win over Pal Calc's overrides).
    /// </summary>
    public static class ThemeManager
    {
        /// <summary>Raised after the active theme changes.</summary>
        public static event Action ThemeChanged;

        public static AppTheme CurrentTheme { get; private set; } = AppTheme.Dark;

        private static Uri UriFor(AppTheme theme) =>
            theme == AppTheme.Light
                ? ResourceLocator.LightColorScheme
                : ResourceLocator.DarkColorScheme;

        /// <summary>
        /// Applies the given theme by swapping the AdonisUI color-scheme
        /// dictionary in <see cref="Application.Current"/>'s resources.
        /// Safe to call repeatedly; a no-op if the theme is already active
        /// and the scheme dictionary is present.
        /// </summary>
        public static void Apply(AppTheme theme)
        {
            var app = Application.Current;
            if (app == null) return;

            var dicts = app.Resources.MergedDictionaries;

            var darkUri = ResourceLocator.DarkColorScheme.AbsoluteUri;
            var lightUri = ResourceLocator.LightColorScheme.AbsoluteUri;

            int existingIndex = -1;
            for (int i = 0; i < dicts.Count; i++)
            {
                var src = dicts[i].Source;
                if (src != null && src.IsAbsoluteUri &&
                    (src.AbsoluteUri == darkUri || src.AbsoluteUri == lightUri))
                {
                    existingIndex = i;
                    break;
                }
            }

            var newDict = new ResourceDictionary { Source = UriFor(theme) };

            if (existingIndex >= 0)
            {
                // Insert the new scheme immediately before the old one, then
                // remove the old one. Doing it in this order avoids WPF logging
                // spurious "resource not found" warnings during the swap, and
                // keeps the scheme at the same position so Pal Calc's later
                // color overrides continue to take precedence.
                dicts.Insert(existingIndex, newDict);
                dicts.RemoveAt(existingIndex + 1);
            }
            else
            {
                dicts.Insert(0, newDict);
            }

            CurrentTheme = theme;
            ThemeChanged?.Invoke();
        }
    }
}

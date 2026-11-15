using System.Windows;
using Microsoft.Win32;
using Renamer.Core;

namespace Renamer.App.Services;

/// <summary>Swaps the colour palette, and follows the Windows setting when asked to.</summary>
public static class ThemeService
{
    private const int PaletteSlot = 0;
    private const string PersonalizeKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static AppTheme _theme = AppTheme.System;
    private static bool _isDark = true;
    private static bool _isListening;

    public static void Apply(AppTheme theme)
    {
        _theme = theme;
        Load(theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => WindowsUsesDarkApps(),
        });

        if (!_isListening)
        {
            _isListening = true;
            SystemEvents.UserPreferenceChanged += OnWindowsSettingChanged;
        }
    }

    private static void OnWindowsSettingChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General && _theme == AppTheme.System)
        {
            Application.Current?.Dispatcher.Invoke(() => Load(WindowsUsesDarkApps()));
        }
    }

    private static void Load(bool dark)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;

        // App.xaml starts on the dark palette, so loading it again would only cause a redraw.
        var isLoaded = dictionaries[PaletteSlot].Source is not null && _isDark == dark;
        if (isLoaded)
        {
            return;
        }

        _isDark = dark;
        dictionaries[PaletteSlot] = new ResourceDictionary
        {
            Source = new Uri($"Views/Theme/Palette.{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative),
        };
    }

    private static bool WindowsUsesDarkApps() =>
        Registry.GetValue(PersonalizeKey, "AppsUseLightTheme", 1) is 0;
}

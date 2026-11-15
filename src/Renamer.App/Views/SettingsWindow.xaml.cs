using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using Microsoft.Win32;
using Renamer.App.ViewModels;
using Renamer.Core;

namespace Renamer.App.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;
    private bool _syncingKey;

    public SettingsWindow(SettingsViewModel vm, int tab)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        Tabs.SelectedIndex = tab;

        // PasswordBox cannot be data-bound, so it is kept in step with the view model by hand.
        KeyHidden.Password = vm.ApiKey;
        vm.PropertyChanged += OnViewModelChanged;
        UpdateSaveButton();

        Closed += (_, _) => vm.PropertyChanged -= OnViewModelChanged;
    }

    public AppSettings? Result { get; private set; }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.ValidationError))
        {
            UpdateSaveButton();
        }
        else if (e.PropertyName == nameof(SettingsViewModel.ApiKey) && !_syncingKey && KeyHidden.Password != _vm.ApiKey)
        {
            _syncingKey = true;
            KeyHidden.Password = _vm.ApiKey;
            _syncingKey = false;
        }
    }

    private void UpdateSaveButton() => SaveButton.IsEnabled = _vm.ValidationError is null;

    private void OnKeyHiddenChanged(object sender, RoutedEventArgs e)
    {
        if (!_syncingKey)
        {
            _syncingKey = true;
            _vm.ApiKey = KeyHidden.Password;
            _syncingKey = false;
        }
    }

    private void OnShowKeyChanged(object sender, RoutedEventArgs e)
    {
        var show = ShowKey.IsChecked == true;
        KeyVisible.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        KeyHidden.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Clicking into a text box inside a row selects that row, so Move and Remove act on it.</summary>
    private void OnGridFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        for (var current = e.NewFocus as DependencyObject; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is DataGridRow row)
            {
                row.IsSelected = true;
                return;
            }
        }
    }

    private void OnBrowseMovies(object sender, RoutedEventArgs e) => Browse("Choose your movie library folder", path => _vm.MovieLibrary = path);

    private void OnBrowseTv(object sender, RoutedEventArgs e) => Browse("Choose your TV library folder", path => _vm.TvLibrary = path);

    private void Browse(string title, Action<string> apply)
    {
        var dialog = new OpenFolderDialog { Title = title };
        if (dialog.ShowDialog(this) == true)
        {
            apply(dialog.FolderName);
        }
    }

    private void OnLink(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_vm.ValidationError is not null)
        {
            return;
        }

        Result = _vm.BuildSettings();
        DialogResult = true;
    }
}

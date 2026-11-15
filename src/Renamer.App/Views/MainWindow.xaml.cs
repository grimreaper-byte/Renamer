using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Renamer.App.Services;
using Renamer.App.ViewModels;
using Renamer.Core;

namespace Renamer.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    /// <summary>
    /// A drag fires DragLeave every time it crosses into a child control, so the highlight only
    /// goes away once no DragOver has followed for a moment.
    /// </summary>
    private readonly DispatcherTimer _dragEnded = new() { Interval = TimeSpan.FromMilliseconds(150) };

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _dragEnded.Tick += (_, _) =>
        {
            _dragEnded.Stop();
            _vm.IsDragging = false;
        };
    }

    public async void AddPaths(IReadOnlyList<string> paths)
    {
        try
        {
            await _vm.AddPathsAsync(paths);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ErrorLog.Write(ex);
            MessageBox.Show(this, $"Could not add those files:\n\n{ex.Message}", "Renamer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var hasFiles = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;

        if (hasFiles)
        {
            _dragEnded.Stop();
            _vm.IsDragging = true;
        }
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        e.Handled = true;
        _dragEnded.Start();
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        _dragEnded.Stop();
        _vm.IsDragging = false;
        e.Handled = true;

        // AddPaths returns at its first await, so File Explorer is not kept waiting during a scan.
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
        {
            AddPaths(paths);
        }
    }

    private void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var extensions = FileScanner.ParseExtensions(_vm.Settings.VideoExtensions).Select(x => "*" + x);
        var dialog = new OpenFileDialog
        {
            Title = "Add video files",
            Multiselect = true,
            Filter = $"Video files|{string.Join(';', extensions)}|All files|*.*",
        };

        if (dialog.ShowDialog(this) == true)
        {
            AddPaths(dialog.FileNames);
        }
    }

    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Add a folder of videos", Multiselect = true };
        if (dialog.ShowDialog(this) == true)
        {
            AddPaths(dialog.FolderNames);
        }
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        var tab = sender is FrameworkElement { Tag: string text } && int.TryParse(text, out var index) ? index : 0;
        OpenSettings(tab);
    }

    private void OnAlertAction(object sender, RoutedEventArgs e) => OpenSettings(_vm.AlertTab);

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _vm.HasSelection = Files.SelectedItems.Count > 0;

    private void OpenSettings(int tab)
    {
        var window = new SettingsWindow(new SettingsViewModel(_vm.Settings), tab) { Owner = this };
        if (window.ShowDialog() == true && window.Result is not null)
        {
            _vm.ApplySettings(window.Result);
        }
    }

    private void OnChangeMatch(object sender, RoutedEventArgs e) => OpenMatchDialog();

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindParent<DataGridRow>(e.OriginalSource as DependencyObject) is not null)
        {
            OpenMatchDialog();
        }
    }

    private void OnRowRightClick(object sender, MouseButtonEventArgs e)
    {
        // Right-clicking a row outside the selection acts on that row, like File Explorer.
        if (FindParent<DataGridRow>(e.OriginalSource as DependencyObject) is { IsSelected: false } row)
        {
            Files.SelectedItems.Clear();
            row.IsSelected = true;
        }
    }

    private void OpenMatchDialog()
    {
        var item = Files.SelectedItems.OfType<MediaItem>().FirstOrDefault();
        if (item is null)
        {
            _vm.ShowMessage("Select a file in the list first.");
            return;
        }

        if (item.Status is ItemStatus.Renamed or ItemStatus.Renaming)
        {
            _vm.ShowMessage("That file has already been renamed. Use Undo to change it.");
            return;
        }

        if (_vm.Matcher is not { } matcher)
        {
            OpenSettings(0);
            return;
        }

        var window = new MatchWindow(new MatchViewModel(item, _vm.Items, matcher)) { Owner = this };
        if (window.ShowDialog() == true && window.Targets is { } targets && window.Choice is { } choice)
        {
            RunManualMatch(targets, choice);
        }
    }

    private async void RunManualMatch(IReadOnlyList<(MediaItem Item, ParsedMedia Parsed)> targets, TmdbCandidate choice)
    {
        try
        {
            await _vm.ApplyManualMatchAsync(targets, choice);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ErrorLog.Write(ex);
            MessageBox.Show(this, ex.Message, "Change match", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnRemoveSelected(object sender, RoutedEventArgs e) => RemoveSelected();

    private void RemoveSelected() => _vm.Remove(Files.SelectedItems.OfType<MediaItem>().ToList());

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (Files.SelectedItems.OfType<MediaItem>().FirstOrDefault() is { } item)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Path}\"") { UseShellExecute = true });
        }
    }

    private void OnAbout(object sender, RoutedEventArgs e)
    {
        new AboutWindow { Owner = this }.ShowDialog();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        switch (e.Key)
        {
            case Key.O when ctrl && shift:
                OnAddFolder(this, e);
                break;
            case Key.O when ctrl:
                OnAddFiles(this, e);
                break;
            case Key.M when ctrl:
                OpenMatchDialog();
                break;
            case Key.OemComma when ctrl:
                OpenSettings(0);
                break;
            case Key.Z when ctrl:
                Execute(_vm.UndoCommand);
                break;
            case Key.F5:
                Execute(_vm.MatchCommand);
                break;
            case Key.Delete:
                RemoveSelected();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private static void Execute(ICommand command)
    {
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    private static T? FindParent<T>(DependencyObject? start)
        where T : DependencyObject
    {
        for (var current = start; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }

            // Text inside a cell is not part of the visual tree, so step out through its logical parent.
            if (current is System.Windows.Documents.TextElement text)
            {
                current = text.Parent;
                if (current is T logicalMatch)
                {
                    return logicalMatch;
                }
            }
        }

        return null;
    }
}

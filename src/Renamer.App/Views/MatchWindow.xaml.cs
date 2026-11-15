using System.Windows;
using System.Windows.Input;
using Renamer.App.ViewModels;
using Renamer.Core;

namespace Renamer.App.Views;

public partial class MatchWindow : Window
{
    private readonly MatchViewModel _vm;

    public MatchWindow(MatchViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        Loaded += (_, _) => QueryBox.Focus();
    }

    public IReadOnlyList<(MediaItem Item, ParsedMedia Parsed)>? Targets { get; private set; }

    public TmdbCandidate? Choice { get; private set; }

    private void OnApply(object sender, RoutedEventArgs e) => Apply();

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.CanApply)
        {
            Apply();
        }
    }

    private void Apply()
    {
        Choice = _vm.Selected;
        Targets = _vm.BuildTargets();
        DialogResult = true;
    }

    /// <summary>Enter in a search box runs the search rather than pressing the default button.</summary>
    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (_vm.SearchCommand.CanExecute(null))
            {
                _vm.SearchCommand.Execute(null);
            }
        }
    }
}

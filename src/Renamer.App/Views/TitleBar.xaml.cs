using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Renamer.App.Views;

/// <summary>The app's own title bar, so the window chrome matches the rest of the design.</summary>
public partial class TitleBar : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(TitleBar));

    public static readonly DependencyProperty SlotProperty =
        DependencyProperty.Register(nameof(Slot), typeof(object), typeof(TitleBar));

    public static readonly DependencyProperty IsDialogProperty =
        DependencyProperty.Register(nameof(IsDialog), typeof(bool), typeof(TitleBar));

    private Window? _window;

    public TitleBar()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public string? Title
    {
        get => (string?)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public object? Slot
    {
        get => GetValue(SlotProperty);
        set => SetValue(SlotProperty, value);
    }

    /// <summary>Dialogs only offer a close button.</summary>
    public bool IsDialog
    {
        get => (bool)GetValue(IsDialogProperty);
        set => SetValue(IsDialogProperty, value);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _window = Window.GetWindow(this);
        if (_window is null)
        {
            return;
        }

        _window.StateChanged += OnWindowChanged;
        _window.Activated += OnWindowChanged;
        _window.Deactivated += OnWindowChanged;

        if (IsDialog)
        {
            MinimizeButton.Visibility = Visibility.Collapsed;
            MaximizeButton.Visibility = Visibility.Collapsed;
        }

        UpdateAppearance();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_window is not null)
        {
            _window.StateChanged -= OnWindowChanged;
            _window.Activated -= OnWindowChanged;
            _window.Deactivated -= OnWindowChanged;
        }
    }

    private void OnWindowChanged(object? sender, EventArgs e) => UpdateAppearance();

    private void UpdateAppearance()
    {
        if (_window is null)
        {
            return;
        }

        var isMaximized = _window.WindowState == WindowState.Maximized;
        MaximizeButton.Content = isMaximized ? "\uE923" : "\uE922";
        AutomationProperties.SetName(MaximizeButton, isMaximized ? "Restore" : "Maximize");

        // Fading the title is how an inactive window shows it is not the one in use.
        TitleText.Opacity = _window.IsActive ? 1 : 0.55;
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(_window!);

    private void OnMaximize(object sender, RoutedEventArgs e)
    {
        if (_window!.WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(_window);
        }
        else
        {
            SystemCommands.MaximizeWindow(_window);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(_window!);
}

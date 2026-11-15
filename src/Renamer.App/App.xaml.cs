using System.Windows;
using System.Windows.Threading;
using Renamer.App.Services;
using Renamer.App.Views;

namespace Renamer.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        var window = new MainWindow();
        window.Show();

        // Files dropped on the .exe itself, or sent through "Send to", arrive as arguments.
        if (e.Args.Length > 0)
        {
            window.AddPaths(e.Args);
        }
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ErrorLog.Write(e.Exception);
        MessageBox.Show(
            $"Something went wrong:\n\n{e.Exception.Message}\n\nDetails were saved to {AppPaths.ErrorLog}",
            "Renamer",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}

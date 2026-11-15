using System.IO;

namespace Renamer.App.Services;

public static class AppPaths
{
    /// <summary>RENAMER_DATA_DIR moves all saved state elsewhere, which keeps test runs apart from real settings.</summary>
    public static string DataFolder { get; } =
        Environment.GetEnvironmentVariable("RENAMER_DATA_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Renamer");

    public static string SettingsFile => Path.Combine(DataFolder, "settings.json");

    public static string HistoryFile => Path.Combine(DataFolder, "history.json");

    public static string ErrorLog => Path.Combine(DataFolder, "errors.log");
}

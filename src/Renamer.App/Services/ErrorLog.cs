using System.IO;

namespace Renamer.App.Services;

public static class ErrorLog
{
    public static void Write(Exception error)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataFolder);
            File.AppendAllText(AppPaths.ErrorLog, $"{DateTime.Now:u}{Environment.NewLine}{error}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Failing to log must not turn into a second error for the user.
        }
    }
}

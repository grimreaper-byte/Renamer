using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Renamer.Core;

namespace Renamer.App.Services;

/// <summary>
/// Loads and saves settings. The API key is encrypted with Windows DPAPI, so the saved value
/// can only be read back by the same Windows user on the same PC.
/// </summary>
public static class SettingsStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Renamer.TmdbApiKey");

    public static AppSettings Load()
    {
        var path = AppPaths.SettingsFile;
        if (!File.Exists(path))
        {
            return new AppSettings();
        }

        try
        {
            var settings = AppSettings.FromJson(File.ReadAllText(path));
            settings.ApiKey = Decrypt(settings.ApiKeyProtected);

            // Templates from an earlier version of a preset move to the current one, and templates
            // saved as Custom before a matching preset existed now show that preset's name.
            settings.Templates = Defaults.Upgrade(settings.Templates);
            settings.Preset = Defaults.PresetNameFor(settings.Templates);
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Keep the unreadable file so nothing is lost, and start again from the defaults.
            TryKeepCopy(path);
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        settings.ApiKeyProtected = Encrypt(settings.ApiKey);
        Directory.CreateDirectory(AppPaths.DataFolder);

        var temporary = AppPaths.SettingsFile + ".tmp";
        File.WriteAllText(temporary, settings.ToJson());
        File.Move(temporary, AppPaths.SettingsFile, overwrite: true);
    }

    private static string? Encrypt(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(key), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    private static string Decrypt(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return "";
        }

        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(stored), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Saved under another Windows account or PC. The user just enters the key again.
            return "";
        }
    }

    private static void TryKeepCopy(string path)
    {
        try
        {
            File.Copy(path, path + ".bad", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the backup copy is not worth stopping the app for.
        }
    }
}

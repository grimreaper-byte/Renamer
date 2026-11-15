using System.Text.RegularExpressions;

namespace Renamer.Core;

public sealed record ScanResult(IReadOnlyList<string> Videos, int NotVideo, int Ignored);

/// <summary>Expands dropped files and folders into the video files they contain.</summary>
public sealed class FileScanner
{
    private static readonly EnumerationOptions Recursive = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,

        // Skipping reparse points avoids following a junction back into a folder already seen.
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
    };

    private readonly HashSet<string> _videoExtensions;
    private readonly Regex? _ignore;

    public FileScanner(AppSettings settings)
    {
        _videoExtensions = ParseExtensions(settings.VideoExtensions);
        _ignore = string.IsNullOrWhiteSpace(settings.IgnorePattern) ? null : RegexTools.TryCreate(settings.IgnorePattern, out _);
    }

    public ScanResult Scan(IEnumerable<string> paths, CancellationToken ct = default)
    {
        var videos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var notVideo = 0;
        var ignored = 0;

        foreach (var path in paths)
        {
            IEnumerable<string> files;
            if (Directory.Exists(path))
            {
                files = Directory.EnumerateFiles(path, "*", Recursive);
            }
            else if (File.Exists(path))
            {
                files = [path];
            }
            else
            {
                continue;
            }

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                if (!_videoExtensions.Contains(Path.GetExtension(file)))
                {
                    notVideo++;
                }
                else if (IsIgnored(file))
                {
                    ignored++;
                }
                else
                {
                    videos.Add(Path.GetFullPath(file));
                }
            }
        }

        return new ScanResult(videos.ToList(), notVideo, ignored);
    }

    /// <summary>Reads a list like ".mkv, .mp4" or "mkv mp4" into a case-insensitive set.</summary>
    public static HashSet<string> ParseExtensions(string text) =>
        text.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(extension => extension.StartsWith('.') ? extension : "." + extension)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private bool IsIgnored(string file)
    {
        if (_ignore is null)
        {
            return false;
        }

        var folder = Path.GetFileName(Path.GetDirectoryName(file) ?? "");
        return RegexTools.SafeIsMatch(_ignore, Path.GetFileNameWithoutExtension(file)) ||
               RegexTools.SafeIsMatch(_ignore, folder);
    }
}

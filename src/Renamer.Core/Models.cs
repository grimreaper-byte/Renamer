namespace Renamer.Core;

public enum MediaKind
{
    Unknown,
    Movie,
    Episode,
}

/// <summary>What could be read from a file name and its folders, before asking TMDB.</summary>
public sealed record ParsedMedia
{
    public MediaKind Kind { get; init; }
    public string Title { get; init; } = "";
    public int? Year { get; init; }
    public int? Season { get; init; }
    public int? Episode { get; init; }

    /// <summary>Last episode of a multi-episode file (S01E01-E03 gives 3).</summary>
    public int? EpisodeEnd { get; init; }

    /// <summary>Episode number counted across the whole show, common for anime.</summary>
    public int? Absolute { get; init; }

    public DateOnly? AirDate { get; init; }
    public string? Resolution { get; init; }
    public string? Source { get; init; }
    public string? Codec { get; init; }

    /// <summary>Name of the pattern that matched, or a description of the folder rule used.</summary>
    public string? Pattern { get; init; }
}

/// <summary>Confirmed metadata used to fill in the naming templates.</summary>
public sealed record MediaInfo
{
    public MediaKind Kind { get; init; }

    /// <summary>The movie title, or the show name for an episode.</summary>
    public string Title { get; init; } = "";

    public int? Year { get; init; }
    public int? TmdbId { get; init; }
    public int? Season { get; init; }
    public IReadOnlyList<int> Episodes { get; init; } = [];
    public string? EpisodeTitle { get; init; }
    public DateOnly? AirDate { get; init; }
    public string? Resolution { get; init; }
    public string? Source { get; init; }
    public string? Codec { get; init; }
    public string? OriginalName { get; init; }
}

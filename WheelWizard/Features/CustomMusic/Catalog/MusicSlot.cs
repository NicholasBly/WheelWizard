namespace WheelWizard.CustomMusic.Catalog;

public enum MusicCategory
{
    /// <summary>A Retro Rewind race track (retro and custom tracks from ConfigRT/ConfigCT).</summary>
    RetroRewindTrack,

    /// <summary>A Retro Rewind battle arena (ConfigBT).</summary>
    RetroRewindArena,

    /// <summary>One of the original 32 courses (or Galaxy Colosseum).</summary>
    Course,

    /// <summary>One of the original 10 battle arenas.</summary>
    Arena,

    /// <summary>Menu background music.</summary>
    Menu,
}

/// <summary>One piece of music the player can replace, and the files the game loads for it.</summary>
public sealed record MusicSlot
{
    /// <summary>Stable identifier, used to remember songs between sessions and Retro Rewind updates.</summary>
    public required string Key { get; init; }

    public required string Name { get; init; }
    public required MusicCategory Category { get; init; }

    /// <summary>Section the music belongs to, e.g. "Mushroom Cup".</summary>
    public required string Group { get; init; }

    /// <summary>File names (no folders) that receive the normal version.</summary>
    public required IReadOnlyList<string> FileNames { get; init; }

    /// <summary>File names that receive the final lap version; empty when the music has no final lap.</summary>
    public IReadOnlyList<string> FinalLapFileNames { get; init; } = [];

    /// <summary>Short explanation of what currently plays here (e.g. "Uses Luigi Circuit music").</summary>
    public string? Detail { get; init; }

    public bool HasFinalLap => FinalLapFileNames.Count > 0;

    public IEnumerable<string> AllFileNames => FileNames.Concat(FinalLapFileNames);
}

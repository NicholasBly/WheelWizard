using System.ComponentModel;
using Avalonia.Media.Imaging;
using WheelWizard.CustomMusic.Catalog;
using WheelWizard.CustomMusic.Library;

namespace WheelWizard.CustomMusic.ViewModels;

/// <summary>A section heading in the music list (a cup, "Menus", ...).</summary>
public sealed class MusicGroupHeader(string title, bool isFirst)
{
    public string Title { get; } = title;

    /// <summary>The first heading sits flush with the top of the list.</summary>
    public bool IsFirst { get; } = isFirst;
}

/// <summary>One row in the custom music list.</summary>
public sealed class MusicSlotItem(MusicSlot slot) : INotifyPropertyChanged
{
    private CustomMusicEntry? _saved;
    private CustomMusicEntry? _current;
    private bool _isProcessing;
    private double _progress;
    private string? _error;
    private Bitmap? _cover;
    private bool _isDropTarget;

    public MusicSlot Slot { get; private set; } = slot;
    public string Name => Slot.Name;

    /// <summary>What is installed in the game right now.</summary>
    public CustomMusicEntry? Saved
    {
        get => _saved;
        set => Set(ref _saved, value);
    }

    /// <summary>What will be installed after saving (null = the original music).</summary>
    public CustomMusicEntry? Current
    {
        get => _current;
        set => Set(ref _current, value);
    }

    public bool IsProcessing
    {
        get => _isProcessing;
        set => Set(ref _isProcessing, value);
    }

    public double Progress
    {
        get => _progress;
        set => Set(ref _progress, value);
    }

    public string? Error
    {
        get => _error;
        set => Set(ref _error, value);
    }

    public Bitmap? Cover
    {
        get => _cover;
        set
        {
            if (ReferenceEquals(_cover, value))
                return;
            _cover?.Dispose();
            _cover = value;
            Changed();
        }
    }

    /// <summary>Highlighted while a file is dragged over this row.</summary>
    public bool IsDropTarget
    {
        get => _isDropTarget;
        set => Set(ref _isDropTarget, value);
    }

    public bool IsCustom => Current != null;
    public bool IsPending => Saved?.StorageId != Current?.StorageId;
    public bool HasError => Error != null && !IsProcessing;
    public bool HasCover => Cover != null && !IsProcessing;

    /// <summary>The default music-note tile: the original music, or a custom song without artwork.</summary>
    public bool ShowNoteIcon => !IsProcessing && Cover == null;

    public string Subtitle
    {
        get
        {
            if (IsProcessing)
                return t("custom_music.converting", new { percent = (int)Math.Round(Progress * 100) });
            if (Error != null)
                return Error;
            if (Current is { } entry)
            {
                var artist = string.IsNullOrWhiteSpace(entry.Artist) ? null : entry.Artist;
                return artist == null ? $"♪ {entry.DisplayTitle}" : $"♪ {entry.DisplayTitle} — {artist}";
            }

            return Slot.Detail ?? Slot.FileNames.FirstOrDefault() ?? string.Empty;
        }
    }

    /// <summary>Hover details: which files are written and how loud the song was made.</summary>
    public string Details
    {
        get
        {
            var lines = new List<string> { string.Join(", ", Slot.FileNames.Take(3)) + (Slot.FileNames.Count > 3 ? ", …" : string.Empty) };
            if (Slot.HasFinalLap)
                lines.Add(t("custom_music.details.final_lap", new { file = Slot.FinalLapFileNames[0] }));
            if (Current is { } entry)
            {
                lines.Add(t("custom_music.details.source", new { file = System.IO.Path.GetFileName(entry.SourcePath) }));
                lines.Add(
                    t(
                        "custom_music.details.loudness",
                        new
                        {
                            source = entry.SourceLufs.ToString("0.0"),
                            target = entry.TargetLufs.ToString("0.0"),
                            reference = entry.ReferenceSource switch
                            {
                                MusicReferenceSource.RetroRewind => t("custom_music.reference.retro_rewind"),
                                MusicReferenceSource.GameDisc => t("custom_music.reference.game_disc"),
                                _ => t("custom_music.reference.estimate"),
                            },
                        }
                    )
                );
                if (entry.UsedSourceLoopPoints)
                    lines.Add(t("custom_music.details.loop_points"));
            }

            return string.Join(Environment.NewLine, lines);
        }
    }

    public string SortName => Name;

    public bool Matches(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;
        return Contains(Name) || Contains(Slot.Group) || Contains(Slot.SearchText) || Contains(Current?.Title) || Contains(Current?.Artist);

        bool Contains(string? text) => text?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
    }

    public void UpdateSlot(MusicSlot slot)
    {
        Slot = slot;
        Changed();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        Changed();
    }

    private static readonly string[] AllProperties =
    [
        nameof(Slot),
        nameof(Name),
        nameof(Saved),
        nameof(Current),
        nameof(IsProcessing),
        nameof(Progress),
        nameof(Error),
        nameof(Cover),
        nameof(IsDropTarget),
        nameof(IsCustom),
        nameof(IsPending),
        nameof(HasError),
        nameof(HasCover),
        nameof(ShowNoteIcon),
        nameof(Subtitle),
        nameof(Details),
    ];

    /// <summary>Most properties derive from each other; refreshing them all keeps the bindings simple.</summary>
    private void Changed()
    {
        var handler = PropertyChanged;
        if (handler == null)
            return;
        foreach (var property in AllProperties)
            handler(this, new PropertyChangedEventArgs(property));
    }
}

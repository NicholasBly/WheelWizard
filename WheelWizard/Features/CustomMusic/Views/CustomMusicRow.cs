using System.ComponentModel;
using WheelWizard.CustomMusic.Catalog;

namespace WheelWizard.CustomMusic.Views;

/// <summary>One row of the Custom Music list.</summary>
public sealed class CustomMusicRow(MusicSlot slot, CustomSong? song) : INotifyPropertyChanged
{
    private CustomSong? _song = song;
    private bool _isBusy;
    private bool _isPlaying;
    private bool _isLoadingMusic;
    private string? _error;

    public MusicSlot Slot { get; } = slot;

    public string Name => Slot.Name;
    public bool IsCustom => _song != null;
    public bool HasError => _error != null;

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            _isBusy = value;
            if (value)
                _error = null;
            Changed();
        }
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            _isPlaying = value;
            Changed();
        }
    }

    public bool IsLoadingMusic
    {
        get => _isLoadingMusic;
        set
        {
            _isLoadingMusic = value;
            if (value)
                _error = null;
            Changed();
        }
    }

    public string? Error
    {
        get => _error;
        set
        {
            _error = value;
            Changed();
        }
    }

    public string Subtitle =>
        _error
        ?? (
            _isBusy ? t("custom_music.converting")
            : _song is { } current ? (current.Artist == null ? current.Title : $"{current.Title} · {current.Artist}")
            : Slot.Detail == null ? Slot.Group
            : $"{Slot.Group} · {Slot.Detail}"
        );

    public void SetSong(CustomSong? song)
    {
        _song = song;
        _error = null;
        Changed();
    }

    public bool Matches(string search) =>
        search.Length == 0
        || Name.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Slot.Group.Contains(search, StringComparison.OrdinalIgnoreCase)
        || (_song?.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
        || (_song?.Artist?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Changed()
    {
        foreach (
            var name in new[]
            {
                nameof(IsBusy),
                nameof(IsPlaying),
                nameof(IsLoadingMusic),
                nameof(Error),
                nameof(HasError),
                nameof(IsCustom),
                nameof(Subtitle),
            }
        )
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using WheelWizard.CustomMusic.Catalog;
using WheelWizard.Shared.Desktop.Storage;
using WheelWizard.Views.Dialogs.Base;

namespace WheelWizard.CustomMusic.Views;

/// <summary>Lists the replaceable music; choosing or dropping a song converts and installs it right away.</summary>
public partial class CustomMusicWindow : ModalContent
{
    // The categories shown on each tab: races, battle arenas, menus.
    private static readonly MusicCategory[][] TabCategories =
    [
        [MusicCategory.RetroRewindTrack, MusicCategory.Course],
        [MusicCategory.RetroRewindArena, MusicCategory.Arena],
        [MusicCategory.Menu],
    ];

    private readonly ICustomMusicService _music;
    private readonly IFilePickerService _filePicker;
    private readonly IMusicPlayer _player;
    private List<CustomMusicRow> _rows = [];
    private CustomMusicRow? _playing;
    private int _playRequest;
    private bool _settingsLoaded;

    private static FilePickerFileType SongFiles =>
        new(t("custom_music.file_type"))
        {
            Patterns = MusicConverter.Extensions.Select(extension => "*" + extension).ToArray(),
            AppleUniformTypeIdentifiers = ["public.audio"],
            MimeTypes = ["audio/*"],
        };

    public CustomMusicWindow(ICustomMusicService music, IFilePickerService filePicker, IMusicPlayer player)
        : base(t("custom_music.title"))
    {
        _music = music;
        _filePicker = filePicker;
        _player = player;
        InitializeComponent();
        IsDismissible = true;

        SortBox.ItemsSource = new[]
        {
            t("custom_music.sort.track_order"),
            t("custom_music.sort.name"),
            t("custom_music.sort.changed_first"),
        };
        SortBox.SelectedIndex = 0;
        FinalLapBox.ItemsSource = new[]
        {
            t("custom_music.final_lap.faster"),
            t("custom_music.final_lap.faster_same_pitch"),
            t("custom_music.final_lap.unchanged"),
        };
        Tabs.SelectedIndex = 0;
        MusicList.AddHandler(DragDrop.DragOverEvent, Music_DragOver);
        MusicList.AddHandler(DragDrop.DropEvent, Music_Drop);
        _player.Finished += Player_Finished;
    }

    protected override async void BeforeOpen()
    {
        var settings = _music.LoadSettings();
        MatchVolumeToggle.IsChecked = settings.MatchVolume;
        FinalLapBox.SelectedIndex = (int)settings.FinalLap;
        _settingsLoaded = true;
        try
        {
            var slots = await _music.LoadSlotsAsync();
            var songs = _music.LoadSongs(slots);
            _rows = slots.Select(slot => new CustomMusicRow(slot, songs.GetValueOrDefault(slot.Key))).ToList();
            ShowRows();
        }
        finally
        {
            LoadingSpinner.IsVisible = false;
        }
    }

    protected override void BeforeClose()
    {
        _player.Finished -= Player_Finished;
        StopPlaying();
    }

    private void ShowRows()
    {
        var categories = TabCategories[Math.Max(0, Tabs.SelectedIndex)];
        var search = SearchInput.Text?.Trim() ?? string.Empty;
        var rows = _rows.Where(row => categories.Contains(row.Slot.Category) && row.Matches(search));
        rows = SortBox.SelectedIndex switch
        {
            1 => rows.OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase),
            2 => rows.OrderByDescending(row => row.IsCustom),
            _ => rows,
        };
        MusicList.ItemsSource = rows.ToList();
    }

    private void Filter_Changed(object? sender, SelectionChangedEventArgs e) => ShowRows();

    private void Search_TextChanged(object? sender, TextChangedEventArgs e) => ShowRows();

    private void Settings_Click(object? sender, RoutedEventArgs e)
    {
        SettingsPanel.IsVisible = !SettingsPanel.IsVisible;
        MusicList.IsVisible = !SettingsPanel.IsVisible;
    }

    private void FinalLap_Changed(object? sender, SelectionChangedEventArgs e) => SaveSettings();

    private void MatchVolume_Click(object? sender, RoutedEventArgs e) => SaveSettings();

    private void SaveSettings()
    {
        if (_settingsLoaded)
            _music.SaveSettings(
                new CustomMusicSettings((FinalLapStyle)Math.Max(0, FinalLapBox.SelectedIndex), MatchVolumeToggle.IsChecked == true)
            );
    }

    private void OpenFolder_Click(object? sender, RoutedEventArgs e) => _filePicker.OpenFolderInFileManager(_music.FolderPath);

    private async void Choose_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not CustomMusicRow { IsBusy: false } row)
            return;

        var path = await _filePicker.OpenSingleFileAsync(t("custom_music.pick_title", new { name = row.Name }), [SongFiles]);
        if (path != null)
            await SetSongAsync(row, path);
    }

    private async void Reset_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not CustomMusicRow { IsBusy: false } row)
            return;

        StopPlaying();
        row.IsBusy = true;
        var result = await _music.ResetAsync(row.Slot);
        row.IsBusy = false;
        if (result.IsSuccess)
            row.SetSong(null);
        else
            row.Error = result.Error.Message;
    }

    private async Task SetSongAsync(CustomMusicRow row, string path)
    {
        StopPlaying();
        row.IsBusy = true;
        var result = await _music.SetSongAsync(row.Slot, path);
        row.IsBusy = false;
        if (result.IsSuccess)
            row.SetSong(result.Value);
        else
            row.Error = result.Error.Message;
    }

    private async void Play_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not CustomMusicRow row)
            return;

        var wasPlaying = row.IsPlaying;
        StopPlaying();
        if (wasPlaying)
            return;

        // Only the last row asked for plays, however long reading each one takes.
        var request = ++_playRequest;
        row.IsLoadingMusic = true;
        var music = await _music.ReadMusicAsync(row.Slot);
        row.IsLoadingMusic = false;
        if (request != _playRequest)
            return;
        if (music.IsFailure)
        {
            row.Error = music.Error.Message;
            return;
        }

        try
        {
            _player.Play(music.Value);
        }
        // Damaged files and missing or busy audio devices fail in many different ways.
        catch (Exception exception)
        {
            row.Error = t("custom_music.error.play", new { message = exception.Message });
            return;
        }

        _playing = row;
        row.IsPlaying = true;
    }

    private void StopPlaying()
    {
        _playRequest++;
        _player.Stop();
        if (_playing != null)
            _playing.IsPlaying = false;
        _playing = null;
    }

    private void Player_Finished(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_playing != null)
                _playing.IsPlaying = false;
            _playing = null;
        });

    private static CustomMusicRow? RowAt(DragEventArgs e) => (e.Source as StyledElement)?.DataContext as CustomMusicRow;

    private void Music_DragOver(object? sender, DragEventArgs e) =>
        e.DragEffects =
            RowAt(e) is { IsBusy: false } && e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;

    private async void Music_Drop(object? sender, DragEventArgs e)
    {
        if (RowAt(e) is { IsBusy: false } row && e.DataTransfer.TryGetFile()?.TryGetLocalPath() is { } path)
            await SetSongAsync(row, path);
    }
}

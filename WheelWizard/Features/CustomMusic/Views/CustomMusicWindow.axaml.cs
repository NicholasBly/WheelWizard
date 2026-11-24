using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using WheelWizard.Shared.Desktop.Storage;
using WheelWizard.Views.Dialogs.Base;

namespace WheelWizard.CustomMusic.Views;

/// <summary>Lists the replaceable music; choosing a song converts and installs it right away.</summary>
public partial class CustomMusicWindow : ModalContent
{
    private readonly ICustomMusicService _music;
    private readonly IFilePickerService _filePicker;
    private List<CustomMusicRow> _rows = [];

    private static FilePickerFileType SongFiles =>
        new(t("custom_music.file_type"))
        {
            Patterns = MusicConverter.Extensions.Select(extension => "*" + extension).ToArray(),
            AppleUniformTypeIdentifiers = ["public.audio"],
            MimeTypes = ["audio/*"],
        };

    public CustomMusicWindow(ICustomMusicService music, IFilePickerService filePicker)
        : base(t("custom_music.title"))
    {
        _music = music;
        _filePicker = filePicker;
        InitializeComponent();
        IsDismissible = true;
    }

    protected override async void BeforeOpen()
    {
        try
        {
            var slots = await _music.LoadSlotsAsync();
            var songs = _music.LoadSongs();
            _rows = slots.Select(slot => new CustomMusicRow(slot, songs.GetValueOrDefault(slot.Key))).ToList();
            ShowRows();
        }
        finally
        {
            LoadingSpinner.IsVisible = false;
        }
    }

    private void ShowRows()
    {
        var search = SearchInput.Text?.Trim() ?? string.Empty;
        MusicList.ItemsSource = _rows.Where(row => row.Matches(search)).ToList();
    }

    private void Search_TextChanged(object? sender, TextChangedEventArgs e) => ShowRows();

    private async void Choose_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not CustomMusicRow { IsBusy: false } row)
            return;

        var path = await _filePicker.OpenSingleFileAsync(t("custom_music.pick_title", new { name = row.Name }), [SongFiles]);
        if (path == null)
            return;

        row.IsBusy = true;
        var result = await _music.SetSongAsync(row.Slot, path);
        row.IsBusy = false;
        if (result.IsSuccess)
            row.SetSong(result.Value);
        else
            row.Error = result.Error.Message;
    }

    private async void Reset_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not CustomMusicRow { IsBusy: false } row)
            return;

        row.IsBusy = true;
        var result = await _music.ResetAsync(row.Slot);
        row.IsBusy = false;
        if (result.IsSuccess)
            row.SetSong(null);
        else
            row.Error = result.Error.Message;
    }
}

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using WheelWizard.CustomMusic;
using WheelWizard.CustomMusic.Catalog;
using WheelWizard.CustomMusic.Views;
using WheelWizard.Shared;
using WheelWizard.Shared.Desktop.Storage;
using WheelWizard.Views.Components;

namespace WheelWizard.UI.Test;

public class CustomMusicWindowTests
{
    private static readonly MusicSlot Luigi = Slot("game:n_circuit32", "Luigi Circuit", MusicCategory.Course);
    private static readonly MusicSlot Coconut = Slot("game:n_shopping32", "Coconut Mall", MusicCategory.Course);
    private static readonly MusicSlot Block = Slot("game:n_block", "Block Plaza", MusicCategory.Arena);
    private static readonly MusicSlot Title = Slot("menu:title_bg", "Title screen", MusicCategory.Menu);

    private readonly ICustomMusicService _music = Substitute.For<ICustomMusicService>();
    private readonly IFilePickerService _picker = Substitute.For<IFilePickerService>();
    private readonly IMusicPlayer _player = Substitute.For<IMusicPlayer>();

    public CustomMusicWindowTests()
    {
        IReadOnlyList<MusicSlot> slots = [Luigi, Coconut, Block, Title];
        _music.LoadSlotsAsync(Arg.Any<CancellationToken>()).Returns(slots);
        _music
            .LoadSongs(Arg.Any<IEnumerable<MusicSlot>>())
            .Returns(new Dictionary<string, CustomSong> { [Coconut.Key] = new("Theme", "Band"), [Title.Key] = new("Intro", null) });
        _music.LoadSettings().Returns(new CustomMusicSettings());
    }

    [AvaloniaFact]
    public void Window_ShowsEachTab_SortsAndSearches_AndChangesSongs()
    {
        _music.SetSongAsync(Luigi, "/songs/new.mp3", Arg.Any<CancellationToken>()).Returns(OperationResult.Ok(new CustomSong("New", null)));
        _music.ResetAsync(Coconut).Returns(OperationResult.Ok());
        _picker.OpenSingleFileAsync(Arg.Any<string>(), Arg.Any<IEnumerable<FilePickerFileType>>()).Returns("/songs/new.mp3");
        using var owner = new UiTestHost();
        var window = Open(owner);
        var list = window.FindControl<ListBox>("MusicList")!;
        var tabs = window.FindControl<SegmentedControl>("Tabs")!;
        var sort = window.FindControl<Dropdown>("SortBox")!;

        Assert.False(window.FindControl<Control>("LoadingSpinner")!.IsVisible);
        Assert.Equal(["Luigi Circuit", "Coconut Mall"], Names(list));
        Assert.Equal("Theme · Band", Rows(list)[1].Subtitle);
        sort.SelectedIndex = 1;
        Assert.Equal(["Coconut Mall", "Luigi Circuit"], Names(list));
        sort.SelectedIndex = 2;
        Assert.Equal(["Coconut Mall", "Luigi Circuit"], Names(list));
        sort.SelectedIndex = 0;
        window.FindControl<InputField>("SearchInput")!.Text = "band";
        Layout(owner);
        Assert.Equal(["Coconut Mall"], Names(list));
        window.FindControl<InputField>("SearchInput")!.Text = "";
        tabs.SelectedIndex = 1;
        Assert.Equal(["Block Plaza"], Names(list));
        tabs.SelectedIndex = 2;
        Assert.Equal(["Title screen"], Names(list));
        tabs.SelectedIndex = 0;
        Layout(owner);

        // Each row has a play button, a reset button shown only for custom songs, and a choose button.
        Assert.False(Buttons(list, 0)[1].IsVisible);
        Click(Buttons(list, 0)[2]);
        Click(Buttons(list, 1)[1]);
        Assert.Equal("New", Rows(list)[0].Subtitle);
        Assert.True(Buttons(list, 0)[1].IsVisible);
        Assert.False(Rows(list)[1].IsCustom);
    }

    [AvaloniaFact]
    public void Window_PlaysTheMusic_AndSavesTheSettings()
    {
        byte[] brstm = [1, 2, 3];
        _music.ReadMusicAsync(Luigi).Returns(OperationResult.Ok(brstm));
        _music.FolderPath.Returns("/patches/Custom Music");
        using var owner = new UiTestHost();
        var window = Open(owner);
        var list = window.FindControl<ListBox>("MusicList")!;

        Click(Buttons(list, 0)[0]);
        _player.Received(1).Play(brstm);
        Assert.True(Rows(list)[0].IsPlaying);
        _player.Finished += Raise.Event();
        Dispatcher.UIThread.RunJobs();
        Assert.False(Rows(list)[0].IsPlaying);

        Click(window.FindControl<ActionButton>("SettingsButton")!);
        Assert.True(window.FindControl<Control>("SettingsPanel")!.IsVisible);
        Assert.False(list.IsVisible);
        var matchVolume = window.FindControl<ToggleCheckBox>("MatchVolumeToggle")!;
        Assert.True(matchVolume.IsChecked);
        matchVolume.IsChecked = false;
        Click(matchVolume);
        _music.Received().SaveSettings(new CustomMusicSettings(FinalLapStyle.Faster, MatchVolume: false));
        window.FindControl<Dropdown>("FinalLapBox")!.SelectedIndex = 1;
        _music.Received().SaveSettings(new CustomMusicSettings(FinalLapStyle.FasterSamePitch, MatchVolume: false));
    }

    private CustomMusicWindow Open(UiTestHost owner)
    {
        owner.Show();
        var window = new CustomMusicWindow(_music, _picker, _player);
        window.SetModalOwner(owner);
        window.Show();
        Layout(owner);
        return window;
    }

    private static MusicSlot Slot(string key, string name, MusicCategory category) =>
        new()
        {
            Key = key,
            Name = name,
            Category = category,
            Group = "Cup",
            FileNames = [key + ".brstm"],
        };

    private static List<CustomMusicRow> Rows(ListBox list) => list.ItemsSource!.Cast<CustomMusicRow>().ToList();

    private static IEnumerable<string> Names(ListBox list) => Rows(list).Select(row => row.Name);

    private static ActionButton[] Buttons(ListBox list, int index) =>
        list.ContainerFromIndex(index)!.GetVisualDescendants().OfType<ActionButton>().ToArray();

    private static void Click(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static void Layout(Window owner)
    {
        owner.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        owner.UpdateLayout();
    }
}

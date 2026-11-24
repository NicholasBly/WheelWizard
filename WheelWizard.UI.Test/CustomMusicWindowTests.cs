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
    [AvaloniaFact]
    public void Window_ListsAndSearchesTheMusic_AndChangesSongs()
    {
        var luigi = Slot("game:n_circuit32", "Luigi Circuit", "Mushroom Cup");
        var title = Slot("menu:title_bg", "Title screen", "Menus");
        IReadOnlyList<MusicSlot> slots = [luigi, title];
        var music = Substitute.For<ICustomMusicService>();
        music.LoadSlotsAsync(Arg.Any<CancellationToken>()).Returns(slots);
        music.LoadSongs().Returns(new Dictionary<string, CustomSong> { [title.Key] = new("Theme", "Band") });
        music.SetSongAsync(luigi, "/songs/new.mp3", Arg.Any<CancellationToken>()).Returns(OperationResult.Ok(new CustomSong("New", null)));
        music.ResetAsync(title).Returns(OperationResult.Ok());
        var picker = Substitute.For<IFilePickerService>();
        picker.OpenSingleFileAsync(Arg.Any<string>(), Arg.Any<IEnumerable<FilePickerFileType>>()).Returns("/songs/new.mp3");
        using var owner = new UiTestHost();
        owner.Show();

        var window = new CustomMusicWindow(music, picker);
        window.SetModalOwner(owner);
        window.Show();
        Layout(owner);

        var list = window.FindControl<ListBox>("MusicList")!;
        var search = window.FindControl<InputField>("SearchInput")!;
        Assert.False(window.FindControl<Control>("LoadingSpinner")!.IsVisible);
        Assert.Equal(["Luigi Circuit", "Title screen"], Rows(list).Select(row => row.Name));
        Assert.Equal("Theme · Band", Rows(list)[1].Subtitle);

        search.Text = "band";
        Layout(owner);
        Assert.Equal(["Title screen"], Rows(list).Select(row => row.Name));
        search.Text = "";
        Layout(owner);

        // Each row has a reset button, shown only for custom songs, and a choose button.
        Assert.False(Buttons(list, 0)[0].IsVisible);
        Click(Buttons(list, 0)[1]);
        Click(Buttons(list, 1)[0]);
        Assert.Equal("New", Rows(list)[0].Subtitle);
        Assert.True(Buttons(list, 0)[0].IsVisible);
        Assert.False(Rows(list)[1].IsCustom);
    }

    private static MusicSlot Slot(string key, string name, string group) =>
        new()
        {
            Key = key,
            Name = name,
            Category = MusicCategory.Menu,
            Group = group,
            FileNames = [key + ".brstm"],
        };

    private static List<CustomMusicRow> Rows(ListBox list) => list.ItemsSource!.Cast<CustomMusicRow>().ToList();

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

using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging.Abstractions;
using Testably.Abstractions.Testing;
using WheelWizard.ApplicationData;
using WheelWizard.CustomMusic;
using WheelWizard.CustomMusic.Catalog;
using WheelWizard.DiscImages;
using WheelWizard.Models.Mods;
using WheelWizard.Mods;
using WheelWizard.Settings;
using WheelWizard.Settings.Types;
using static WheelWizard.Localization.TranslationFunctions;

namespace WheelWizard.Test.Features.CustomMusic;

public class CustomMusicServiceTests
{
    private static readonly Dictionary<string, MusicSlot> Slots = GameMusicCatalog.Build().ToDictionary(slot => slot.Key);

    private readonly MockFileSystem _fs = new();
    private readonly IModManager _mods = Substitute.For<IModManager>();
    private readonly IDiscImageService _discs = Substitute.For<IDiscImageService>();
    private string _gamePath = "";
    private readonly ObservableCollection<Mod> _modList = [];
    private readonly string _songFolder;
    private readonly string _modFolder;
    private readonly Func<CustomMusicService> _createService;
    private readonly CustomMusicService _service;

    public CustomMusicServiceTests()
    {
        var location = Substitute.For<IApplicationDataLocation>();
        location.DirectoryPath.Returns(_fs.Path.Combine(Path.GetTempPath(), "custom-music-tests", Guid.NewGuid().ToString("N")));
        var paths = new ModPaths(location, _fs);
        _modFolder = paths.GetModDirectoryPath(CustomMusicService.ModTitle);
        _songFolder = _fs.Path.Combine(location.DirectoryPath, "Songs");
        _fs.Directory.CreateDirectory(_songFolder);

        _mods.Mods.Returns(_modList);
        _mods.ReloadAsync().Returns(Ok());
        _mods.SaveModsAsync().Returns(Ok());
        _mods.GetLowestActivePriority().Returns(_ => _modList.Count == 0 ? 0 : _modList.Min(mod => mod.Priority));
        _mods
            .AddModAsync(Arg.Any<Mod>())
            .Returns(call =>
            {
                _modList.Add(call.Arg<Mod>());
                return Ok();
            });
        _mods
            .DeleteModAsync(Arg.Any<Mod>())
            .Returns(call =>
            {
                _fs.Directory.Delete(_modFolder, recursive: true);
                _modList.Remove(call.Arg<Mod>());
                return Ok();
            });

        var settings = Substitute.For<ISettingsManager>();
        var gameLocation = new WhWzSetting<string>("GameLocation", "");
        settings.GAME_LOCATION.Returns(gameLocation);
        settings.Get(gameLocation).Returns(_ => _gamePath);

        _createService = () =>
            new CustomMusicService(
                Substitute.For<IMusicCatalogService>(),
                _mods,
                paths,
                location,
                _discs,
                settings,
                _fs,
                NullLogger<CustomMusicService>.Instance
            );
        _service = _createService();
    }

    [Fact]
    public async Task SetSong_InstallsBothLapsInAnEnabledTopPriorityPatch()
    {
        _modList.Add(
            new Mod
            {
                Title = "Music Pack",
                Priority = 3,
                IsEnabled = true,
            }
        );

        var result = await _service.SetSongAsync(Slots["game:n_circuit32"], Song("My Song.wav"));

        Assert.True(result.IsSuccess);
        Assert.Equal(new CustomSong("My Song", null), result.Value); // the file name stands in for a missing title tag
        var mod = Assert.Single(_modList, m => m.Title == CustomMusicService.ModTitle);
        Assert.True(mod.IsEnabled);
        Assert.Equal(2, mod.Priority); // beats the music pack (lower number wins)
        var normal = _fs.File.ReadAllBytes(_fs.Path.Combine(_modFolder, "n_Circuit32_n.brstm"));
        var finalLap = _fs.File.ReadAllBytes(_fs.Path.Combine(_modFolder, "n_Circuit32_f.brstm"));
        Assert.True(finalLap.Length < normal.Length); // the final lap plays faster, so it is shorter
        Assert.Equal(new CustomSong("My Song", null), _service.LoadSongs(Slots.Values)["game:n_circuit32"]);
    }

    [Fact]
    public async Task SetSong_ForMenuMusic_WritesOneFile()
    {
        await _service.SetSongAsync(Slots["menu:title_bg"], Song("Menu.wav"));

        Assert.Equal(["title_bg.brstm"], _fs.Directory.EnumerateFiles(_modFolder, "*.brstm").Select(_fs.Path.GetFileName));
    }

    [Fact]
    public async Task SetSong_LeavesEverythingAsItWas_WhenTheSongCannotBeUsed()
    {
        var broken = _fs.Path.Combine(_songFolder, "broken.mp3");
        _fs.File.WriteAllBytes(broken, new byte[50_000]);
        var slot = Slots["game:n_circuit32"];

        Assert.Equal(t("custom_music.error.unsupported"), (await _service.SetSongAsync(slot, Song("song.wav", ".wma"))).Error?.Message);
        Assert.Equal(
            t("custom_music.error.missing"),
            (await _service.SetSongAsync(slot, _fs.Path.Combine(_songFolder, "gone.wav"))).Error?.Message
        );
        Assert.Equal(t("custom_music.error.unreadable"), (await _service.SetSongAsync(slot, broken)).Error?.Message);

        Assert.Empty(_modList);
        Assert.False(_fs.Directory.Exists(_modFolder));
        Assert.Empty(_service.LoadSongs(Slots.Values));
    }

    [Fact]
    public async Task SetSong_ReplacesTheOldSong_AndTurnsADisabledPatchBackOn()
    {
        var slot = Slots["game:n_circuit32"];
        await _service.SetSongAsync(slot, Song("First.wav"));
        var mod = _modList.Single();
        mod.IsEnabled = false;

        await _service.SetSongAsync(slot, Song("Second.wav"));

        Assert.True(mod.IsEnabled);
        await _mods.Received(1).SaveModsAsync();
        Assert.Equal("Second", Assert.Single(_service.LoadSongs(Slots.Values)).Value.Title);
    }

    [Fact]
    public async Task Reset_RemovesTheSong_AndThePatchWithTheLastSong()
    {
        await _service.SetSongAsync(Slots["game:n_circuit32"], Song("One.wav"));
        await _service.SetSongAsync(Slots["menu:title_bg"], Song("Two.wav"));

        Assert.True((await _service.ResetAsync(Slots["game:n_circuit32"])).IsSuccess);

        Assert.False(_fs.File.Exists(_fs.Path.Combine(_modFolder, "n_Circuit32_n.brstm")));
        Assert.False(_fs.File.Exists(_fs.Path.Combine(_modFolder, "n_Circuit32_f.brstm")));
        Assert.Single(_modList);
        Assert.Equal(["menu:title_bg"], _service.LoadSongs(Slots.Values).Keys);

        Assert.True((await _service.ResetAsync(Slots["menu:title_bg"])).IsSuccess);

        Assert.Empty(_modList);
        Assert.Empty(_service.LoadSongs(Slots.Values));
    }

    [Fact]
    public async Task DeletingThePatch_ForgetsItsSongs()
    {
        await _service.SetSongAsync(Slots["game:n_circuit32"], Song("Song.wav"));

        await _mods.DeleteModAsync(_modList.Single());

        Assert.Empty(_service.LoadSongs(Slots.Values));
    }

    [Fact]
    public void MusicInThePatchFolder_CountsAsCustom_EvenWithoutAName()
    {
        _fs.Directory.CreateDirectory(_modFolder);
        _fs.File.WriteAllBytes(_fs.Path.Combine(_modFolder, "n_Circuit32_n.brstm"), [1, 2, 3]);

        Assert.Equal(new CustomSong(t("custom_music.unknown_song"), null), Assert.Single(_service.LoadSongs(Slots.Values)).Value);
    }

    [Fact]
    public async Task Settings_AreSaved_AndUsedForNewSongs()
    {
        var settings = new CustomMusicSettings(FinalLapStyle.Unchanged, MatchVolume: false);

        _service.SaveSettings(settings);
        await _service.SetSongAsync(Slots["game:n_circuit32"], Song("Song.wav"));

        Assert.Equal(settings, _createService().LoadSettings());
        Assert.Equal(
            _fs.File.ReadAllBytes(_fs.Path.Combine(_modFolder, "n_Circuit32_n.brstm")),
            _fs.File.ReadAllBytes(_fs.Path.Combine(_modFolder, "n_Circuit32_f.brstm"))
        );
    }

    [Fact]
    public async Task NewSongs_MatchTheVolumeOfTheOriginalMusic()
    {
        var original = _fs.Path.Combine(_songFolder, "original.brstm");
        var quiet = MusicConverter.Convert(MusicConverterTests.Wav(32000, 2, 10, amplitude: 0.1), ".wav", new ConvertOptions(false)).Normal;
        _fs.File.WriteAllBytes(original, quiet);
        var slot = Slots["menu:title_bg"] with { RetroRewindFile = original };

        await _service.SetSongAsync(slot, Song("Loud.wav"));

        var installed = MusicConverterTests.ReadBrstm(_fs.File.ReadAllBytes(_fs.Path.Combine(_modFolder, "title_bg.brstm")));
        Assert.InRange(
            MusicConverterTests.LoudnessOf(installed),
            MusicConverter.MeasureLoudness(quiet, ".brstm") - 0.5,
            MusicConverter.MeasureLoudness(quiet, ".brstm") + 0.5
        );
    }

    [Fact]
    public async Task ReadMusic_GivesTheCustomSong_OrElseTheOriginalMusic()
    {
        var slot = Slots["game:n_circuit32"];
        Assert.Equal(t("custom_music.error.no_game"), (await _service.ReadMusicAsync(slot)).Error?.Message);
        Assert.Equal(t("custom_music.error.no_original"), (await _service.ReadMusicAsync(slot with { GameFile = null })).Error?.Message);

        _gamePath = "/games/mkwii.rvz";
        byte[] disc = [1, 2, 3];
        _discs.ReadFile("/games/mkwii.rvz", "sound/strm/n_Circuit32_n.brstm").Returns(Ok(disc));
        Assert.Equal(disc, (await _service.ReadMusicAsync(slot)).Value);

        await _service.SetSongAsync(slot, Song("Song.wav"));
        Assert.Equal(
            _fs.File.ReadAllBytes(_fs.Path.Combine(_modFolder, "n_Circuit32_n.brstm")),
            (await _service.ReadMusicAsync(slot)).Value
        );
    }

    private string Song(string name, string? extension = null)
    {
        var path = _fs.Path.Combine(_songFolder, extension == null ? name : _fs.Path.ChangeExtension(name, extension));
        _fs.File.WriteAllBytes(path, MusicConverterTests.Wav(32000, channels: 2, seconds: 2));
        return path;
    }
}

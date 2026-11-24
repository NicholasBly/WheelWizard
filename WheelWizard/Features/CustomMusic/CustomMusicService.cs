using System.IO.Abstractions;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WheelWizard.ApplicationData;
using WheelWizard.CustomMusic.Catalog;
using WheelWizard.DiscImages;
using WheelWizard.Models.Mods;
using WheelWizard.Mods;
using WheelWizard.Settings;

namespace WheelWizard.CustomMusic;

/// <summary>A song the player put on a music slot.</summary>
public sealed record CustomSong(string Title, string? Artist);

/// <summary>How new songs are converted.</summary>
public sealed record CustomMusicSettings(FinalLapStyle FinalLap = FinalLapStyle.Faster, bool MatchVolume = true);

public interface ICustomMusicService
{
    /// <summary>The "Custom Music" patch folder with the converted music, or the folder it will go in until there is any.</summary>
    string FolderPath { get; }

    Task<IReadOnlyList<MusicSlot>> LoadSlotsAsync(CancellationToken cancellationToken = default);

    /// <summary>The slots that have custom music in the patch, by slot key.</summary>
    IReadOnlyDictionary<string, CustomSong> LoadSongs(IEnumerable<MusicSlot> slots);

    CustomMusicSettings LoadSettings();

    void SaveSettings(CustomMusicSettings settings);

    /// <summary>Converts the song and installs it for the slot, in the "Custom Music" patch.</summary>
    Task<OperationResult<CustomSong>> SetSongAsync(MusicSlot slot, string songPath, CancellationToken cancellationToken = default);

    /// <summary>Puts the slot back to its original music, removing the patch when no songs are left.</summary>
    Task<OperationResult> ResetAsync(MusicSlot slot);

    /// <summary>The music that plays for the slot: its custom song, or else the original music.</summary>
    Task<OperationResult<byte[]>> ReadMusicAsync(MusicSlot slot);
}

/// <summary>Installs songs as a normal patch, which Wheel Wizard copies into Retro Rewind's Patches folder at launch.</summary>
public sealed class CustomMusicService(
    IMusicCatalogService catalog,
    IModManager mods,
    IModPaths modPaths,
    IApplicationDataLocation applicationData,
    IDiscImageService discImages,
    ISettingsManager settings,
    IFileSystem fileSystem,
    ILogger<CustomMusicService> logger
) : ICustomMusicService
{
    public const string ModTitle = "Custom Music";
    private const long MaxFileBytes = 300L * 1024 * 1024;

    // The song names, by slot key, and the files written for each. The patch folder holds the music itself.
    private sealed record SavedSong(string Title, string? Artist, string[] Files);

    private sealed record SavedState(CustomMusicSettings Settings, Dictionary<string, SavedSong> Songs);

    private readonly SemaphoreSlim _lock = new(1, 1);

    public string FolderPath => fileSystem.Directory.Exists(ModFolder) ? ModFolder : fileSystem.Path.GetDirectoryName(ModFolder)!;
    private string ModFolder => modPaths.GetModDirectoryPath(ModTitle);
    private string StateFile => fileSystem.Path.Combine(applicationData.DirectoryPath, "CustomMusic.json");

    public Task<IReadOnlyList<MusicSlot>> LoadSlotsAsync(CancellationToken cancellationToken = default) =>
        catalog.LoadAsync(cancellationToken);

    // Music in the patch folder counts even without a saved name, e.g. files put there by hand.
    public IReadOnlyDictionary<string, CustomSong> LoadSongs(IEnumerable<MusicSlot> slots)
    {
        var saved = ReadState().Songs;
        return slots
            .Where(slot => fileSystem.File.Exists(fileSystem.Path.Combine(ModFolder, slot.FileNames[0])))
            .ToDictionary(
                slot => slot.Key,
                slot =>
                    saved.TryGetValue(slot.Key, out var song)
                        ? new CustomSong(song.Title, song.Artist)
                        : new CustomSong(t("custom_music.unknown_song"), null)
            );
    }

    public CustomMusicSettings LoadSettings() => ReadState().Settings;

    public void SaveSettings(CustomMusicSettings newSettings)
    {
        _lock.Wait();
        try
        {
            WriteState(ReadState() with { Settings = newSettings });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not save the custom music settings.");
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<OperationResult<CustomSong>> SetSongAsync(
        MusicSlot slot,
        string songPath,
        CancellationToken cancellationToken = default
    )
    {
        var extension = fileSystem.Path.GetExtension(songPath);
        if (!MusicConverter.IsSupported(extension))
            return Fail(t("custom_music.error.unsupported"));
        if (!fileSystem.File.Exists(songPath))
            return Fail(t("custom_music.error.missing"));
        if (fileSystem.FileInfo.New(songPath).Length > MaxFileBytes)
            return Fail(t("custom_music.error.too_long"));

        var options = LoadSettings();
        ConvertedMusic music;
        try
        {
            var file = await fileSystem.File.ReadAllBytesAsync(songPath, cancellationToken);
            music = await Task.Run(
                () =>
                {
                    double? target = options.MatchVolume ? OriginalLoudness(slot) : null;
                    return MusicConverter.Convert(
                        file,
                        extension,
                        new ConvertOptions(slot.HasFinalLap, options.FinalLap, target),
                        cancellationToken
                    );
                },
                cancellationToken
            );
        }
        catch (SongTooLongException)
        {
            return Fail(t("custom_music.error.too_long"));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The decoders throw many kinds of exceptions on damaged files.
            logger.LogWarning(exception, "Could not convert {Song} for custom music.", songPath);
            return Fail(t("custom_music.error.unreadable"));
        }

        var song = new CustomSong(music.Title ?? fileSystem.Path.GetFileNameWithoutExtension(songPath), music.Artist);
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var modResult = await EnsureModAsync();
            if (modResult.IsFailure)
                return modResult.Error;

            fileSystem.Directory.CreateDirectory(ModFolder);
            var state = ReadState();
            if (state.Songs.TryGetValue(slot.Key, out var previous))
                DeleteFiles(previous.Files);
            foreach (var name in slot.FileNames)
                await fileSystem.File.WriteAllBytesAsync(fileSystem.Path.Combine(ModFolder, name), music.Normal, CancellationToken.None);
            foreach (var name in slot.FinalLapFileNames)
                await fileSystem.File.WriteAllBytesAsync(
                    fileSystem.Path.Combine(ModFolder, name),
                    music.FinalLap ?? music.Normal,
                    CancellationToken.None
                );

            state.Songs[slot.Key] = new SavedSong(song.Title, song.Artist, slot.AllFileNames.ToArray());
            WriteState(state);
            return song;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogError(exception, "Could not install custom music for {Slot}.", slot.Key);
            return new OperationError
            {
                Message = t("custom_music.error.write", new { message = exception.Message }),
                Exception = exception,
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<OperationResult> ResetAsync(MusicSlot slot)
    {
        await _lock.WaitAsync();
        try
        {
            var state = ReadState();
            DeleteFiles(state.Songs.Remove(slot.Key, out var song) ? song.Files.Union(slot.AllFileNames) : slot.AllFileNames);
            WriteState(state);

            var mod = FindMod();
            var hasMusic = fileSystem.Directory.Exists(ModFolder) && fileSystem.Directory.EnumerateFiles(ModFolder, "*.brstm").Any();
            return mod != null && !hasMusic ? await mods.DeleteModAsync(mod) : Ok();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogError(exception, "Could not remove custom music for {Slot}.", slot.Key);
            return new OperationError
            {
                Message = t("custom_music.error.write", new { message = exception.Message }),
                Exception = exception,
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task<OperationResult<byte[]>> ReadMusicAsync(MusicSlot slot)
    {
        var custom = fileSystem.Path.Combine(ModFolder, slot.FileNames[0]);
        return Task.Run(() => fileSystem.File.Exists(custom) ? ReadFile(custom) : ReadOriginal(slot));
    }

    /// <summary>The original music: Retro Rewind's own file, or the file on the player's game disc.</summary>
    private OperationResult<byte[]> ReadOriginal(MusicSlot slot)
    {
        if (slot.RetroRewindFile != null)
            return ReadFile(slot.RetroRewindFile);
        if (slot.GameFile == null)
            return Fail(t("custom_music.error.no_original"));

        var gamePath = settings.Get(settings.GAME_LOCATION);
        if (string.IsNullOrWhiteSpace(gamePath))
            return Fail(t("custom_music.error.no_game"));
        return discImages.ReadFile(gamePath, "sound/strm/" + slot.GameFile);
    }

    private OperationResult<byte[]> ReadFile(string path)
    {
        try
        {
            return fileSystem.File.ReadAllBytes(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new OperationError { Message = exception.Message, Exception = exception };
        }
    }

    /// <summary>How loud the music being replaced is, so the new song can match it.</summary>
    private double OriginalLoudness(MusicSlot slot)
    {
        try
        {
            var original = ReadOriginal(slot);
            var loudness = original.IsSuccess ? MusicConverter.MeasureLoudness(original.Value, ".brstm") : double.NaN;
            if (double.IsFinite(loudness))
                return loudness;
        }
        // The original is only a measuring stick; a typical volume stands in when it cannot be read.
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not measure the original music of {Slot}.", slot.Key);
        }

        return MusicConverter.DefaultLoudness;
    }

    /// <summary>Creates the patch with the highest priority, so the songs chosen here beat music packs, and keeps it on.</summary>
    private async Task<OperationResult> EnsureModAsync()
    {
        // The Patches page loads the list in the background; never add a second "Custom Music" next to an unloaded one.
        if (FindMod() == null && await mods.ReloadAsync() is { IsFailure: true } reload)
            return reload;

        var mod = FindMod();
        if (mod == null)
        {
            return await mods.AddModAsync(
                new Mod
                {
                    Title = ModTitle,
                    Author = "Wheel Wizard",
                    ModID = -1,
                    IsEnabled = true,
                    // Lowest number = highest priority.
                    Priority = mods.Mods.Count == 0 ? 0 : mods.GetLowestActivePriority() - 1,
                }
            );
        }

        if (mod.IsEnabled)
            return Ok();
        mod.IsEnabled = true;
        return await mods.SaveModsAsync();
    }

    private Mod? FindMod() => mods.Mods.FirstOrDefault(mod => mod.Title.Equals(ModTitle, StringComparison.OrdinalIgnoreCase));

    private void DeleteFiles(IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            var path = fileSystem.Path.Combine(ModFolder, name);
            if (fileSystem.File.Exists(path))
                fileSystem.File.Delete(path);
        }
    }

    private SavedState ReadState()
    {
        try
        {
            if (
                fileSystem.File.Exists(StateFile)
                && JsonSerializer.Deserialize<SavedState>(fileSystem.File.ReadAllText(StateFile)) is { } state
            )
                return state with { Settings = state.Settings ?? new(), Songs = state.Songs ?? [] };
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(
                exception,
                "Could not read {File}; the default settings are used and custom songs show without a name.",
                StateFile
            );
        }

        return new SavedState(new(), []);
    }

    private void WriteState(SavedState state) => fileSystem.File.WriteAllText(StateFile, JsonSerializer.Serialize(state));
}

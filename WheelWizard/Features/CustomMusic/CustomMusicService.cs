using System.IO.Abstractions;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WheelWizard.ApplicationData;
using WheelWizard.CustomMusic.Catalog;
using WheelWizard.Models.Mods;
using WheelWizard.Mods;

namespace WheelWizard.CustomMusic;

/// <summary>A song the player put on a music slot.</summary>
public sealed record CustomSong(string Title, string? Artist);

public interface ICustomMusicService
{
    Task<IReadOnlyList<MusicSlot>> LoadSlotsAsync(CancellationToken cancellationToken = default);

    /// <summary>The installed songs, by slot key.</summary>
    IReadOnlyDictionary<string, CustomSong> LoadSongs();

    /// <summary>Converts the song and installs it for the slot, in the "Custom Music" patch.</summary>
    Task<OperationResult<CustomSong>> SetSongAsync(MusicSlot slot, string songPath, CancellationToken cancellationToken = default);

    /// <summary>Puts the slot back to its original music, removing the patch when no songs are left.</summary>
    Task<OperationResult> ResetAsync(MusicSlot slot);
}

/// <summary>
/// Installs songs as a normal patch: the converted files sit in the "Custom Music" patch folder, which Wheel Wizard copies
/// into Retro Rewind's Patches folder at launch, where they replace the game's music by file name.
/// </summary>
public sealed class CustomMusicService(
    IMusicCatalogService catalog,
    IModManager mods,
    IModPaths modPaths,
    IApplicationDataLocation applicationData,
    IFileSystem fileSystem,
    ILogger<CustomMusicService> logger
) : ICustomMusicService
{
    public const string ModTitle = "Custom Music";
    private const long MaxFileBytes = 300L * 1024 * 1024;

    // The song names, by slot key, and the files written for each. The patch folder holds the music itself.
    private sealed record SavedSong(string Title, string? Artist, string[] Files);

    private readonly SemaphoreSlim _lock = new(1, 1);

    private string ModFolder => modPaths.GetModDirectoryPath(ModTitle);
    private string SongsFile => fileSystem.Path.Combine(applicationData.DirectoryPath, "CustomMusic.json");

    public Task<IReadOnlyList<MusicSlot>> LoadSlotsAsync(CancellationToken cancellationToken = default) =>
        catalog.LoadAsync(cancellationToken);

    // A song counts as installed while its files are still in the patch (deleting the patch removes them all).
    public IReadOnlyDictionary<string, CustomSong> LoadSongs() =>
        ReadSongs()
            .Where(pair => pair.Value.Files.Length > 0 && fileSystem.File.Exists(fileSystem.Path.Combine(ModFolder, pair.Value.Files[0])))
            .ToDictionary(pair => pair.Key, pair => new CustomSong(pair.Value.Title, pair.Value.Artist));

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

        ConvertedMusic music;
        try
        {
            var file = await fileSystem.File.ReadAllBytesAsync(songPath, cancellationToken);
            music = await Task.Run(() => MusicConverter.Convert(file, extension, slot.HasFinalLap, cancellationToken), cancellationToken);
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
            var songs = ReadSongs();
            if (songs.TryGetValue(slot.Key, out var previous))
                DeleteFiles(previous.Files);
            foreach (var name in slot.FileNames)
                await fileSystem.File.WriteAllBytesAsync(fileSystem.Path.Combine(ModFolder, name), music.Normal, CancellationToken.None);
            foreach (var name in slot.FinalLapFileNames)
                await fileSystem.File.WriteAllBytesAsync(
                    fileSystem.Path.Combine(ModFolder, name),
                    music.FinalLap ?? music.Normal,
                    CancellationToken.None
                );

            songs[slot.Key] = new SavedSong(song.Title, song.Artist, slot.AllFileNames.ToArray());
            WriteSongs(songs);
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
            var songs = ReadSongs();
            DeleteFiles(songs.Remove(slot.Key, out var song) ? song.Files.Union(slot.AllFileNames) : slot.AllFileNames);
            WriteSongs(songs);

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

    private Dictionary<string, SavedSong> ReadSongs()
    {
        try
        {
            if (fileSystem.File.Exists(SongsFile))
                return JsonSerializer.Deserialize<Dictionary<string, SavedSong>>(fileSystem.File.ReadAllText(SongsFile)) ?? [];
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not read {File}; custom songs are listed as original music.", SongsFile);
        }

        return [];
    }

    private void WriteSongs(Dictionary<string, SavedSong> songs) =>
        fileSystem.File.WriteAllText(SongsFile, JsonSerializer.Serialize(songs));
}

using System.IO.Abstractions;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WheelWizard.CustomMusic.Audio;
using WheelWizard.CustomMusic.Audio.Dsp;
using WheelWizard.CustomMusic.Audio.Formats;
using WheelWizard.CustomMusic.Catalog;
using WheelWizard.Settings;

namespace WheelWizard.CustomMusic.Library;

/// <summary>How loud the music being replaced is, and whether it loops.</summary>
public sealed record MusicReference(double Lufs, bool? Loops, MusicReferenceSource Source);

public interface IMusicReferenceService
{
    /// <summary>
    /// Measures the music that currently plays in <paramref name="slot"/>: Retro Rewind's own BRSTM when it
    /// ships one, otherwise the original file on the player's game disc. Results are cached on disk.
    /// </summary>
    Task<MusicReference> GetReferenceAsync(MusicSlot slot, CancellationToken cancellationToken = default);
}

public sealed class MusicReferenceService(
    IFileSystem fileSystem,
    ICustomMusicLibrary library,
    IGameDiscMusicReader discReader,
    ISettingsManager settings,
    ILogger<MusicReferenceService> logger
) : IMusicReferenceService
{
    private sealed record CachedReference(double Lufs, bool Loops, MusicReferenceSource Source);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private Dictionary<string, CachedReference>? _cache;

    private string CachePath => fileSystem.Path.Combine(library.RootFolderPath, "reference-loudness.json");

    public async Task<MusicReference> GetReferenceAsync(MusicSlot slot, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var cache = LoadCache();

            if (slot.RetroRewindFilePath is { } shipped && fileSystem.File.Exists(shipped))
            {
                var key = Identity("rr", shipped, shipped);
                if (cache.TryGetValue(key, out var cached))
                    return new(cached.Lufs, cached.Loops, cached.Source);

                var measured = await Task.Run(() => Measure(fileSystem.File.ReadAllBytes(shipped)), cancellationToken);
                if (measured is { } result)
                    return Remember(cache, key, result.Lufs, result.Loops, MusicReferenceSource.RetroRewind);
            }

            var gamePath = settings.Get<string>(settings.GAME_LOCATION);
            if (slot.GameFileName is { } gameFile && !string.IsNullOrWhiteSpace(gamePath) && fileSystem.File.Exists(gamePath))
            {
                var key = Identity("disc", gamePath, gameFile);
                if (cache.TryGetValue(key, out var cached))
                    return new(cached.Lufs, cached.Loops, cached.Source);

                var bytes = await Task.Run(() => discReader.ReadStreamFile(gamePath, gameFile), cancellationToken);
                if (bytes != null && Measure(bytes) is { } result)
                    return Remember(cache, key, result.Lufs, result.Loops, MusicReferenceSource.GameDisc);
            }

            return new(EstimateFrom(cache), null, MusicReferenceSource.Estimate);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// For music we cannot measure (menu music is sequenced, not streamed), use the typical loudness of the
    /// music we did measure, so menus end up as loud as the courses.
    /// </summary>
    private static double EstimateFrom(Dictionary<string, CachedReference> cache)
    {
        var measured = cache.Values.Where(value => value.Source != MusicReferenceSource.Estimate).Select(value => value.Lufs).Order().ToList();
        return measured.Count >= 3 ? measured[measured.Count / 2] : MusicLoudness.DefaultTargetLufs;
    }

    private (double Lufs, bool Loops)? Measure(byte[] brstm)
    {
        try
        {
            var info = BrstmReader.ReadInfo(brstm);
            var audio = BrstmReader.Decode(brstm);
            var loudness = LoudnessMeter.Measure(audio);
            if (loudness.IsSilent)
                return null;
            // Pulsar scales the stream by header byte 0x3F, so that is part of what players hear.
            return (loudness.IntegratedLufs + 20 * Math.Log10(info.PulsarGain), info.Looping);
        }
        catch (Exception exception) when (exception is InvalidDataException or IndexOutOfRangeException or ArgumentException)
        {
            logger.LogDebug(exception, "Could not measure a reference BRSTM.");
            return null;
        }
    }

    private MusicReference Remember(Dictionary<string, CachedReference> cache, string key, double lufs, bool loops, MusicReferenceSource source)
    {
        cache[key] = new CachedReference(lufs, loops, source);
        try
        {
            fileSystem.Directory.CreateDirectory(library.RootFolderPath);
            fileSystem.File.WriteAllText(CachePath, JsonSerializer.Serialize(cache));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(exception, "Could not store the reference loudness cache.");
        }

        return new(lufs, loops, source);
    }

    private Dictionary<string, CachedReference> LoadCache()
    {
        if (_cache != null)
            return _cache;

        try
        {
            if (fileSystem.File.Exists(CachePath))
                _cache = JsonSerializer.Deserialize<Dictionary<string, CachedReference>>(fileSystem.File.ReadAllText(CachePath));
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            logger.LogDebug(exception, "Ignoring an unreadable reference loudness cache.");
        }

        return _cache ??= new();
    }

    /// <summary>A cache key that changes whenever the source file changes (new Retro Rewind version, other game dump).</summary>
    private string Identity(string kind, string containerPath, string item)
    {
        var info = fileSystem.FileInfo.New(containerPath);
        return $"{kind}|{containerPath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{item}".ToLowerInvariant();
    }
}

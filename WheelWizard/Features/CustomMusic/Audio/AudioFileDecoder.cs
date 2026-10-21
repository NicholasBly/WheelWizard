using WheelWizard.CustomMusic.Audio.Formats;
using WheelWizard.CustomMusic.Audio.Tags;

namespace WheelWizard.CustomMusic.Audio;

/// <summary>Detects an audio file's format from its contents and decodes it.</summary>
public static class AudioFileDecoder
{
    /// <summary>Formats decoded without any external tools.</summary>
    public static readonly string[] NativeExtensions = [".mp3", ".wav", ".wave", ".flac", ".ogg", ".oga", ".aif", ".aiff", ".aifc", ".brstm"];

    /// <summary>Formats we hand to FFmpeg when the user has it installed.</summary>
    public static readonly string[] FfmpegExtensions = [".m4a", ".mp4", ".aac", ".opus", ".wma", ".alac", ".webm", ".mka", ".ac3", ".mp2"];

    public static IEnumerable<string> AllExtensions => NativeExtensions.Concat(FfmpegExtensions);

    /// <summary>Largest file we are willing to load into memory (a 10 minute 24-bit/96 kHz WAV is ~350 MB).</summary>
    public const long MaxFileSize = 512L * 1024 * 1024;

    public static bool IsSupportedExtension(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return NativeExtensions.Contains(extension) || FfmpegExtensions.Contains(extension);
    }

    public static async Task<AudioBuffer> DecodeAsync(string path, CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException("The audio file could not be found.", path);
        if (info.Length > MaxFileSize)
            throw new InvalidDataException("This audio file is too large to convert.");

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var buffer = await DecodeAsync(bytes, path, cancellationToken);

        // Generic folder art (cover.jpg, ...) only belongs to songs that are part of a tagged album; a loose
        // download in a shared folder would otherwise pick up some other album's cover.
        buffer.Metadata.Cover ??= FolderArtFinder.Find(path, allowAlbumArt: !string.IsNullOrWhiteSpace(buffer.Metadata.Album));
        return buffer;
    }

    public static async Task<AudioBuffer> DecodeAsync(byte[] bytes, string pathForFallback, CancellationToken cancellationToken = default)
    {
        AudioBuffer buffer;
        if (WavReader.IsWav(bytes))
            buffer = await Run(() => WavReader.Read(bytes), pathForFallback, cancellationToken);
        else if (AiffReader.IsAiff(bytes))
            buffer = await Run(() => AiffReader.Read(bytes), pathForFallback, cancellationToken);
        else if (FlacReader.IsFlac(bytes))
            buffer = await Task.Run(() => FlacReader.Read(bytes), cancellationToken);
        else if (OggVorbisReader.IsVorbis(bytes))
            buffer = await Task.Run(() => OggVorbisReader.Read(bytes, cancellationToken), cancellationToken);
        else if (BrstmReader.IsBrstm(bytes))
            buffer = await Task.Run(() => BrstmReader.Decode(bytes), cancellationToken);
        else if (Mp4TagReader.IsMp4(bytes))
        {
            buffer = await FfmpegReader.ReadAsync(pathForFallback, cancellationToken);
            using var stream = new MemoryStream(bytes, writable: false);
            buffer.Metadata = Mp4TagReader.Read(stream);
        }
        else if (OggVorbisReader.IsOgg(bytes))
        {
            // Opus, Ogg FLAC or Speex: no managed decoder; FFmpeg decodes and reads the tags.
            buffer = await FfmpegReader.ReadAsync(pathForFallback, cancellationToken);
            buffer.Metadata = await FfmpegReader.ReadMetadataAsync(pathForFallback, cancellationToken);
        }
        else if (Mp3Reader.LooksLikeMp3(bytes))
            buffer = await Task.Run(() => Mp3Reader.Read(bytes, cancellationToken), cancellationToken);
        else
        {
            buffer = await FfmpegReader.ReadAsync(pathForFallback, cancellationToken);
            buffer.Metadata = await FfmpegReader.ReadMetadataAsync(pathForFallback, cancellationToken);
        }

        if (buffer.Loop == null && buffer.Metadata.ResolveLoopTags(buffer.SampleCount) is { } loop)
            buffer.Loop = loop;

        return buffer;
    }

    /// <summary>Runs a managed reader, falling back to FFmpeg for compressed payloads inside WAV/AIFF containers.</summary>
    private static async Task<AudioBuffer> Run(Func<AudioBuffer> read, string path, CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(read, cancellationToken);
        }
        catch (NotSupportedException) when (FfmpegReader.IsAvailable)
        {
            return await FfmpegReader.ReadAsync(path, cancellationToken);
        }
    }
}

/// <summary>
/// Finds artwork stored next to the song: a picture with the song's own name (song.jpg, as yt-dlp and many
/// rippers save it), or, for songs from an album, the usual album art names (cover.jpg, folder.png, ...).
/// </summary>
public static class FolderArtFinder
{
    private static readonly string[] Names = ["cover", "folder", "front", "album", "albumart", "albumartsmall", "artwork"];
    private static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".webp"];

    /// <param name="audioPath">The song file.</param>
    /// <param name="allowAlbumArt">Whether generic album art names may be used (only when the song is tagged with an album).</param>
    public static CoverArt? Find(string audioPath, bool allowAlbumArt)
    {
        try
        {
            var directory = Path.GetDirectoryName(audioPath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                return null;

            var baseName = Path.GetFileNameWithoutExtension(audioPath);
            var candidates = Directory
                .EnumerateFiles(directory)
                .Where(file => Extensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
                .Select(file => (File: file, Name: Path.GetFileNameWithoutExtension(file).ToLowerInvariant()))
                .ToList();

            // A picture named exactly like the song wins, then (for album tracks) the usual album art names.
            var match = candidates.FirstOrDefault(c => c.Name.Equals(baseName, StringComparison.OrdinalIgnoreCase)).File;
            if (match == null && allowAlbumArt)
                match = Names.Select(name => candidates.FirstOrDefault(c => c.Name == name).File).FirstOrDefault(file => file != null);
            if (match == null)
                return null;

            var info = new FileInfo(match);
            return info.Length is > 0 and < 20 * 1024 * 1024 ? CoverArt.FromBytes(File.ReadAllBytes(match)) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

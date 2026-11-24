using System.IO.Abstractions;
using RVZSharp.Blobs;
using RVZSharp.Files;
using RVZSharp.Interfaces;
using RVZSharp.Models;
using RVZSharp.Wii;
using WheelWizard.DiscImages.Domain;

namespace WheelWizard.DiscImages;

public interface IDiscImageService
{
    /// <summary>File extensions (with dot) of the disc image formats a Wii game can be read from.</summary>
    IReadOnlyList<string> SupportedExtensions { get; }

    /// <summary>Reads the disc header of a game file in any supported format.</summary>
    OperationResult<DiscImageInfo> ReadInfo(string path);

    /// <summary>Reads a file from the game's data partition, e.g. "sound/strm/n_Circuit32_n.brstm".</summary>
    OperationResult<byte[]> ReadFile(string path, string filePath);
}

public sealed class DiscImageService(IFileSystem fileSystem) : IDiscImageService
{
    private readonly object _cacheLock = new();
    private (string Path, long Length, DateTime LastWrite, DiscImageInfo Info)? _cache;

    public IReadOnlyList<string> SupportedExtensions { get; } = Enum.GetValues<BlobType>().SelectMany(ExtensionsFor).ToArray();

    private static string[] ExtensionsFor(BlobType type) =>
        type switch
        {
            BlobType.Plain => [".iso", ".gcm"],
            BlobType.Gcz => [".gcz"],
            BlobType.Ciso => [".ciso"],
            BlobType.Wbfs => [".wbfs"],
            BlobType.Wia => [".wia"],
            BlobType.Rvz => [".rvz"],
            // TGC only wraps GameCube discs, and NFS needs key files from a Wii U title next to it.
            _ => [],
        };

    public OperationResult<DiscImageInfo> ReadInfo(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Fail("No game file is set.");

        try
        {
            var file = fileSystem.FileInfo.New(path);
            if (!file.Exists)
                return Fail($"Game file not found: {path}");

            // The license loader asks on every poll, so only open the image again once the file changes.
            lock (_cacheLock)
            {
                if (
                    _cache is { } cached
                    && cached.Path == path
                    && cached.Length == file.Length
                    && cached.LastWrite == file.LastWriteTimeUtc
                )
                    return cached.Info;
            }

            var result = Read(path);
            // Failures aren't cached: a file that was briefly locked should read fine on the next call.
            if (result.IsSuccess)
            {
                lock (_cacheLock)
                    _cache = (path, file.Length, file.LastWriteTimeUtc, result.Value);
            }
            return result;
        }
        catch (Exception ex)
        {
            return new OperationError { Message = $"Failed to read game file: {ex.Message}", Exception = ex };
        }
    }

    public OperationResult<byte[]> ReadFile(string path, string filePath)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Fail("No game file is set.");

        try
        {
            using var reader = Open(path);
            var partitions = WiiVolume.GetPartitions(reader);
            // Wii discs keep their files in the data partition (type 0); GameCube discs have no partitions.
            using var files =
                partitions.Count == 0
                    ? DiscFileSystem.Open(reader)
                    : DiscFileSystem.Open(reader, partitions.FirstOrDefault(partition => partition.Type == 0, partitions[0]));
            var file = files.Find(filePath);
            if (file is not { IsDirectory: false, Size: > 0 and <= 64 * 1024 * 1024 })
                return Fail($"The game file has no {filePath}.");

            using var output = new MemoryStream((int)file.Size);
            files.CopyFileTo(file, output);
            return output.ToArray();
        }
        catch (Exception ex)
        {
            return new OperationError { Message = $"Failed to read game file: {ex.Message}", Exception = ex };
        }
    }

    private OperationResult<DiscImageInfo> Read(string path)
    {
        try
        {
            using var reader = Open(path);
            var info = DiscInfo.TryRead(reader);
            if (info == null)
                return Fail("This file is not a GameCube or Wii disc image.");

            return new DiscImageInfo(info.GameId, info.InternalName, info.Revision, ToFormat(reader.Type));
        }
        catch (Exception ex)
        {
            return new OperationError { Message = $"Failed to read game file: {ex.Message}", Exception = ex };
        }
    }

    private IBlobReader Open(string path)
    {
        var stream = fileSystem.File.OpenRead(path);
        try
        {
            return Blob.Open(stream, path, leaveOpen: false);
        }
        catch
        {
            // Blob.Open only takes ownership of the stream once it succeeds.
            stream.Dispose();
            throw;
        }
    }

    private static DiscImageFormat ToFormat(BlobType type) =>
        type switch
        {
            BlobType.Gcz => DiscImageFormat.Gcz,
            BlobType.Ciso => DiscImageFormat.Ciso,
            BlobType.Wbfs => DiscImageFormat.Wbfs,
            BlobType.Tgc => DiscImageFormat.Tgc,
            BlobType.Wia => DiscImageFormat.Wia,
            BlobType.Rvz => DiscImageFormat.Rvz,
            BlobType.Nfs => DiscImageFormat.Nfs,
            _ => DiscImageFormat.Iso,
        };
}

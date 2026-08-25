// SPDX-License-Identifier: MPL-2.0

namespace RelayBridge.Infrastructure.Storage;

public interface ISpoolFileSystem
{
    Stream CreateReceiveStream(string path);

    Task FlushToDiskAsync(Stream stream, CancellationToken cancellationToken);

    Stream OpenRead(string path);

    void Move(string sourcePath, string destinationPath);

    void Delete(string path);

    bool Exists(string path);

    IEnumerable<string> EnumerateFiles(string directory, string pattern);

    DateTimeOffset GetLastWriteTimeUtc(string path);

    long GetAvailableFreeSpace(string path);

    bool CanWrite(string directory);
}

public sealed class PhysicalSpoolFileSystem : ISpoolFileSystem
{
    public Stream CreateReceiveStream(string path)
    {
        return new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
    }

    public async Task FlushToDiskAsync(Stream stream, CancellationToken cancellationToken)
    {
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (stream is not FileStream fileStream)
        {
            throw new InvalidOperationException("The physical spool requires a FileStream for durable flush.");
        }

        fileStream.Flush(flushToDisk: true);
    }

    public Stream OpenRead(string path)
    {
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    public void Move(string sourcePath, string destinationPath)
    {
        File.Move(sourcePath, destinationPath);
    }

    public void Delete(string path)
    {
        File.Delete(path);
    }

    public bool Exists(string path)
    {
        return File.Exists(path);
    }

    public IEnumerable<string> EnumerateFiles(string directory, string pattern)
    {
        return Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly);
    }

    public DateTimeOffset GetLastWriteTimeUtc(string path)
    {
        return File.GetLastWriteTimeUtc(path);
    }

    public long GetAvailableFreeSpace(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path))
            ?? throw new IOException($"Cannot determine the volume for '{path}'.");
        return new DriveInfo(root).AvailableFreeSpace;
    }

    public bool CanWrite(string directory)
    {
        var path = Path.Combine(directory, $".{Guid.NewGuid():N}.health");
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
            stream.WriteByte(0);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The health probe uses DeleteOnClose; this is only a best-effort fallback.
            }
        }
    }
}

using System.Buffers;
using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Rag.Application.Providers;

namespace Rag.Infrastructure.Storage;

internal sealed class LocalDocumentStorage : IDocumentStorage
{
    private readonly string _rootPath;

    public LocalDocumentStorage(
        IOptions<StorageOptions> options,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        _rootPath = Path.GetFullPath(
            Path.IsPathRooted(options.Value.LocalPath)
                ? options.Value.LocalPath
                : Path.Combine(environment.ContentRootPath, options.Value.LocalPath));
        Directory.CreateDirectory(_rootPath);
    }

    public async ValueTask<StoredDocument> StoreAsync(
        DocumentStorageWriteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        string targetPath = ResolveObjectPath(request.ObjectKey);
        string directoryPath = Path.GetDirectoryName(targetPath)!;
        Directory.CreateDirectory(directoryPath);
        string temporaryPath = $"{targetPath}.upload-{Guid.NewGuid():N}";
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        long length = 0;

        try
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: buffer.Length,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                while (true)
                {
                    int bytesRead = await request.Content
                        .ReadAsync(buffer.AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    if (bytesRead == 0)
                    {
                        break;
                    }

                    hash.AppendData(buffer, 0, bytesRead);
                    await destination
                        .WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken)
                        .ConfigureAwait(false);
                    length += bytesRead;
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, targetPath, overwrite: false);
            return new StoredDocument(
                request.ObjectKey,
                length,
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }
        catch
        {
            File.Delete(temporaryPath);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    public ValueTask<Stream> OpenReadAsync(
        string objectKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stream stream = new FileStream(
            ResolveObjectPath(objectKey),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return ValueTask.FromResult(stream);
    }

    public ValueTask DeleteAsync(
        string objectKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(ResolveObjectPath(objectKey));
        return ValueTask.CompletedTask;
    }

    private string ResolveObjectPath(string objectKey)
    {
        if (string.IsNullOrWhiteSpace(objectKey) ||
            Path.IsPathRooted(objectKey))
        {
            throw new ArgumentException("The storage object key is invalid.", nameof(objectKey));
        }

        string normalizedKey = objectKey.Replace('/', Path.DirectorySeparatorChar);
        string resolvedPath = Path.GetFullPath(Path.Combine(_rootPath, normalizedKey));
        string rootPrefix = $"{_rootPath.TrimEnd(Path.DirectorySeparatorChar)}" +
            Path.DirectorySeparatorChar;
        StringComparison pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!resolvedPath.StartsWith(rootPrefix, pathComparison))
        {
            throw new ArgumentException(
                "The storage object key escapes the configured root.",
                nameof(objectKey));
        }

        return resolvedPath;
    }
}

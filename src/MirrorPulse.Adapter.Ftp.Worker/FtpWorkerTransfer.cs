using FluentFTP;
using MirrorPulse.Adapter.Sdk;

namespace MirrorPulse.Adapter.Ftp.Worker;

public sealed class FtpWorkerTransfer(AsyncFtpClient client, FtpWorkerConfiguration configuration)
{
    public const int MaximumRangeBytes = 1024 * 1024;

    public async Task<byte[]> ReadRangeAsync(
        string relativePath,
        long offset,
        int length,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (length is < 0 or > MaximumRangeBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        string path = ResolvePath(relativePath);
        if (length == 0)
        {
            return [];
        }

        await using Stream source = await client.OpenRead(
            path, FtpDataType.Binary, offset, -1, cancellationToken).ConfigureAwait(false);
        byte[] result = new byte[length];
        await source.ReadExactlyAsync(result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<string?> GetRevisionAsync(string relativePath, CancellationToken cancellationToken)
    {
        string path = ResolvePath(relativePath);
        long length = await client.GetFileSize(path, -1, cancellationToken).ConfigureAwait(false);
        if (length < 0)
        {
            return null;
        }

        DateTime modified = await client.GetModifiedTime(path, cancellationToken).ConfigureAwait(false);
        if (modified == DateTime.MinValue)
        {
            throw new NotSupportedException("The FTP server does not expose modification time for conditional upload.");
        }

        return $"{length}:{modified.ToUniversalTime():yyyyMMddHHmmss}";
    }

    public async Task<string> UploadAsync(
        string relativePath,
        string? expectedRevision,
        Stream content,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead || !content.CanSeek || requestId == Guid.Empty)
        {
            throw new ArgumentException("A seekable upload stream and request ID are required.");
        }

        string path = ResolvePath(relativePath);
        string? current = await GetRevisionAsync(relativePath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(current, expectedRevision, StringComparison.Ordinal))
        {
            throw new FtpRevisionConflictException();
        }

        string stagedPath = $"{path}.mirrorpulse-upload-{requestId:N}";
        try
        {
            FtpStatus status = await client.UploadStream(content, stagedPath,
                FtpRemoteExists.Overwrite, createRemoteDir: false, progress: null, cancellationToken)
                .ConfigureAwait(false);
            if (status != FtpStatus.Success)
            {
                throw new IOException("The FTP server did not accept the staged upload.");
            }

            string? afterUpload = await GetRevisionAsync(relativePath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(afterUpload, expectedRevision, StringComparison.Ordinal))
            {
                throw new FtpRevisionConflictException();
            }

            await client.Rename(stagedPath, path, cancellationToken).ConfigureAwait(false);
            return await GetRevisionAsync(relativePath, cancellationToken).ConfigureAwait(false)
                ?? throw new IOException("The renamed FTP upload is missing.");
        }
        catch
        {
            try
            {
                await client.DeleteFile(stagedPath, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // A failed cleanup is reported through the next retry's upload result.
            }

            throw;
        }
    }

    public string ResolvePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (relativePath.Length > 4096 || relativePath.Contains('\0') ||
            relativePath.StartsWith('/') || relativePath.StartsWith('\\'))
        {
            throw new InvalidDataException("The FTP path must be a bounded relative path.");
        }

        string[] parts = relativePath.Replace('\\', '/').Split('/');
        if (parts.Any(part => part.Length == 0 || part is "." or ".." || part.Contains(':')))
        {
            throw new InvalidDataException("The FTP path contains an unsafe segment.");
        }

        string root = configuration.Endpoint.AbsolutePath.TrimEnd('/');
        return root + "/" + string.Join('/', parts);
    }
}

public sealed class FtpRevisionConflictException : IOException
{
    public FtpRevisionConflictException()
        : base("The remote FTP file changed before the conditional upload could complete.")
    {
    }
}

public sealed class FtpWorkerTransferProtocol(
    AdapterControlChannel channel,
    AsyncFtpClient client,
    FtpWorkerConfiguration configuration,
    Guid instanceId,
    Guid workerSessionId)
{
    private readonly FtpWorkerTransfer _transfer = new(client, configuration);

    public async Task HandleAsync(AdapterControlFrame command, CancellationToken cancellationToken)
    {
        try
        {
            switch (command.MessageType)
            {
                case "Stat":
                    {
                        string path = command.Payload.GetProperty("path").GetString()
                            ?? throw new InvalidDataException("The FTP stat path is missing.");
                        string? revision = await _transfer.GetRevisionAsync(path, cancellationToken).ConfigureAwait(false);
                        await channel.SendAsync("StatResult", command.RequestId, true, new { revision }, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    }
                case "ReadRange":
                    {
                        string path = command.Payload.GetProperty("path").GetString()
                            ?? throw new InvalidDataException("The FTP read path is missing.");
                        long offset = command.Payload.GetProperty("offset").GetInt64();
                        int length = command.Payload.GetProperty("length").GetInt32();
                        byte[] bytes = await _transfer.ReadRangeAsync(path, offset, length, cancellationToken)
                            .ConfigureAwait(false);
                        Guid streamId = Guid.NewGuid();
                        await channel.SendAsync("ReadRangeReady", command.RequestId, true,
                            new { streamId, length = bytes.Length }, cancellationToken).ConfigureAwait(false);
                        await channel.SendChunkAsync(new AdapterBinaryChunk(
                            command.RequestId, instanceId, workerSessionId, streamId, offset, bytes, true),
                            cancellationToken).ConfigureAwait(false);
                        break;
                    }
                case "Upload":
                    await HandleUploadAsync(command, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new InvalidDataException("The FTP Worker received an unsupported command.");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            string code = exception switch
            {
                FtpRevisionConflictException => "RemoteConflict",
                InvalidDataException or ArgumentException or System.Text.Json.JsonException => "InvalidRequest",
                NotSupportedException => "CapabilityUnavailable",
                _ => "RetryableTransferFailure",
            };
            await channel.SendAsync("OperationError", command.RequestId, true, new { code }, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private async Task HandleUploadAsync(AdapterControlFrame command, CancellationToken cancellationToken)
    {
        string path = command.Payload.GetProperty("path").GetString()
            ?? throw new InvalidDataException("The FTP upload path is missing.");
        string? expectedRevision = command.Payload.GetProperty("expectedRevision").GetString();
        long length = command.Payload.GetProperty("length").GetInt64();
        Guid streamId = command.Payload.GetProperty("streamId").GetGuid();
        if (length < 0 || streamId == Guid.Empty)
        {
            throw new InvalidDataException("The FTP upload length or stream ID is invalid.");
        }

        string cache = Environment.GetEnvironmentVariable("MP_TRANSFER_CACHE_DIR")
            ?? throw new InvalidDataException("The Host did not provide a transfer cache directory.");
        Directory.CreateDirectory(cache);
        string stagedFile = Path.Combine(cache, $"ftp-{command.RequestId:N}.tmp");
        await channel.SendAsync("UploadReady", command.RequestId, true, new { streamId }, cancellationToken)
            .ConfigureAwait(false);
        string revision;
        try
        {
            await using (var output = new FileStream(stagedFile, FileMode.Create, FileAccess.Write,
                FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                long received = 0;
                while (true)
                {
                    AdapterBinaryChunk chunk = await channel.ReadChunkAsync(cancellationToken).ConfigureAwait(false);
                    if (chunk.RequestId != command.RequestId || chunk.StreamId != streamId ||
                        chunk.Offset != received || chunk.Data.Length > length - received)
                    {
                        throw new InvalidDataException("The FTP upload chunk is out of order or exceeds the declared size.");
                    }

                    await output.WriteAsync(chunk.Data, cancellationToken).ConfigureAwait(false);
                    received += chunk.Data.Length;
                    if (chunk.EndOfStream)
                    {
                        if (received != length)
                        {
                            throw new InvalidDataException("The FTP upload ended before its declared size.");
                        }

                        break;
                    }
                }
            }

            await using var input = new FileStream(stagedFile, FileMode.Open, FileAccess.Read,
                FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
            revision = await _transfer.UploadAsync(path, expectedRevision, input,
                command.RequestId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(stagedFile);
        }

        await channel.SendAsync("UploadComplete", command.RequestId, true,
            new { revision }, cancellationToken).ConfigureAwait(false);
    }
}

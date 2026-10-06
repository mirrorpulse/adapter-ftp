using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentFTP.Exceptions;
using MirrorPulse.Adapter.Sdk;

namespace MirrorPulse.Adapter.Ftp.Worker;

internal sealed class FtpTransferProtocol(AdapterControlChannel channel, AdapterWorkerProcessArguments arguments,
    FtpWorkerRoots roots, string cache) : IAsyncDisposable
{
    private readonly Dictionary<Guid, PendingUpload> _uploads = [];
    private readonly Dictionary<Guid, string> _bindings = [];
    private readonly Queue<Guid> _bindingOrder = [];

    public async Task RunAsync(CancellationToken token)
    {
        while (true)
        {
            AdapterWorkerFrame frame = await channel.ReadNextAsync(token).ConfigureAwait(false);
            if (frame.Chunk is { } chunk)
            {
                await ReceiveAsync(chunk, token).ConfigureAwait(false);
                continue;
            }
            AdapterControlFrame command = frame.Control!;
            if (command.IsResponse) throw new InvalidDataException("UnexpectedResponse");
            if (command.MessageType == "Stop") { await ReplyAsync(command, "Stopped", new { }, token).ConfigureAwait(false); return; }
            try
            {
                if (command.MessageType == "Cancel")
                {
                    await CancelAsync(command, token).ConfigureAwait(false);
                    continue;
                }
                AdapterFileAddress address = AdapterProtocolJson.ReadAddress(command.Payload, 2);
                FtpWorkerRoot root = roots.Get(address.RootKey);
                _ = FtpPathPolicy.Resolve(root.Configuration.Endpoint, address.Path);
                switch (command.MessageType)
                {
                    case "Stat":
                        await ReplyAsync(command, "StatResult", new
                        {
                            rootKey = address.RootKey,
                            revision = await FtpOperations.RevisionAsync(root, address.Path, token).ConfigureAwait(false)
                        }, token).ConfigureAwait(false);
                        break;
                    case "List":
                        string? cursor = command.Payload.TryGetProperty("cursor", out JsonElement cursorValue) && cursorValue.ValueKind == JsonValueKind.String ? cursorValue.GetString() : null;
                        object page = await FtpOperations.ListAsync(root, address, command.Payload.GetProperty("pageSize").GetInt32(), cursor, token).ConfigureAwait(false);
                        await ReplyAsync(command, "DirectoryPage", page, token).ConfigureAwait(false);
                        break;
                    case "ReadRange":
                        long offset = command.Payload.GetProperty("offset").GetInt64();
                        long length = command.Payload.GetProperty("length").GetInt64();
                        if (offset < 0 || length is < 1 or > AdapterBinaryChunkV2Codec.MaximumChunkBytes || offset > long.MaxValue - length)
                            throw new InvalidDataException("InvalidRange");
                        string? expected = command.Payload.TryGetProperty("expectedRevision", out JsonElement revision) && revision.ValueKind == JsonValueKind.String ? revision.GetString() : null;
                        byte[] bytes = await FtpOperations.ReadAsync(root, address, offset, checked((int)length), expected, token).ConfigureAwait(false);
                        Guid stream = Guid.NewGuid();
                        await ReplyAsync(command, "ReadRangeReady", new { rootKey = address.RootKey, streamId = stream, length }, token).ConfigureAwait(false);
                        await channel.SendChunkAsync(new(command.RequestId, arguments.InstanceId, arguments.WorkerSessionId, stream, offset, bytes, true)
                        { RootKey = address.RootKey }, token).ConfigureAwait(false);
                        break;
                    case "Upload": await BeginUploadAsync(command, address, root, token).ConfigureAwait(false); break;
                    default: throw new InvalidDataException("ConditionalMutationUnavailable");
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or JsonException or FormatException or FtpException or KeyNotFoundException or System.Security.Authentication.AuthenticationException)
            {
                await ErrorAsync(command, exception, token).ConfigureAwait(false);
            }
        }
    }

    private async Task BeginUploadAsync(AdapterControlFrame command, AdapterFileAddress address, FtpWorkerRoot root, CancellationToken token)
    {
        AdapterOperationRequest operation = AdapterProtocolJson.Decode<AdapterOperationRequest>(Encoding.UTF8.GetBytes(command.Payload.GetRawText()));
        AdapterProtocolJson.ValidateMutation(operation, requiresDestination: false);
        long length = command.Payload.GetProperty("length").GetInt64();
        Guid stream = command.Payload.GetProperty("streamId").GetGuid();
        if (length < 0 || stream == Guid.Empty || _uploads.Count >= 4) throw new InvalidDataException("UploadLimit");
        if (_uploads.Values.Any(upload => upload.Operation.OperationId == operation.OperationId)) throw new InvalidDataException("OperationInProgress");
        string fingerprint = FtpUploadOperations.Fingerprint(operation, length);
        if (_bindings.TryGetValue(operation.OperationId, out string? previous) && previous != fingerprint)
            throw new InvalidDataException("OperationBindingMismatch");
        await FtpUploadOperations.PrepareAsync(root, operation, length, token).ConfigureAwait(false);
        var lease = new AdapterTransferLease(cache);
        try
        {
            _uploads.Add(command.RequestId, new(command, operation, root,
                new(command.RequestId, arguments.InstanceId, arguments.WorkerSessionId, stream, address.RootKey, 0, length), lease));
        }
        catch { await lease.DisposeAsync().ConfigureAwait(false); throw; }
        if (_bindings.TryAdd(operation.OperationId, fingerprint))
        {
            _bindingOrder.Enqueue(operation.OperationId);
            if (_bindingOrder.Count > 256) _bindings.Remove(_bindingOrder.Dequeue());
        }
        await ReplyAsync(command, "UploadReady", new { rootKey = address.RootKey, operationId = operation.OperationId, streamId = stream }, token).ConfigureAwait(false);
    }

    private async Task ReceiveAsync(AdapterBinaryChunk chunk, CancellationToken token)
    {
        if (!_uploads.TryGetValue(chunk.RequestId, out PendingUpload? upload)) throw new InvalidDataException("UnexpectedChunk");
        bool terminal = false;
        try
        {
            upload.Binding.Accept(chunk);
            await upload.Lease.Stream.WriteAsync(chunk.Data, token).ConfigureAwait(false);
            if (!upload.Binding.Completed) return;
            terminal = true;
            upload.Lease.Stream.Position = 0;
            string digest = Convert.ToHexString(await SHA256.HashDataAsync(upload.Lease.Stream, token).ConfigureAwait(false));
            string revision = await FtpUploadOperations.UploadAsync(upload.Root, upload.Operation, upload.Lease.Stream, digest, token).ConfigureAwait(false);
            await ReplyAsync(upload.Command, "UploadComplete", new
            {
                rootKey = upload.Operation.RootKey,
                operationId = upload.Operation.OperationId,
                revision,
                contentSha256 = digest
            }, token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or JsonException or FormatException or FtpException or KeyNotFoundException or System.Security.Authentication.AuthenticationException)
        { terminal = true; await ErrorAsync(upload.Command, exception, token).ConfigureAwait(false); }
        finally
        {
            if (terminal)
            {
                _uploads.Remove(chunk.RequestId);
                await upload.Lease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task CancelAsync(AdapterControlFrame command, CancellationToken token)
    {
        string rootKey = command.Payload.GetProperty("rootKey").GetString() ?? throw new InvalidDataException("RootRequired");
        roots.Get(rootKey);
        Guid target = command.Payload.GetProperty("targetRequestId").GetGuid();
        if (target == Guid.Empty) throw new InvalidDataException("InvalidRequest");
        string status = "alreadyCompleted";
        if (_uploads.TryGetValue(target, out PendingUpload? upload))
        {
            if (upload.Operation.RootKey != rootKey) throw new InvalidDataException("CancelRootMismatch");
            if (command.Payload.TryGetProperty("operationId", out JsonElement operation) && operation.ValueKind != JsonValueKind.Null &&
                operation.GetGuid() != upload.Operation.OperationId) throw new InvalidDataException("CancelOperationMismatch");
            _uploads.Remove(target);
            await upload.Lease.CancelAsync().ConfigureAwait(false);
            await ErrorAsync(upload.Command, new InvalidDataException("Canceled"), token).ConfigureAwait(false);
            status = "canceled";
        }
        await ReplyAsync(command, "CancelAck", new { rootKey, targetRequestId = target, status }, token).ConfigureAwait(false);
    }

    private ValueTask ErrorAsync(AdapterControlFrame command, Exception exception, CancellationToken token)
    {
        string[] codes = ["UnknownRoot", "RootOffline", "InvalidPath", "InvalidCursor", "InvalidPageSize", "InvalidRange", "RemoteConflict",
            "SourceUnavailable", "DirectoryEnumerationIncomplete", "ConditionalMutationUnavailable", "OperationBindingMismatch", "OperationInProgress",
            "ReadOnlyRoot", "RootMutationForbidden", "ReservedPath", "UploadLimit", "Canceled", "CancelRootMismatch", "CancelOperationMismatch"];
        string code = exception is FtpRecoveryRequiredException ? "MutationOutcomeAmbiguous" :
            exception is System.Security.Authentication.AuthenticationException ? "CertificateRejected" :
            exception is InvalidDataException && codes.Contains(exception.Message, StringComparer.Ordinal) ? exception.Message :
            exception is FtpException or IOException ? "RetryableTransferFailure" : "InvalidRequest";
        string? root = command.Payload.TryGetProperty("rootKey", out JsonElement rootValue) && rootValue.ValueKind == JsonValueKind.String ? rootValue.GetString() : null;
        Guid? operation = command.Payload.TryGetProperty("operationId", out JsonElement operationValue) && operationValue.ValueKind == JsonValueKind.String && operationValue.TryGetGuid(out Guid id) ? id : null;
        return ReplyAsync(command, "OperationError", new
        {
            rootKey = root,
            operationId = operation,
            code,
            recoveryRelativePath = (exception as FtpRecoveryRequiredException)?.RecoveryRelativePath
        }, token);
    }
    private ValueTask ReplyAsync(AdapterControlFrame command, string type, object payload, CancellationToken token) =>
        channel.SendAsync(type, command.RequestId, true, payload, token);
    public async ValueTask DisposeAsync()
    {
        foreach (PendingUpload upload in _uploads.Values) await upload.Lease.DisposeAsync().ConfigureAwait(false);
        _uploads.Clear();
    }

    private sealed record PendingUpload(AdapterControlFrame Command, AdapterOperationRequest Operation, FtpWorkerRoot Root,
        AdapterStreamBinding Binding, AdapterTransferLease Lease);
}

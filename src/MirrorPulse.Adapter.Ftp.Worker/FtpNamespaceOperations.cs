using System.Security.Cryptography;
using System.Text;
using FluentFTP;
using FluentFTP.Exceptions;
using MirrorPulse.Adapter.Sdk;

namespace MirrorPulse.Adapter.Ftp.Worker;

internal static class FtpNamespaceOperations
{
    public static string Fingerprint(string type, AdapterOperationRequest operation) =>
        Convert.ToHexString(SHA256.HashData(AdapterProtocolJson.Encode(new { type, operation })));

    public static async Task<string?> MutateAsync(string type, FtpWorkerRoot root,
        AdapterOperationRequest operation, string cache, Action bindOperation, CancellationToken token)
    {
        FtpUploadOperations.ValidateUserPath(operation.Path);
        if (!root.AllowsMutations) throw new InvalidDataException("ReadOnlyRoot");
        if (type == "Move")
        {
            if (operation.DestinationRootKey != operation.RootKey) throw new InvalidDataException("CrossRootMoveUnavailable");
            FtpUploadOperations.ValidateUserPath(operation.DestinationPath!);
            _ = FtpPathPolicy.Resolve(root.Configuration.Endpoint, operation.DestinationPath!);
            if (operation.Path == operation.DestinationPath) throw new InvalidDataException("InvalidRequest");
            if (operation.IsDirectory) throw new InvalidDataException("DirectoryMoveUnavailable");
        }
        string journal = FtpUploadOperations.Sibling(operation.Path, "journal", operation.OperationId);
        string backup = FtpUploadOperations.Sibling(operation.Path, "recovery", operation.OperationId);
        string fingerprint = Fingerprint(type, operation);
        FtpUploadOperations.Receipt? receipt = await FtpUploadOperations.ReadReceiptAsync(root, journal, token).ConfigureAwait(false);
        if (receipt is not null && receipt.Fingerprint != fingerprint) throw new InvalidDataException("OperationBindingMismatch");
        string? current = await FtpOperations.RevisionAsync(root, operation.Path, token).ConfigureAwait(false);
        if (receipt?.Phase == "Committed")
        {
            bindOperation();
            return await ReconcileAsync(type, root, operation, receipt.Digest, token).ConfigureAwait(false);
        }
        if (receipt is null)
        {
            AdapterMutationPreconditions conditions = operation.Preconditions ?? new();
            if (type == "CreateDirectory")
            {
                if (current is not null && (conditions.DestinationMustBeAbsent || !IsDirectory(current)))
                    throw new InvalidDataException("RemoteConflict");
            }
            else if (current is null || current != conditions.ExpectedRevision || IsDirectory(current) != operation.IsDirectory)
                throw new InvalidDataException("RemoteConflict");
            if (type == "Move" && await FtpOperations.RevisionAsync(root, operation.DestinationPath!, token).ConfigureAwait(false) is not null)
                throw new InvalidDataException("RemoteConflict");
            if (type == "Delete" && operation.IsDirectory) await RequireEmptyAsync(root, operation.Path, token).ConfigureAwait(false);
            string digest = operation.IsDirectory ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(current ?? ""))) :
                await FtpUploadOperations.DigestAsync(root, operation.Path, token).ConfigureAwait(false) ?? throw new InvalidDataException("RemoteConflict");
            receipt = new(fingerprint, digest, null, "Prepared");
            await FtpUploadOperations.WriteReceiptAsync(root, journal, receipt, token).ConfigureAwait(false);
        }
        bindOperation();
        try
        {
            if (operation.IsDirectory)
            {
                if (type == "CreateDirectory")
                {
                    if (current is null) await root.Client.CreateDirectory(Resolve(root, operation.Path), false, token).ConfigureAwait(false);
                }
                else if (current is not null)
                {
                    if (current != operation.Preconditions?.ExpectedRevision || !IsDirectory(current))
                        throw new InvalidDataException("RemoteConflict");
                    await RequireEmptyAsync(root, operation.Path, token).ConfigureAwait(false);
                    // DeleteDirectory is recursive. RMD alone cannot remove children.
                    FtpReply reply = await root.Client.Execute("RMD " + Resolve(root, operation.Path), token).ConfigureAwait(false);
                    if (!reply.Success) throw new FtpRecoveryRequiredException(journal);
                }
            }
            else
            {
                string? preserved = await FtpUploadOperations.DigestAsync(root, backup, token).ConfigureAwait(false);
                if (type == "Delete")
                {
                    if (preserved is not null)
                    {
                        if (preserved != receipt.Digest || current is not null) throw new FtpRecoveryRequiredException(backup);
                    }
                    else
                    {
                        await RequireSourceAsync(root, operation, receipt.Digest, token).ConfigureAwait(false);
                        await root.Client.Rename(Resolve(root, operation.Path), Resolve(root, backup), token).ConfigureAwait(false);
                    }
                    if (await FtpUploadOperations.DigestAsync(root, backup, token).ConfigureAwait(false) != receipt.Digest)
                        throw new FtpRecoveryRequiredException(backup);
                }
                else
                {
                    if (preserved is null)
                    {
                        await RequireSourceAsync(root, operation, receipt.Digest, token).ConfigureAwait(false);
                        await using var lease = new AdapterTransferLease(cache);
                        if (!await root.Client.DownloadStream(lease.Stream, Resolve(root, operation.Path), token: token).ConfigureAwait(false))
                            throw new IOException("PreservationFailed");
                        lease.Stream.Position = 0;
                        if (Convert.ToHexString(await SHA256.HashDataAsync(lease.Stream, token).ConfigureAwait(false)) != receipt.Digest)
                            throw new InvalidDataException("RemoteConflict");
                        lease.Stream.Position = 0;
                        if (await root.Client.UploadStream(lease.Stream, Resolve(root, backup), FtpRemoteExists.NoCheck,
                            createRemoteDir: false, token: token).ConfigureAwait(false) != FtpStatus.Success)
                            throw new FtpRecoveryRequiredException(backup);
                    }
                    if (await FtpUploadOperations.DigestAsync(root, backup, token).ConfigureAwait(false) != receipt.Digest)
                        throw new FtpRecoveryRequiredException(backup);
                    if (current is not null)
                    {
                        await RequireSourceAsync(root, operation, receipt.Digest, token).ConfigureAwait(false);
                        if (await FtpOperations.RevisionAsync(root, operation.DestinationPath!, token).ConfigureAwait(false) is not null)
                            throw new InvalidDataException("RemoteConflict");
                        await root.Client.Rename(Resolve(root, operation.Path), Resolve(root, operation.DestinationPath!), token).ConfigureAwait(false);
                    }
                }
            }
            string? revision = await ReconcileAsync(type, root, operation, receipt.Digest, token).ConfigureAwait(false);
            await FtpUploadOperations.WriteReceiptAsync(root, journal, receipt with { Phase = "Committed" }, token).ConfigureAwait(false);
            return revision;
        }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) when (exception is FtpException or IOException or OperationCanceledException or TimeoutException or System.Net.Sockets.SocketException)
        {
            if (exception is not FtpRecoveryRequiredException)
                root.Client.RequireReconnect();
            throw new FtpRecoveryRequiredException(operation.IsDirectory ? journal : backup);
        }
    }

    private static async Task RequireSourceAsync(FtpWorkerRoot root, AdapterOperationRequest operation, string digest, CancellationToken token)
    {
        if (await FtpOperations.RevisionAsync(root, operation.Path, token).ConfigureAwait(false) != operation.Preconditions?.ExpectedRevision ||
            await FtpUploadOperations.DigestAsync(root, operation.Path, token).ConfigureAwait(false) != digest)
            throw new InvalidDataException("RemoteConflict");
    }

    private static async Task<string?> ReconcileAsync(string type, FtpWorkerRoot root, AdapterOperationRequest operation, string digest, CancellationToken token)
    {
        string? source = await FtpOperations.RevisionAsync(root, operation.Path, token).ConfigureAwait(false);
        if (type == "CreateDirectory")
        {
            if (source is null || !IsDirectory(source)) throw new InvalidDataException("RemoteConflict");
            return source;
        }
        if (source is not null) throw new InvalidDataException("RemoteConflict");
        if (type == "Delete") return null;
        if (await FtpUploadOperations.DigestAsync(root, operation.DestinationPath!, token).ConfigureAwait(false) != digest)
            throw new InvalidDataException("RemoteConflict");
        return await FtpOperations.RevisionAsync(root, operation.DestinationPath!, token).ConfigureAwait(false);
    }

    private static async Task RequireEmptyAsync(FtpWorkerRoot root, string relative, CancellationToken token)
    {
        if ((await root.Client.ReadBoundedListingAsync(Resolve(root, relative), token).ConfigureAwait(false)).Count != 0)
            throw new InvalidDataException("DirectoryNotEmpty");
    }
    private static bool IsDirectory(string revision) => revision.StartsWith("ftp-metadata:Directory:", StringComparison.Ordinal);
    private static string Resolve(FtpWorkerRoot root, string relative) => FtpPathPolicy.Resolve(root.Configuration.Endpoint, relative);
}

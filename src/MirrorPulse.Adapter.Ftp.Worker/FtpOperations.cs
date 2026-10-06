using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FluentFTP;
using MirrorPulse.Adapter.Sdk;

namespace MirrorPulse.Adapter.Ftp.Worker;

internal static class FtpOperations
{
    private static string Revision(FtpListItem item) => "ftp-metadata:" + item.Type + ":" +
        item.Size.ToString(CultureInfo.InvariantCulture) + ":" +
        (item.Modified == DateTime.MinValue ? "unknown" : item.Modified.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));

    public static async Task<string?> RevisionAsync(FtpWorkerRoot root, string relative, CancellationToken token)
    {
        string path = FtpPathPolicy.Resolve(root.Configuration.Endpoint, relative);
        if (relative.Length == 0)
        {
            IReadOnlyList<FtpListItem> children = await root.Client.ReadBoundedListingAsync(path, token).ConfigureAwait(false);
            return "ftp-directory:" + Convert.ToHexString(SHA256.HashData(AdapterProtocolJson.Encode(
                children.Where(item => !FtpUploadOperations.IsPrivateName(item.Name)).OrderBy(item => item.Name, StringComparer.Ordinal)
                    .Select(item => new { item.Name, revision = Revision(item) }))));
        }
        int separator = path.LastIndexOf('/');
        string parent = separator == 0 ? "/" : path[..separator];
        string name = path[(separator + 1)..];
        IReadOnlyList<FtpListItem> items = await root.Client.ReadBoundedListingAsync(parent, token).ConfigureAwait(false);
        FtpListItem? entry = items.SingleOrDefault(item => item.Name == name);
        if (entry is not null && entry.Type is not (FtpObjectType.File or FtpObjectType.Directory))
            throw new InvalidDataException("DirectoryEnumerationIncomplete");
        return entry is null ? null : Revision(entry);
    }

    public static async Task<object> ListAsync(FtpWorkerRoot root, AdapterFileAddress address, int size, string? cursor, CancellationToken token)
    {
        if (size is < 1 or > 512) throw new InvalidDataException("InvalidPageSize");
        int offset = 0;
        if (cursor is not null)
        {
            if (cursor.Length > 8192) throw new InvalidDataException("InvalidCursor");
            string[] parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('\0');
            if (parts.Length != 3 || parts[0] != address.RootKey || parts[1] != address.Path ||
                !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset < 0)
                throw new InvalidDataException("InvalidCursor");
        }
        IReadOnlyList<FtpListItem> listed = await root.Client.ReadBoundedListingAsync(FtpPathPolicy.Resolve(root.Configuration.Endpoint, address.Path), token).ConfigureAwait(false);
        FtpListItem[] children = listed.Where(item => !FtpUploadOperations.IsPrivateName(item.Name)).OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
        if (offset > children.Length) throw new InvalidDataException("InvalidCursor");
        var entries = new List<object>();
        foreach (FtpListItem item in children.Skip(offset).Take(size))
        {
            if (item.Type is not (FtpObjectType.File or FtpObjectType.Directory)) throw new InvalidDataException("DirectoryEnumerationIncomplete");
            if (item.Name.Contains('/') || item.Name.Contains('\\') || item.Name.Length == 0) throw new InvalidDataException("InvalidPath");
            string relative = address.Path.Length == 0 ? item.Name : address.Path + "/" + item.Name;
            _ = FtpPathPolicy.Resolve(root.Configuration.Endpoint, relative);
            DateTimeOffset? modified = item.Modified == DateTime.MinValue ? null : new(item.Modified.ToUniversalTime());
            entries.Add(new
            {
                remoteId = relative,
                relativePath = relative,
                remoteRevision = Revision(item),
                itemKind = item.Type == FtpObjectType.Directory ? "Directory" : "File",
                length = item.Type == FtpObjectType.File ? (long?)item.Size : null,
                creationTime = modified,
                lastWriteTime = modified,
                isDeleted = false
            });
        }
        int next = offset + entries.Count;
        bool complete = next >= children.Length;
        string? nextCursor = complete ? null : Convert.ToBase64String(Encoding.UTF8.GetBytes(address.RootKey + "\0" + address.Path + "\0" + next.ToString(CultureInfo.InvariantCulture)));
        return new { rootKey = address.RootKey, entries, cursor = nextCursor, isComplete = complete };
    }

    public static async Task<byte[]> ReadAsync(FtpWorkerRoot root, AdapterFileAddress address, long offset, int length, string? expected, CancellationToken token)
    {
        string? before = await RevisionAsync(root, address.Path, token).ConfigureAwait(false);
        if (before is null) throw new InvalidDataException("SourceUnavailable");
        if (expected is not null && expected != before) throw new InvalidDataException("RemoteConflict");
        byte[] bytes = new byte[length];
        await using (Stream stream = await root.Client.OpenRead(FtpPathPolicy.Resolve(root.Configuration.Endpoint, address.Path),
            FtpDataType.Binary, offset, -1, token).ConfigureAwait(false))
        {
            await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        }
        string? after = await RevisionAsync(root, address.Path, token).ConfigureAwait(false);
        if (before != after) throw new InvalidDataException("RemoteConflict");
        return bytes;
    }
}

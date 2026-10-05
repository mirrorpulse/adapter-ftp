using System.Text;
using FluentFTP;
using FluentFTP.Helpers;
using FluentFTP.Streams;

namespace MirrorPulse.Adapter.Ftp.Worker;

/// <summary>Reuse FluentFTP transport and parsers while bounding raw directory input before parsing.</summary>
public sealed class BoundedFtpClient : AsyncFtpClient
{
    public async Task<IReadOnlyList<FtpListItem>> ReadBoundedListingAsync(string path, CancellationToken token)
    {
        bool machine = Capabilities.Contains(FtpCapability.MLST);
        await SetDataTypeAsync(FtpDataType.Binary, token).ConfigureAwait(false);
        FtpDataStream stream = await OpenDataStreamAsync((machine ? "MLSD " : "LIST ") + path, 0, token).ConfigureAwait(false);
        using var raw = new MemoryStream();
        try
        {
            byte[] buffer = new byte[8192];
            while (true)
            {
                int read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                if (read == 0) break;
                if (raw.Length + read > 4 * 1024 * 1024) throw new InvalidDataException("DirectoryEnumerationIncomplete");
                await raw.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            }
        }
        catch
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try { await stream.CloseAsync(cleanup.Token).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or FluentFTP.Exceptions.FtpException or OperationCanceledException) { }
            throw;
        }
        await stream.CloseAsync(token).ConfigureAwait(false);
        string text = new UTF8Encoding(false, true).GetString(raw.GetBuffer(), 0, checked((int)raw.Length));
        string[] lines = text.Split('\n');
        if (lines.Length > 8193 || lines.Any(line => line.Length > 8192)) throw new InvalidDataException("DirectoryEnumerationIncomplete");
        var parser = new FtpListParser(this);
        parser.Init(ServerOS, FtpParser.Auto);
        var items = new List<FtpListItem>();
        foreach (string line in lines)
        {
            string value = line.TrimEnd('\r');
            if (value.Length == 0) continue;
            string? machineName = null;
            if (machine)
            {
                int separator = value.IndexOf(' ');
                if (separator < 1) throw new InvalidDataException("DirectoryEnumerationIncomplete");
                machineName = value[(separator + 1)..];
                if (machineName is "." or "..") continue;
                if (machineName.Length == 0 || machineName.Contains('/') || machineName.Contains('\\') || machineName.Any(char.IsControl))
                    throw new InvalidDataException("InvalidPath");
            }
            FtpListItem? item = parser.ParseSingleLine(path, value, Capabilities, machine);
            if (item is null) throw new InvalidDataException("DirectoryEnumerationIncomplete");
            if (item.Name is "." or "..") continue;
            if (machineName is not null && item.Name != machineName) throw new InvalidDataException("InvalidPath");
            if (item.FullName.TrimEnd('/') != path.TrimEnd('/') + "/" + item.Name) throw new InvalidDataException("InvalidPath");
            items.Add(item);
        }
        return items;
    }
}

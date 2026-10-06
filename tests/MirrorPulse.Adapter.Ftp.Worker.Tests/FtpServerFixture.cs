using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MirrorPulse.Adapter.Ftp.Worker;

namespace MirrorPulse.Adapter.Ftp.Worker.Tests;

internal sealed class FtpServerFixture : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly X509Certificate2 _certificate;
    private readonly X509Certificate2 _dataCertificate;
    private readonly Task _server;
    private readonly FtpSecurityMode _mode;
    private readonly Dictionary<string, (byte[] Content, DateTime Modified)> _files =
        new(StringComparer.Ordinal);
    private TcpListener? _dataListener;
    private long _restartOffset;
    private string? _renameFrom;
    private bool _protectData;
    private int _uploadCount;

    public FtpServerFixture(FtpSecurityMode mode, string label)
    {
        _mode = mode;
        Label = label;
        _certificate = CreateCertificate();
        _dataCertificate = CreateCertificate();
        CertificateSha256 = Convert.ToHexString(SHA256.HashData(_certificate.RawData));
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _files["/same.txt"] = (Encoding.UTF8.GetBytes(label),
            new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc));
        _files["/second.txt"] = (Encoding.UTF8.GetBytes("second-" + label), new DateTime(2026, 9, 29, 12, 0, 1, DateTimeKind.Utc));
        _server = ServeAsync();
    }

    private static X509Certificate2 CreateCertificate()
    {
        using RSA key = RSA.Create(2048);
        var certificateRequest = new CertificateRequest(
            "CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 generated = certificateRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        return X509CertificateLoader.LoadPkcs12(
            generated.Export(X509ContentType.Pfx), null,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
    }

    public string Label { get; }
    public int CommandsReceived { get; private set; }
    public string? ListingOverride { get; set; }
    public int Port { get; }

    public string CertificateSha256 { get; }

    public bool Authenticated { get; private set; }

    public bool ControlChannelEncrypted { get; private set; }
    public bool UseDifferentDataCertificate { get; set; }

    public bool FailNextStore { get; set; }
    public bool FailNextPublish { get; set; }
    public bool LoseNextPublishAcknowledgement { get; set; }
    public Action? AfterStageStored { get; set; }
    public int PublishedUploads { get; private set; }
    public List<string> MutationCommands { get; } = [];
    public string ServerFailure => _server.Exception?.InnerException?.ToString() ?? "none";
    public string[] StoredPaths => _files.Keys.ToArray();

    public void ReplaceStoredFile(string path, byte[] content, bool keepTimestamp = false)
    {
        DateTime modified = keepTimestamp && _files.TryGetValue(path, out var previous) ? previous.Modified : DateTime.UtcNow;
        _files[path] = (content.ToArray(), modified);
    }

    public byte[]? ReadStoredFile(string path) =>
        _files.TryGetValue(path, out var file) ? file.Content.ToArray() : null;

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        _dataListener?.Stop();
        try
        {
            await _server.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException or TimeoutException or AuthenticationException or IOException)
        {
        }

        _certificate.Dispose();
        _dataCertificate.Dispose();
    }

    private async Task ServeAsync()
    {
        using TcpClient client = await _listener.AcceptTcpClientAsync();
        Stream stream = client.GetStream();
        if (_mode == FtpSecurityMode.ImplicitTls)
        {
            stream = await SecureAsync(stream);
        }

        await SendAsync(stream, "220 MirrorPulse test FTP ready\r\n");
        while (true)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            string? line = await reader.ReadLineAsync();
            if (line is null)
            {
                return;
            }

            CommandsReceived++;
            string command = line.Split(' ', 2)[0].ToUpperInvariant();
            string argument = line.Length > command.Length ? line[(command.Length + 1)..] : string.Empty;
            if (command is "STOR" or "RNFR" or "RNTO") MutationCommands.Add(line);
            switch (command)
            {
                case "AUTH" when _mode == FtpSecurityMode.ExplicitTls && argument == "TLS":
                    await SendAsync(stream, "234 Proceed with TLS\r\n");
                    stream = await SecureAsync(stream);
                    break;
                case "USER":
                    await SendAsync(stream, argument == "user-" + Label ? "331 Password required\r\n" : "530 Invalid user\r\n");
                    break;
                case "PASS":
                    Authenticated = argument == "secret-" + Label;
                    await SendAsync(stream, Authenticated ? "230 Logged in\r\n" : "530 Login incorrect\r\n");
                    break;
                case "FEAT":
                    await SendAsync(stream, "211-Features\r\n UTF8\r\n SIZE\r\n MDTM\r\n REST STREAM\r\n MLST type*;size*;modify*;\r\n211 End\r\n");
                    break;
                case "PROT":
                    _protectData = argument == "P";
                    await SendAsync(stream, "200 Data protection set\r\n");
                    break;
                case "SIZE":
                    await SendAsync(stream, _files.TryGetValue(argument, out var sized)
                        ? $"213 {sized.Content.Length}\r\n" : "550 Not found\r\n");
                    break;
                case "MDTM":
                    await SendAsync(stream, _files.TryGetValue(argument, out var dated)
                        ? $"213 {dated.Modified:yyyyMMddHHmmss}\r\n" : "550 Not found\r\n");
                    break;
                case "EPSV":
                case "PASV":
                    _dataListener?.Stop();
                    _dataListener = new TcpListener(IPAddress.Loopback, 0);
                    _dataListener.Start();
                    int port = ((IPEndPoint)_dataListener.LocalEndpoint).Port;
                    await SendAsync(stream, command == "EPSV"
                        ? $"229 Entering Extended Passive Mode (|||{port}|)\r\n"
                        : $"227 Entering Passive Mode (127,0,0,1,{port / 256},{port % 256})\r\n");
                    break;
                case "MLSD":
                    await SendAsync(stream, "150 Opening data connection\r\n");
                    using (TcpClient data = await (_dataListener ?? throw new InvalidDataException()).AcceptTcpClientAsync())
                    {
                        Stream dataStream = _protectData ? await SecureAsync(data.GetStream(), dataChannel: true) : data.GetStream();
                        string prefix = argument.TrimEnd('/') + "/";
                        if (ListingOverride is not null) await SendAsync(dataStream, ListingOverride);
                        else foreach (var item in _files.Where(item => item.Key.StartsWith(prefix, StringComparison.Ordinal) && !item.Key[prefix.Length..].Contains('/')))
                            await SendAsync(dataStream, $"type=file;size={item.Value.Content.Length};modify={item.Value.Modified:yyyyMMddHHmmss}; {item.Key[prefix.Length..]}\r\n");
                    }
                    _dataListener?.Stop();
                    await SendAsync(stream, "226 Transfer complete\r\n");
                    break;
                case "REST":
                    _restartOffset = long.Parse(argument, System.Globalization.CultureInfo.InvariantCulture);
                    await SendAsync(stream, "350 Restart position accepted\r\n");
                    break;
                case "RETR":
                    if (!_files.TryGetValue(argument, out var retrieved))
                    {
                        await SendAsync(stream, "550 Not found\r\n");
                        break;
                    }

                    await SendAsync(stream, "150 Opening data connection\r\n");
                    using (TcpClient data = await (_dataListener ?? throw new InvalidDataException()).AcceptTcpClientAsync())
                    {
                        Stream dataStream = _protectData ? await SecureAsync(data.GetStream(), dataChannel: true) : data.GetStream();
                        await dataStream.WriteAsync(retrieved.Content.AsMemory(checked((int)_restartOffset)));
                        await dataStream.FlushAsync();
                    }

                    _restartOffset = 0;
                    _dataListener?.Stop();
                    await SendAsync(stream, "226 Transfer complete\r\n");
                    break;
                case "STOR":
                    await SendAsync(stream, "150 Opening data connection\r\n");
                    using (TcpClient data = await (_dataListener ?? throw new InvalidDataException()).AcceptTcpClientAsync())
                    {
                        Stream dataStream = _protectData ? await SecureAsync(data.GetStream(), dataChannel: true) : data.GetStream();
                        using var output = new MemoryStream();
                        await dataStream.CopyToAsync(output);
                        if (!FailNextStore)
                        {
                            _uploadCount++;
                            _files[argument] = (output.ToArray(),
                                new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc).AddSeconds(_uploadCount));
                            if (argument.StartsWith("/.mp-stage-", StringComparison.Ordinal)) AfterStageStored?.Invoke();
                        }
                    }

                    _dataListener?.Stop();
                    if (FailNextStore)
                    {
                        FailNextStore = false;
                        await SendAsync(stream, "451 Temporary transfer failure\r\n");
                    }
                    else
                    {
                        await SendAsync(stream, "226 Transfer complete\r\n");
                    }

                    break;
                case "RNFR":
                    _renameFrom = _files.ContainsKey(argument) ? argument : null;
                    await SendAsync(stream, _renameFrom is null ? "550 Not found\r\n" : "350 Ready for destination\r\n");
                    break;
                case "RNTO":
                    if (_renameFrom is null)
                    {
                        await SendAsync(stream, "503 Rename source missing\r\n");
                        break;
                    }

                    bool publishing = _renameFrom.StartsWith("/.mp-stage-", StringComparison.Ordinal);
                    if (publishing && FailNextPublish)
                    {
                        FailNextPublish = false;
                        _renameFrom = null;
                        await SendAsync(stream, "451 Publication failed\r\n");
                        break;
                    }

                    _files[argument] = _files[_renameFrom];
                    _files.Remove(_renameFrom);
                    _renameFrom = null;
                    if (publishing) PublishedUploads++;
                    bool lost = publishing && LoseNextPublishAcknowledgement;
                    if (publishing) LoseNextPublishAcknowledgement = false;
                    await SendAsync(stream, lost ? "451 Publication acknowledgement unavailable\r\n" : "250 Rename complete\r\n");
                    break;
                case "DELE":
                    await SendAsync(stream, _files.Remove(argument) ? "250 Deleted\r\n" : "550 Not found\r\n");
                    break;
                case "SYST":
                    await SendAsync(stream, "215 UNIX Type: L8\r\n");
                    break;
                case "PWD":
                    await SendAsync(stream, "257 \"/\" is current directory\r\n");
                    break;
                case "QUIT":
                    await SendAsync(stream, "221 Goodbye\r\n");
                    return;
                default:
                    await SendAsync(stream, "200 Command okay\r\n");
                    break;
            }
        }
    }

    private async Task<Stream> SecureAsync(Stream input, bool dataChannel = false)
    {
        var tls = new SslStream(input, leaveInnerStreamOpen: true);
        await tls.AuthenticateAsServerAsync(dataChannel && UseDifferentDataCertificate ? _dataCertificate : _certificate, clientCertificateRequired: false,
            enabledSslProtocols: SslProtocols.Tls12 | SslProtocols.Tls13,
            checkCertificateRevocation: false);
        ControlChannelEncrypted = true;
        return tls;
    }

    private static Task SendAsync(Stream stream, string response) =>
        stream.WriteAsync(Encoding.UTF8.GetBytes(response)).AsTask();
}

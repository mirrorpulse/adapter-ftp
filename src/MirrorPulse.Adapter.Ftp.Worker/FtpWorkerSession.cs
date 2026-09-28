using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text.Json;
using FluentFTP;
using MirrorPulse.Adapter.Sdk;

namespace MirrorPulse.Adapter.Ftp.Worker;

public enum FtpSecurityMode
{
    Plain,
    ExplicitTls,
    ImplicitTls,
}

public sealed record FtpWorkerConfiguration(
    Uri Endpoint,
    string Username,
    string CredentialReference,
    FtpSecurityMode SecurityMode,
    string? TrustedCertificateSha256 = null)
{
    public void Validate()
    {
        if (!Endpoint.IsAbsoluteUri || Endpoint.Scheme != Uri.UriSchemeFtp ||
            string.IsNullOrWhiteSpace(Endpoint.Host) || !string.IsNullOrEmpty(Endpoint.UserInfo) ||
            !string.IsNullOrEmpty(Endpoint.Query) || !string.IsNullOrEmpty(Endpoint.Fragment))
        {
            throw new InvalidDataException("The FTP endpoint must be an absolute ftp:// URI without credentials.");
        }

        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(CredentialReference) ||
            !Enum.IsDefined(SecurityMode))
        {
            throw new InvalidDataException("The FTP configuration is incomplete.");
        }

        if (TrustedCertificateSha256 is not null &&
            (TrustedCertificateSha256.Length != 64 ||
             !TrustedCertificateSha256.All(Uri.IsHexDigit)))
        {
            throw new InvalidDataException("The trusted FTP certificate SHA-256 is invalid.");
        }
    }
}

public static class FtpWorkerConnection
{
    public static async Task<AsyncFtpClient> ConnectAsync(
        FtpWorkerConfiguration configuration,
        string password,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(password);
        configuration.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var client = new AsyncFtpClient
        {
            Host = configuration.Endpoint.Host,
            Port = configuration.Endpoint.IsDefaultPort
                ? configuration.SecurityMode == FtpSecurityMode.ImplicitTls ? 990 : 21
                : configuration.Endpoint.Port,
            Credentials = new NetworkCredential(configuration.Username, password),
        };
        client.Config.EncryptionMode = configuration.SecurityMode switch
        {
            FtpSecurityMode.Plain => FtpEncryptionMode.None,
            FtpSecurityMode.ExplicitTls => FtpEncryptionMode.Explicit,
            FtpSecurityMode.ImplicitTls => FtpEncryptionMode.Implicit,
            _ => throw new InvalidDataException("Unknown FTP security mode."),
        };
        client.Config.DataConnectionEncryption = configuration.SecurityMode != FtpSecurityMode.Plain;
        client.ValidateCertificate += (_, args) =>
        {
            args.Accept = args.PolicyErrors == SslPolicyErrors.None ||
                (args.Certificate is not null && configuration.TrustedCertificateSha256 is not null &&
                 string.Equals(
                     Convert.ToHexString(SHA256.HashData(args.Certificate.GetRawCertData())),
                     configuration.TrustedCertificateSha256,
                     StringComparison.OrdinalIgnoreCase));
        };

        try
        {
            await client.Connect(cancellationToken).ConfigureAwait(false);
            if (configuration.SecurityMode != FtpSecurityMode.Plain && !client.IsEncrypted)
            {
                throw new InvalidDataException("The FTP server did not establish TLS.");
            }

            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}

public static class FtpWorkerProgram
{
    private static readonly JsonSerializerOptions ConfigurationJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        AdapterWorkerProcessArguments arguments;
        try
        {
            arguments = AdapterWorkerProcessArguments.Parse(args);
        }
        catch (ArgumentException)
        {
            return 2;
        }

        await using AdapterNamedPipeClient pipe = await AdapterNamedPipeClient.ConnectAsync(
            arguments.PipeName, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        var channel = new AdapterControlChannel(pipe, arguments.InstanceId, arguments.WorkerSessionId);
        Guid helloId = Guid.NewGuid();
        await channel.SendAsync("Hello", helloId, false,
            new { adapterId = "mirrorpulse.ftp", minimumProtocolVersion = 1, maximumProtocolVersion = 1 },
            cancellationToken).ConfigureAwait(false);

        try
        {
            AdapterControlFrame ready = await channel.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (ready.MessageType != "Ready" || !ready.IsResponse || ready.RequestId != helloId)
            {
                throw new InvalidDataException("The Host did not accept the FTP Worker handshake.");
            }

            FtpWorkerConfiguration configuration = ready.Payload.Deserialize<FtpWorkerConfiguration>(
                    ConfigurationJsonOptions)
                ?? throw new InvalidDataException("The Host did not provide FTP configuration.");
            configuration.Validate();

            Guid credentialId = Guid.NewGuid();
            await channel.SendAsync("CredentialRequest", credentialId, false,
                new { referenceId = configuration.CredentialReference }, cancellationToken).ConfigureAwait(false);
            AdapterControlFrame credential = await channel.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (credential.MessageType != "CredentialResponse" || !credential.IsResponse ||
                credential.RequestId != credentialId ||
                credential.Payload.GetProperty("referenceId").GetString() != configuration.CredentialReference)
            {
                throw new InvalidDataException("The Host did not provide the requested FTP credential.");
            }

            string secret = credential.Payload.GetProperty("secret").GetString()
                ?? throw new InvalidDataException("The FTP credential is empty.");
            using AsyncFtpClient client = await FtpWorkerConnection.ConnectAsync(
                configuration, secret, cancellationToken).ConfigureAwait(false);
            await channel.SendAsync("Connected", helloId, false,
                new { encrypted = client.IsEncrypted }, cancellationToken).ConfigureAwait(false);
            var transfer = new FtpWorkerTransferProtocol(channel, client, configuration,
                arguments.InstanceId, arguments.WorkerSessionId);
            while (true)
            {
                AdapterControlFrame command = await channel.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (command.IsResponse)
                {
                    throw new InvalidDataException("The Host sent an unexpected FTP response.");
                }

                if (command.MessageType == "Stop")
                {
                    await channel.SendAsync("Stopped", command.RequestId, true, new { }, cancellationToken)
                        .ConfigureAwait(false);
                    return 0;
                }

                await transfer.HandleAsync(command, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception)
        {
            string code = exception switch
            {
                InvalidDataException or JsonException => "InvalidConfiguration",
                System.Security.Authentication.AuthenticationException => "CertificateRejected",
                System.Net.Sockets.SocketException or IOException or TimeoutException => "NetworkUnavailable",
                _ => "ConnectionFailed",
            };
            await channel.SendAsync("Error", helloId, false, new { code }, CancellationToken.None)
                .ConfigureAwait(false);
            return 1;
        }
    }
}

public sealed class FtpWorkerEntryMarker;

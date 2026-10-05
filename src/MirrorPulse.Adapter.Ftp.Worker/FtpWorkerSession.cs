using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using FluentFTP;

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

        if (string.IsNullOrWhiteSpace(Username) || Username.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(CredentialReference) ||
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
    public static async Task<BoundedFtpClient> ConnectAsync(
        FtpWorkerConfiguration configuration,
        string password,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(password);
        configuration.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var client = new BoundedFtpClient
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
        client.Config.RetryAttempts = 0;
        client.Config.ConnectTimeout = 15000;
        client.Config.ReadTimeout = 15000;
        client.Config.DataConnectionConnectTimeout = 15000;
        client.Config.DataConnectionReadTimeout = 15000;
        client.Config.DataConnectionEncryption = configuration.SecurityMode != FtpSecurityMode.Plain;
        if (configuration.SecurityMode != FtpSecurityMode.Plain)
        {
            client.Config.CustomStream = typeof(FtpTlsTransport);
            client.Config.CustomStreamConfig = new FtpTlsPolicy(configuration.TrustedCertificateSha256);
        }
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

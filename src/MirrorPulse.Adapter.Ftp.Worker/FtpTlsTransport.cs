using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentFTP;
using FluentFTP.Client.BaseClient;
using FluentFTP.Streams;

namespace MirrorPulse.Adapter.Ftp.Worker;

/// <summary>Applies the root trust policy to every FluentFTP TLS connection using System TLS.</summary>
public sealed class FtpTlsTransport : IFtpStream, IDisposable
{
    private SslStream? _stream;

    public void Init(BaseFtpClient client, string targetHost, Socket socket,
        CustomRemoteCertificateValidationCallback customRemoteCertificateValidation, bool isControl,
        IFtpStream controlConnStream, IFtpStreamConfig config)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(socket);
        if (config is not FtpTlsPolicy policy) throw new AuthenticationException("CertificatePolicyRequired");
        var network = new NetworkStream(socket, ownsSocket: false)
        {
            ReadTimeout = isControl ? client.Config.ReadTimeout : client.Config.DataConnectionReadTimeout,
            WriteTimeout = isControl ? client.Config.ReadTimeout : client.Config.DataConnectionReadTimeout
        };
        var stream = new SslStream(network, leaveInnerStreamOpen: false, (_, certificate, _, errors) =>
            errors == SslPolicyErrors.None || (certificate is not null && policy.TrustedCertificateSha256 is not null &&
                string.Equals(Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())),
                    policy.TrustedCertificateSha256, StringComparison.OrdinalIgnoreCase)));
        try
        {
            // The library's data callback assumes trust from the control socket.
            // The product policy validates each certificate through the BCL instead.
            stream.AuthenticateAsClient(new SslClientAuthenticationOptions
            {
                TargetHost = targetHost,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.Online,
                ClientCertificates = client.Config.ClientCertificates
            });
            _stream = stream;
        }
        catch { stream.Dispose(); throw; }
    }

    public Stream GetBaseStream() => _stream ?? throw new InvalidOperationException("TlsNotInitialized");
    public bool CanRead() => _stream?.CanRead == true;
    public bool CanWrite() => _stream?.CanWrite == true;
    public SslProtocols GetSslProtocol() => _stream?.SslProtocol ?? SslProtocols.None;
    public string GetCipherSuite() => "System TLS";
    public void Dispose() { _stream?.Dispose(); _stream = null; }
}

public sealed record FtpTlsPolicy(string? TrustedCertificateSha256) : IFtpStreamConfig;

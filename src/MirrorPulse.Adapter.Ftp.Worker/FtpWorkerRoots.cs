using FluentFTP;
using MirrorPulse.Adapter.Sdk;

namespace MirrorPulse.Adapter.Ftp.Worker;

internal sealed record FtpWorkerRoot(string Key, FtpWorkerConfiguration Configuration, BoundedFtpClient Client);

internal sealed class FtpWorkerRoots : IDisposable
{
    private readonly Dictionary<string, FtpWorkerRoot?> _roots = new(StringComparer.Ordinal);

    public static async Task<FtpWorkerRoots> CreateAsync(AdapterReady ready, AdapterControlChannel channel, CancellationToken token)
    {
        if (ready.Roots.Count is < 1 or > 64) throw new InvalidDataException("InvalidRoots");
        var roots = new FtpWorkerRoots();
        try
        {
            foreach (AdapterRootBinding binding in ready.Roots)
            {
                if (!binding.Enabled) { roots._roots.Add(binding.RootKey, null); continue; }
                string endpoint = binding.Configuration.GetValueOrDefault("endpoint") ?? throw new InvalidDataException("EndpointRequired");
                if (!Enum.TryParse(binding.Configuration.GetValueOrDefault("securityMode") ?? "ExplicitTls", out FtpSecurityMode mode))
                    throw new InvalidDataException("InvalidSecurityMode");
                if (mode == FtpSecurityMode.Plain && binding.Configuration.GetValueOrDefault("allowPlaintext") != "true")
                    throw new InvalidDataException("PlaintextNotAllowed");
                var configuration = new FtpWorkerConfiguration(new Uri(endpoint),
                    binding.Configuration.GetValueOrDefault("username") ?? throw new InvalidDataException("UsernameRequired"),
                    binding.Configuration.GetValueOrDefault("credentialReference") ?? throw new InvalidDataException("CredentialRequired"),
                    mode, binding.Configuration.GetValueOrDefault("trustedCertificateSha256"));
                configuration.Validate();
                _ = FtpPathPolicy.Resolve(configuration.Endpoint, "");
                Guid request = Guid.NewGuid();
                await channel.SendAsync("CredentialRequest", request, false,
                    new { rootKey = binding.RootKey, referenceId = configuration.CredentialReference }, token).ConfigureAwait(false);
                AdapterControlFrame response = await channel.ReadAsync(token).ConfigureAwait(false);
                if (response.MessageType != "CredentialResponse" || !response.IsResponse || response.RequestId != request ||
                    response.Payload.GetProperty("referenceId").GetString() != configuration.CredentialReference)
                    throw new InvalidDataException("CredentialRejected");
                string secret = response.Payload.GetProperty("secret").GetString() ?? throw new InvalidDataException("CredentialRequired");
                BoundedFtpClient client = await FtpWorkerConnection.ConnectAsync(configuration, secret, token).ConfigureAwait(false);
                try { roots._roots.Add(binding.RootKey, new(binding.RootKey, configuration, client)); }
                catch { client.Dispose(); throw; }
            }
            return roots;
        }
        catch { roots.Dispose(); throw; }
    }

    public FtpWorkerRoot Get(string key) => !_roots.TryGetValue(key, out FtpWorkerRoot? root)
        ? throw new InvalidDataException("UnknownRoot") : root ?? throw new InvalidDataException("RootOffline");

    public void Dispose()
    {
        foreach (FtpWorkerRoot? root in _roots.Values) root?.Client.Dispose();
    }
}

internal static class FtpPathPolicy
{
    public static string Resolve(Uri endpoint, string relative)
    {
        if (relative.Length > 4096 || relative.StartsWith('/') || relative.Contains('\\') ||
            relative.Contains(':') || relative.Any(char.IsControl)) throw new InvalidDataException("InvalidPath");
        string prefix = Uri.UnescapeDataString(endpoint.AbsolutePath).TrimEnd('/');
        if (prefix.Contains('\\') || prefix.Contains(':') || prefix.Any(char.IsControl) ||
            prefix.Split('/').Any(part => part is "." or "..")) throw new InvalidDataException("InvalidPath");
        if (relative.Length == 0) return prefix.Length == 0 ? "/" : prefix;
        if (relative.Split('/').Any(part => part.Length == 0 || part is "." or "..")) throw new InvalidDataException("InvalidPath");
        return prefix + "/" + relative;
    }
}

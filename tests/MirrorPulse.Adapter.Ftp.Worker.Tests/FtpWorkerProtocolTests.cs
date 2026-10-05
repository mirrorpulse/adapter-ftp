using System.Text;
using MirrorPulse.Adapter.Sdk;

namespace MirrorPulse.Adapter.Ftp.Worker.Tests;

[TestClass]
public sealed class FtpWorkerProtocolTests
{
    private static readonly string[] CredentialRoots = ["left", "right"];
    [TestMethod]
    public async Task TwoSourcesKeepCredentialsNamesRangesAndOfflineRootsIndependent()
    {
        await using var session = await FtpWorkerSession.StartAsync();
        CollectionAssert.AreEqual(CredentialRoots, session.CredentialRoots);
        foreach (string root in new[] { "left", "right" })
        {
            AdapterControlFrame page = await session.RequestAsync("List", new { rootKey = root, path = "", pageSize = 1 });
            Assert.AreEqual("DirectoryPage", page.MessageType);
            Assert.AreEqual("same.txt", page.Payload.GetProperty("entries")[0].GetProperty("relativePath").GetString());
            string listed = page.Payload.GetProperty("entries")[0].GetProperty("remoteRevision").GetString()!;
            AdapterControlFrame stat = await session.RequestAsync("Stat", new { rootKey = root, path = "same.txt" });
            Assert.AreEqual(listed, stat.Payload.GetProperty("revision").GetString());
            Assert.AreEqual(root, Encoding.UTF8.GetString(await session.ReadRangeAsync(root, "same.txt", root.Length)));
        }
        foreach (var entry in new[] { ("unknown", "UnknownRoot"), ("offline", "RootOffline") })
        {
            AdapterControlFrame error = await session.RequestAsync("Stat", new { rootKey = entry.Item1, path = "same.txt" });
            Assert.AreEqual("OperationError", error.MessageType);
            Assert.AreEqual(entry.Item2, error.Payload.GetProperty("code").GetString());
        }
    }

    [TestMethod]
    public async Task CursorsAndInvalidRequestsRemainBoundToTheirAuthorizedRoot()
    {
        await using var session = await FtpWorkerSession.StartAsync();
        AdapterControlFrame first = await session.RequestAsync("List", new { rootKey = "left", path = "", pageSize = 1 });
        string cursor = first.Payload.GetProperty("cursor").GetString()!;
        AdapterControlFrame next = await session.RequestAsync("List", new { rootKey = "left", path = "", pageSize = 1, cursor });
        Assert.AreEqual("second.txt", next.Payload.GetProperty("entries")[0].GetProperty("relativePath").GetString());
        AdapterControlFrame other = await session.RequestAsync("List", new { rootKey = "right", path = "", pageSize = 1, cursor });
        Assert.AreEqual("InvalidCursor", other.Payload.GetProperty("code").GetString());
        foreach (string path in new[] { "../same.txt", "same.txt\r\nDELE /other", "/same.txt", "other\\same.txt" })
        {
            int before = session.Left.CommandsReceived;
            AdapterControlFrame error = await session.RequestAsync("Stat", new { rootKey = "left", path });
            Assert.AreEqual("OperationError", error.MessageType);
            Assert.AreEqual(before, session.Left.CommandsReceived);
        }
        Assert.AreEqual("left", Encoding.UTF8.GetString(await session.ReadRangeAsync("left", "same.txt", 4)));
    }

    [TestMethod]
    public async Task StaleMetadataAndOversizedRangesReturnNoContentFrame()
    {
        await using var session = await FtpWorkerSession.StartAsync();
        AdapterControlFrame stale = await session.RequestAsync("ReadRange", new
        {
            rootKey = "left",
            path = "same.txt",
            offset = 0,
            length = 4,
            expectedRevision = "stale"
        });
        Assert.AreEqual("RemoteConflict", stale.Payload.GetProperty("code").GetString());
        AdapterControlFrame range = await session.RequestAsync("ReadRange", new
        {
            rootKey = "left",
            path = "same.txt",
            offset = 0,
            length = AdapterBinaryChunkV2Codec.MaximumChunkBytes + 1
        });
        Assert.AreEqual("InvalidRange", range.Payload.GetProperty("code").GetString());
        Assert.AreEqual("right", Encoding.UTF8.GetString(await session.ReadRangeAsync("right", "same.txt", 5)));
    }

    [TestMethod]
    public async Task ExplicitAndImplicitTlsProtectControlAndDataChannels()
    {
        foreach (FtpSecurityMode mode in new[] { FtpSecurityMode.ExplicitTls, FtpSecurityMode.ImplicitTls })
        {
            await using var session = await FtpWorkerSession.StartAsync(mode);
            Assert.IsTrue(session.Left.ControlChannelEncrypted);
            Assert.IsTrue(session.Right.ControlChannelEncrypted);
            Assert.AreEqual("left", Encoding.UTF8.GetString(await session.ReadRangeAsync("left", "same.txt", 4)));
        }
    }

    [TestMethod]
    public async Task APlaintextRootRequiresExplicitConsentBeforeCredentialsOrNetwork()
    {
        await using var session = await FtpWorkerSession.StartAsync(allowPlaintext: false);
        Assert.AreEqual("Error", session.StartupFrame!.MessageType);
        Assert.IsEmpty(session.CredentialRoots);
        Assert.AreEqual(0, session.Left.CommandsReceived);
        Assert.AreEqual(0, session.Right.CommandsReceived);
    }

    [TestMethod]
    public async Task AnUntrustedDataCertificateCannotPublishDirectoryEntriesOrContent()
    {
        await using var session = await FtpWorkerSession.StartAsync(FtpSecurityMode.ExplicitTls);
        session.Left.UseDifferentDataCertificate = true;
        AdapterControlFrame refused = await session.RequestAsync("List", new { rootKey = "left", path = "", pageSize = 512 });
        Assert.AreEqual("OperationError", refused.MessageType);
        Assert.AreEqual("CertificateRejected", refused.Payload.GetProperty("code").GetString());
        Assert.AreEqual("right", Encoding.UTF8.GetString(await session.ReadRangeAsync("right", "same.txt", 5)));
    }

    [TestMethod]
    public async Task AnUntrustedTlsCertificateIsRejectedBeforePasswordAuthentication()
    {
        await using var session = await FtpWorkerSession.StartAsync(FtpSecurityMode.ExplicitTls, rejectCertificate: true);
        Assert.AreEqual("Error", session.StartupFrame!.MessageType);
        Assert.IsFalse(session.Left.Authenticated);
        Assert.IsFalse(session.Right.Authenticated);
        Assert.IsFalse(session.StartupFrame.Payload.ToString().Contains("secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task AnOversizedOrEscapingListingCannotProjectOutsideItsRoot()
    {
        foreach (string listing in new[] { new string('x', 4 * 1024 * 1024 + 1),
            "type=file;size=1;modify=20260929120000; ../escape.txt\r\n" })
        {
            await using var session = await FtpWorkerSession.StartAsync();
            session.Left.ListingOverride = listing;
            AdapterControlFrame failed = await session.RequestAsync("List", new { rootKey = "left", path = "", pageSize = 512 });
            Assert.AreEqual("OperationError", failed.MessageType);
            Assert.IsTrue(failed.Payload.GetProperty("code").GetString() is "DirectoryEnumerationIncomplete" or "InvalidPath");
            Assert.AreEqual("right", Encoding.UTF8.GetString(await session.ReadRangeAsync("right", "same.txt", 5)));
        }
    }
}

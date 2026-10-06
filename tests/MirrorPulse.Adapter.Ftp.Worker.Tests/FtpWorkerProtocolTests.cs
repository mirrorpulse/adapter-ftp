using System.Security.Cryptography;
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

    [TestMethod]
    public async Task OptimisticUploadsUseVerifiedStagingAndPreservePreviousContentAcrossAllTransports()
    {
        foreach (FtpSecurityMode mode in new[] { FtpSecurityMode.Plain, FtpSecurityMode.ExplicitTls, FtpSecurityMode.ImplicitTls })
        {
            await using var session = await FtpWorkerSession.StartAsync(mode);
            Guid operation = Guid.NewGuid();
            string revision = (await session.RequestAsync("Stat", new { rootKey = "left", path = "same.txt" })).Payload.GetProperty("revision").GetString()!;
            byte[] content = Encoding.UTF8.GetBytes("verified replacement");
            AdapterControlFrame complete = await session.UploadAsync("left", "same.txt", content, operation, new(revision, false));
            Assert.AreEqual("UploadComplete", complete.MessageType, mode + ": " + complete.Payload.GetRawText() + " Server: " + session.Left.ServerFailure + " Commands: " + string.Join(";", session.Left.MutationCommands));
            Assert.AreEqual(Convert.ToHexString(SHA256.HashData(content)), complete.Payload.GetProperty("contentSha256").GetString());
            CollectionAssert.AreEqual(content, session.Left.ReadStoredFile("/same.txt"));
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("left"), session.Left.ReadStoredFile("/.mp-recovery-" + operation.ToString("N")));
            Assert.AreEqual("right", Encoding.UTF8.GetString(session.Right.ReadStoredFile("/same.txt")!));
            Assert.IsNull(session.Left.ReadStoredFile("/.mp-stage-" + operation.ToString("N")));
            Assert.AreEqual(complete.Payload.GetProperty("revision").GetString(),
                (await session.RequestAsync("Stat", new { rootKey = "left", path = "same.txt" })).Payload.GetProperty("revision").GetString());
            AdapterControlFrame page = await session.RequestAsync("List", new { rootKey = "left", path = "", pageSize = 512 });
            Assert.AreEqual(2, page.Payload.GetProperty("entries").GetArrayLength());
            Assert.IsFalse(page.Payload.GetProperty("entries").EnumerateArray().Any(item => item.GetProperty("relativePath").GetString()!.StartsWith(".mp-", StringComparison.Ordinal)));
            await AssertCacheClearedAsync(session);
        }
    }

    [TestMethod]
    public async Task MultiFrameAndEmptyUploadsPreserveTheirExactLengthAndClearTransferLeases()
    {
        await using var session = await FtpWorkerSession.StartAsync();
        byte[] large = new byte[AdapterBinaryChunkV2Codec.MaximumChunkBytes + 173];
        RandomNumberGenerator.Fill(large);
        Assert.AreEqual("UploadComplete", (await session.UploadAsync("left", "large.bin", large)).MessageType);
        CollectionAssert.AreEqual(large, session.Left.ReadStoredFile("/large.bin"));
        Assert.AreEqual("UploadComplete", (await session.UploadAsync("right", "empty.bin", [])).MessageType);
        Assert.IsEmpty(session.Right.ReadStoredFile("/empty.bin")!);
        await AssertCacheClearedAsync(session);
    }

    [TestMethod]
    public async Task RootPoliciesStaleRevisionsAndReservedPathsRefuseWritesBeforeReceivingBytes()
    {
        await using (var session = await FtpWorkerSession.StartAsync(mutationPolicy: "ReadOnly"))
        {
            int before = session.Left.CommandsReceived;
            AssertCode("ReadOnlyRoot", await session.UploadAsync("left", "new.txt", [1]));
            Assert.AreEqual(before, session.Left.CommandsReceived);
            Assert.IsNull(session.Left.ReadStoredFile("/new.txt"));
        }
        await using (var session = await FtpWorkerSession.StartAsync(mutationPolicy: "invalid"))
        {
            Assert.AreEqual("Error", session.StartupFrame!.MessageType);
            Assert.IsEmpty(session.CredentialRoots);
            Assert.AreEqual(0, session.Left.CommandsReceived);
        }
        await using (var session = await FtpWorkerSession.StartAsync())
        {
            AssertCode("RemoteConflict", await session.UploadAsync("left", "same.txt", [1], preconditions: new("stale", false)));
            AssertCode("ReservedPath", await session.UploadAsync("left", ".mp-stage-" + Guid.NewGuid().ToString("N"), [1]));
            AssertCode("RootMutationForbidden", await session.UploadAsync("left", "", [1]));
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("left"), session.Left.ReadStoredFile("/same.txt"));
            Assert.IsFalse(Directory.Exists(session.Cache));
        }
    }

    [TestMethod]
    public async Task StableOperationReplayChecksBytesAndBindingAndNeverRepublishesChangedTargets()
    {
        await using var session = await FtpWorkerSession.StartAsync();
        Guid operation = Guid.NewGuid();
        byte[] content = Encoding.UTF8.GetBytes("created");
        Assert.AreEqual("UploadComplete", (await session.UploadAsync("left", "new.txt", content, operation)).MessageType);
        Assert.AreEqual("UploadComplete", (await session.UploadAsync("left", "new.txt", content, operation)).MessageType);
        Assert.AreEqual(1, session.Left.PublishedUploads);
        AssertCode("OperationBindingMismatch", await session.UploadAsync("left", "new.txt", Encoding.UTF8.GetBytes("changed"), operation));
        AssertCode("OperationBindingMismatch", await session.UploadAsync("right", "new.txt", content, operation));
        AssertCode("OperationBindingMismatch", await session.UploadAsync("left", "different.txt", content, operation));
        session.Left.ReplaceStoredFile("/new.txt", Encoding.UTF8.GetBytes("external"));
        AssertCode("RemoteConflict", await session.UploadAsync("left", "new.txt", content, operation));
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("external"), session.Left.ReadStoredFile("/new.txt"));
        Assert.AreEqual(1, session.Left.PublishedUploads);
        await AssertCacheClearedAsync(session);
    }

    [TestMethod]
    public async Task ContentVerificationDetectsExternalEditsWithUnchangedSizeAndTimestamp()
    {
        await using var session = await FtpWorkerSession.StartAsync();
        string revision = (await session.RequestAsync("Stat", new { rootKey = "left", path = "same.txt" })).Payload.GetProperty("revision").GetString()!;
        session.Left.AfterStageStored = () => session.Left.ReplaceStoredFile("/same.txt", Encoding.UTF8.GetBytes("edit"), keepTimestamp: true);
        AssertCode("RemoteConflict", await session.UploadAsync("left", "same.txt", Encoding.UTF8.GetBytes("replacement"), preconditions: new(revision, false)));
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("edit"), session.Left.ReadStoredFile("/same.txt"));
        Assert.AreEqual(0, session.Left.PublishedUploads);
        Assert.IsFalse(session.Left.StoredPaths.Any(path => path.StartsWith("/.mp-recovery-", StringComparison.Ordinal)));
        await AssertCacheClearedAsync(session);
    }

    [TestMethod]
    public async Task FailedAndLostPublicationAcknowledgementsRetainOriginalsAndReconcileStableRetries()
    {
        foreach (bool loseAcknowledgement in new[] { false, true })
        {
            await using var session = await FtpWorkerSession.StartAsync();
            Guid operation = Guid.NewGuid();
            string revision = (await session.RequestAsync("Stat", new { rootKey = "left", path = "same.txt" })).Payload.GetProperty("revision").GetString()!;
            session.Left.FailNextPublish = !loseAcknowledgement;
            session.Left.LoseNextPublishAcknowledgement = loseAcknowledgement;
            byte[] content = Encoding.UTF8.GetBytes("new version");
            AdapterControlFrame ambiguous = await session.UploadAsync("left", "same.txt", content, operation, new(revision, false));
            Assert.AreEqual("OperationError", ambiguous.MessageType, "loseAck=" + loseAcknowledgement + " Commands: " + string.Join(";", session.Left.MutationCommands));
            AssertCode("MutationOutcomeAmbiguous", ambiguous);
            Assert.AreEqual(".mp-recovery-" + operation.ToString("N"), ambiguous.Payload.GetProperty("recoveryRelativePath").GetString());
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("left"), session.Left.ReadStoredFile("/.mp-recovery-" + operation.ToString("N")));
            Assert.AreEqual("UploadComplete", (await session.UploadAsync("left", "same.txt", content, operation, new(revision, false))).MessageType);
            CollectionAssert.AreEqual(content, session.Left.ReadStoredFile("/same.txt"));
            Assert.AreEqual(1, session.Left.PublishedUploads);
            await AssertCacheClearedAsync(session);
        }
    }

    [TestMethod]
    public async Task CanceledOrInvalidUploadFramesReleaseCachesWithoutMutatingTheSource()
    {
        await using var session = await FtpWorkerSession.StartAsync();
        foreach (bool cancel in new[] { true, false })
        {
            Guid request = Guid.NewGuid(), stream = Guid.NewGuid(), operation = Guid.NewGuid();
            await session.SendAsync("Upload", request, new { rootKey = "left", path = "new.txt", operationId = operation, streamId = stream, length = 2 });
            Assert.AreEqual("UploadReady", (await session.ReadAsync()).MessageType);
            await session.SendChunkAsync(request, stream, "left", 0, [1], false);
            if (cancel)
            {
                AssertCode("CancelRootMismatch", await session.RequestAsync("Cancel", new { rootKey = "right", targetRequestId = request, operationId = operation }));
                Guid cancelRequest = Guid.NewGuid();
                await session.SendAsync("Cancel", cancelRequest, new { rootKey = "left", targetRequestId = request, operationId = operation });
                AdapterControlFrame error = await session.ReadAsync();
                Assert.AreEqual(request, error.RequestId);
                AssertCode("Canceled", error);
                AdapterControlFrame ack = await session.ReadAsync();
                Assert.AreEqual(cancelRequest, ack.RequestId);
                Assert.AreEqual("CancelAck", ack.MessageType);
            }
            else
            {
                await session.SendChunkAsync(request, stream, "right", 1, [2], true);
                Assert.AreEqual("OperationError", (await session.ReadAsync()).MessageType);
            }
            Assert.IsNull(session.Left.ReadStoredFile("/new.txt"));
            Assert.AreEqual(0, session.Left.PublishedUploads);
            await AssertCacheClearedAsync(session);
        }
    }

    [TestMethod]
    public async Task FileMovesAndDeletesRetainOriginalsAndReplayWithoutRepeatingMutations()
    {
        foreach (FtpSecurityMode mode in new[] { FtpSecurityMode.Plain, FtpSecurityMode.ExplicitTls, FtpSecurityMode.ImplicitTls })
        {
            await using var session = await FtpWorkerSession.StartAsync(mode);
            string revision = (await session.RequestAsync("Stat", new { rootKey = "left", path = "same.txt" })).Payload.GetProperty("revision").GetString()!;
            Guid move = Guid.NewGuid();
            var request = new AdapterOperationRequest(move, "left", "same.txt", "left", "renamed.txt", new(revision));
            AdapterControlFrame moved = await session.RequestAsync("Move", request);
            Assert.AreEqual("MutationComplete", moved.MessageType, moved.Payload.GetRawText());
            Assert.IsNull(session.Left.ReadStoredFile("/same.txt"));
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("left"), session.Left.ReadStoredFile("/renamed.txt"));
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("left"), session.Left.ReadStoredFile("/.mp-recovery-" + move.ToString("N")));
            int commands = session.Left.MutationCommands.Count;
            Assert.AreEqual("MutationComplete", (await session.RequestAsync("Move", request)).MessageType);
            Assert.HasCount(commands, session.Left.MutationCommands);
            Guid delete = Guid.NewGuid();
            var removal = new AdapterOperationRequest(delete, "left", "renamed.txt", Preconditions: new(moved.Payload.GetProperty("revision").GetString()));
            Assert.AreEqual("MutationComplete", (await session.RequestAsync("Delete", removal)).MessageType);
            Assert.IsNull(session.Left.ReadStoredFile("/renamed.txt"));
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("left"), session.Left.ReadStoredFile("/.mp-recovery-" + delete.ToString("N")));
            commands = session.Left.MutationCommands.Count;
            Assert.AreEqual("MutationComplete", (await session.RequestAsync("Delete", removal)).MessageType);
            Assert.HasCount(commands, session.Left.MutationCommands);
            session.Left.ReplaceStoredFile("/renamed.txt", Encoding.UTF8.GetBytes("external"));
            AssertCode("RemoteConflict", await session.RequestAsync("Delete", removal));
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("external"), session.Left.ReadStoredFile("/renamed.txt"));
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("right"), session.Right.ReadStoredFile("/same.txt"));
            await AssertCacheClearedAsync(session);
        }
    }

    [TestMethod]
    public async Task DirectoryCreationAndEmptyDeletionNeverRemoveChildrenOrReservedRecoveryEvidence()
    {
        await using var session = await FtpWorkerSession.StartAsync();
        var create = new AdapterCreateDirectoryRequest(Guid.NewGuid(), "left", "folder");
        AdapterControlFrame created = await session.RequestAsync("CreateDirectory", create);
        Assert.AreEqual("MutationComplete", created.MessageType, created.Payload.GetRawText());
        Assert.IsTrue(session.Left.HasDirectory("/folder"));
        Assert.AreEqual("MutationComplete", (await session.RequestAsync("CreateDirectory", create)).MessageType);
        Assert.AreEqual("MutationComplete", (await session.RequestAsync("CreateDirectory", new AdapterCreateDirectoryRequest(Guid.NewGuid(), "left", "folder/nested"))).MessageType);
        string revision = (await session.RequestAsync("Stat", new { rootKey = "left", path = "folder" })).Payload.GetProperty("revision").GetString()!;
        AssertCode("DirectoryNotEmpty", await session.RequestAsync("Delete", new AdapterOperationRequest(Guid.NewGuid(), "left", "folder", Preconditions: new(revision), IsDirectory: true)));
        Assert.IsTrue(session.Left.HasDirectory("/folder/nested"));
        Assert.AreEqual("UploadComplete", (await session.UploadAsync("left", "folder/nested/file.bin", [1, 2])).MessageType);
        AssertCode("DirectoryMoveUnavailable", await session.RequestAsync("Move", new AdapterOperationRequest(Guid.NewGuid(), "left", "folder", "left", "moved", new(revision), true)));
        CollectionAssert.AreEqual(new byte[] { 1, 2 }, session.Left.ReadStoredFile("/folder/nested/file.bin"));
        AdapterControlFrame empty = await session.RequestAsync("CreateDirectory", new AdapterCreateDirectoryRequest(Guid.NewGuid(), "left", "empty"));
        var remove = new AdapterOperationRequest(Guid.NewGuid(), "left", "empty", Preconditions: new(empty.Payload.GetProperty("revision").GetString()), IsDirectory: true);
        Assert.AreEqual("MutationComplete", (await session.RequestAsync("Delete", remove)).MessageType);
        Assert.IsFalse(session.Left.HasDirectory("/empty"));
        Assert.AreEqual("MutationComplete", (await session.RequestAsync("Delete", remove)).MessageType);
        await AssertCacheClearedAsync(session);
    }

    [TestMethod]
    public async Task NamespaceMutationsEnforceReadOnlyRootDestinationAndOperationBindingBoundaries()
    {
        await using (var session = await FtpWorkerSession.StartAsync(mutationPolicy: "ReadOnly"))
        {
            int commands = session.Left.CommandsReceived;
            AssertCode("ReadOnlyRoot", await session.RequestAsync("CreateDirectory", new AdapterCreateDirectoryRequest(Guid.NewGuid(), "left", "folder")));
            AssertCode("ReadOnlyRoot", await session.RequestAsync("Delete", new AdapterOperationRequest(Guid.NewGuid(), "left", "same.txt")));
            AssertCode("ReadOnlyRoot", await session.RequestAsync("Move", new AdapterOperationRequest(Guid.NewGuid(), "left", "same.txt", "left", "new.txt")));
            Assert.AreEqual(commands, session.Left.CommandsReceived);
        }
        await using (var session = await FtpWorkerSession.StartAsync())
        {
            string revision = (await session.RequestAsync("Stat", new { rootKey = "left", path = "same.txt" })).Payload.GetProperty("revision").GetString()!;
            int commands = session.Left.CommandsReceived;
            AssertCode("CrossRootMoveUnavailable", await session.RequestAsync("Move", new AdapterOperationRequest(Guid.NewGuid(), "left", "same.txt", "right", "new.txt", new(revision))));
            Assert.AreEqual(commands, session.Left.CommandsReceived);
            AssertCode("RemoteConflict", await session.RequestAsync("Move", new AdapterOperationRequest(Guid.NewGuid(), "left", "same.txt", "left", "second.txt", new(revision, false))));
            AssertCode("RemoteConflict", await session.RequestAsync("Delete", new AdapterOperationRequest(Guid.NewGuid(), "left", "same.txt", Preconditions: new("stale"))));
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("left"), session.Left.ReadStoredFile("/same.txt"));
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("second-left"), session.Left.ReadStoredFile("/second.txt"));
            Guid operation = Guid.NewGuid();
            Assert.AreEqual("MutationComplete", (await session.RequestAsync("CreateDirectory", new AdapterCreateDirectoryRequest(operation, "left", "created"))).MessageType);
            AssertCode("OperationBindingMismatch", await session.UploadAsync("left", "new.txt", [1], operation));
            AssertCode("RootMutationForbidden", await session.RequestAsync("Delete", new AdapterOperationRequest(Guid.NewGuid(), "left", "", IsDirectory: true)));
        }
    }

    [TestMethod]
    public async Task ARealDisconnectAfterPublicationReconcilesWithoutPublishingTwice()
    {
        foreach (FtpSecurityMode mode in new[] { FtpSecurityMode.Plain, FtpSecurityMode.ExplicitTls })
        {
            await using var session = await FtpWorkerSession.StartAsync(mode);
            string revision = (await session.RequestAsync("Stat", new { rootKey = "left", path = "same.txt" })).Payload.GetProperty("revision").GetString()!;
            Guid operation = Guid.NewGuid();
            byte[] content = Encoding.UTF8.GetBytes("disconnected publication");
            session.Left.DropNextPublishAcknowledgement = true;
            AssertCode("MutationOutcomeAmbiguous", await session.UploadAsync("left", "same.txt", content, operation, new(revision, false)));
            CollectionAssert.AreEqual(content, session.Left.ReadStoredFile("/same.txt"));
            Assert.AreEqual("UploadComplete", (await session.UploadAsync("left", "same.txt", content, operation, new(revision, false))).MessageType);
            Assert.AreEqual(1, session.Left.PublishedUploads);
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("left"), session.Left.ReadStoredFile("/.mp-recovery-" + operation.ToString("N")));
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("right"), session.Right.ReadStoredFile("/same.txt"));
            await AssertCacheClearedAsync(session);
        }
    }

    [TestMethod]
    public async Task RestartedWorkersUseRemoteOperationProofAndRefuseChangedBindingsAndTargets()
    {
        await using var left = new FtpServerFixture(FtpSecurityMode.Plain, "left");
        await using var right = new FtpServerFixture(FtpSecurityMode.Plain, "right");
        Guid operation = Guid.NewGuid();
        byte[] content = Encoding.UTF8.GetBytes("restart recovery");
        string revision;
        string originalCache;
        await using (var first = await FtpWorkerSession.StartAsync(left: left, right: right))
        {
            revision = (await first.RequestAsync("Stat", new { rootKey = "left", path = "same.txt" })).Payload.GetProperty("revision").GetString()!;
            left.FailNextPublish = true;
            AssertCode("MutationOutcomeAmbiguous", await first.UploadAsync("left", "same.txt", content, operation, new(revision, false)));
            originalCache = first.Cache;
        }
        Assert.IsFalse(Directory.Exists(originalCache));
        await using (var second = await FtpWorkerSession.StartAsync(left: left, right: right))
        {
            AssertCode("OperationBindingMismatch", await second.UploadAsync("left", "same.txt", Encoding.UTF8.GetBytes("different bytes"), operation, new(revision, false)));
            Assert.AreEqual("UploadComplete", (await second.UploadAsync("left", "same.txt", content, operation, new(revision, false))).MessageType);
            CollectionAssert.AreEqual(content, left.ReadStoredFile("/same.txt"));
            await AssertCacheClearedAsync(second);
        }
        await using (var third = await FtpWorkerSession.StartAsync(left: left, right: right))
        {
            Assert.AreEqual("UploadComplete", (await third.UploadAsync("left", "same.txt", content, operation, new(revision, false))).MessageType);
            Assert.AreEqual(1, left.PublishedUploads);
            left.ReplaceStoredFile("/same.txt", Encoding.UTF8.GetBytes("external new version"));
            AssertCode("RemoteConflict", await third.UploadAsync("left", "same.txt", content, operation, new(revision, false)));
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("external new version"), left.ReadStoredFile("/same.txt"));
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("left"), left.ReadStoredFile("/.mp-recovery-" + operation.ToString("N")));
        }
    }

    [TestMethod]
    public async Task CorruptOrOversizedRemoteProofBlocksMutationAndLeavesSourcesIntact()
    {
        foreach (byte[] proof in new[] { Encoding.UTF8.GetBytes("null"), Encoding.UTF8.GetBytes("{}"), new byte[64 * 1024 + 1] })
        {
            await using var session = await FtpWorkerSession.StartAsync();
            Guid operation = Guid.NewGuid();
            string journal = ".mp-journal-" + operation.ToString("N");
            session.Left.ReplaceStoredFile("/" + journal, proof);
            AdapterControlFrame error = await session.UploadAsync("left", "same.txt", [1], operation);
            AssertCode("MutationOutcomeAmbiguous", error);
            Assert.AreEqual(journal, error.Payload.GetProperty("recoveryRelativePath").GetString());
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("left"), session.Left.ReadStoredFile("/same.txt"));
            Assert.AreEqual(0, session.Left.PublishedUploads);
            Assert.AreEqual("right", Encoding.UTF8.GetString(await session.ReadRangeAsync("right", "same.txt", 5)));
        }
    }

    [TestMethod]
    public async Task LostNamespaceAcknowledgementsRecoverAfterWorkerRestartWithoutRemovingRecreatedFiles()
    {
        await using var left = new FtpServerFixture(FtpSecurityMode.Plain, "left");
        await using var right = new FtpServerFixture(FtpSecurityMode.Plain, "right");
        AdapterOperationRequest move;
        await using (var first = await FtpWorkerSession.StartAsync(left: left, right: right))
        {
            string revision = (await first.RequestAsync("Stat", new { rootKey = "left", path = "same.txt" })).Payload.GetProperty("revision").GetString()!;
            move = new(Guid.NewGuid(), "left", "same.txt", "left", "moved.txt", new(revision));
            left.DropNextRenameAcknowledgement = true;
            AssertCode("MutationOutcomeAmbiguous", await first.RequestAsync("Move", move));
        }
        AdapterOperationRequest delete;
        await using (var second = await FtpWorkerSession.StartAsync(left: left, right: right))
        {
            AdapterControlFrame moved = await second.RequestAsync("Move", move);
            Assert.AreEqual("MutationComplete", moved.MessageType, moved.Payload.GetRawText());
            delete = new(Guid.NewGuid(), "left", "moved.txt", Preconditions: new(moved.Payload.GetProperty("revision").GetString()));
            left.DropNextRenameAcknowledgement = true;
            AssertCode("MutationOutcomeAmbiguous", await second.RequestAsync("Delete", delete));
        }
        await using (var third = await FtpWorkerSession.StartAsync(left: left, right: right))
        {
            Assert.AreEqual("MutationComplete", (await third.RequestAsync("Delete", delete)).MessageType);
            int commands = left.MutationCommands.Count;
            left.ReplaceStoredFile("/same.txt", Encoding.UTF8.GetBytes("external source"));
            left.ReplaceStoredFile("/moved.txt", Encoding.UTF8.GetBytes("external target"));
            AssertCode("RemoteConflict", await third.RequestAsync("Move", move));
            AssertCode("RemoteConflict", await third.RequestAsync("Delete", delete));
            Assert.HasCount(commands, left.MutationCommands);
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("left"), left.ReadStoredFile("/.mp-recovery-" + move.OperationId.ToString("N")));
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("left"), left.ReadStoredFile("/.mp-recovery-" + delete.OperationId.ToString("N")));
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("right"), right.ReadStoredFile("/same.txt"));
        }
    }

    [TestMethod]
    public async Task CancelDuringPublicationPreservesAnUnknownOutcomeForStableReadback()
    {
        await using var session = await FtpWorkerSession.StartAsync();
        string revision = (await session.RequestAsync("Stat", new { rootKey = "left", path = "same.txt" })).Payload.GetProperty("revision").GetString()!;
        Guid request = Guid.NewGuid(), stream = Guid.NewGuid(), operation = Guid.NewGuid(), cancel = Guid.NewGuid();
        byte[] content = Encoding.UTF8.GetBytes("published before cancel");
        session.Left.PublicationReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Left.ContinuePublication = new(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Left.DropNextPublishAcknowledgement = true;
        await session.SendAsync("Upload", request, new
        {
            rootKey = "left",
            path = "same.txt",
            operationId = operation,
            streamId = stream,
            length = content.Length,
            preconditions = new AdapterMutationPreconditions(revision, false)
        });
        Assert.AreEqual("UploadReady", (await session.ReadAsync()).MessageType);
        await session.SendChunkAsync(request, stream, "left", 0, content, true);
        Task? sendCancel = null;
        try
        {
            await session.Left.PublicationReached.Task.WaitAsync(TimeSpan.FromSeconds(20));
            // Drain replies while writing: a serial Worker can apply backpressure
            // until publication reaches a terminal result.
            sendCancel = session.SendAsync("Cancel", cancel, new { rootKey = "left", targetRequestId = request, operationId = operation });
        }
        finally { session.Left.ContinuePublication.TrySetResult(); }
        AdapterControlFrame uncertain = await session.ReadAsync();
        Assert.AreEqual(request, uncertain.RequestId);
        AssertCode("MutationOutcomeAmbiguous", uncertain);
        await sendCancel!;
        AdapterControlFrame ack = await session.ReadAsync();
        Assert.AreEqual(cancel, ack.RequestId);
        Assert.AreEqual("alreadyCompleted", ack.Payload.GetProperty("status").GetString());
        Assert.AreEqual("UploadComplete", (await session.UploadAsync("left", "same.txt", content, operation, new(revision, false))).MessageType);
        Assert.AreEqual(1, session.Left.PublishedUploads);
        CollectionAssert.AreEqual(content, session.Left.ReadStoredFile("/same.txt"));
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("left"), session.Left.ReadStoredFile("/.mp-recovery-" + operation.ToString("N")));
        await AssertCacheClearedAsync(session);
    }

    private static void AssertCode(string code, AdapterControlFrame response)
    {
        Assert.AreEqual("OperationError", response.MessageType);
        Assert.AreEqual(code, response.Payload.GetProperty("code").GetString());
    }

    private static async Task AssertCacheClearedAsync(FtpWorkerSession session)
    {
        for (int attempt = 0; attempt < 20 && Directory.Exists(session.Cache) && Directory.GetFiles(session.Cache).Length != 0; attempt++)
            await Task.Delay(50);
        if (Directory.Exists(session.Cache)) Assert.IsEmpty(Directory.GetFiles(session.Cache));
    }
}

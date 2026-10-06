# MirrorPulse FTP and FTPS Adapter

This is the official repository for the MirrorPulse FTP and FTPS Worker.
Previously released v1 packages remain immutable and available. The development
Worker consumes the fixed published SDK 0.2.1 and negotiates protocol v2 over a
current-user Named Pipe. Each enabled root has its own endpoint, credentials,
connection, addresses and cursor scope. Disabled roots request no credentials and
open no connection. The Host owns configuration and credential storage.

## Configuration and capabilities

Each root supplies `endpoint` (an `ftp://` URI without credentials), `username`,
`credentialReference` and `securityMode`. `ExplicitTls` is the default;
`ImplicitTls` is also supported. `Plain` requires explicit `allowPlaintext=true`.
Both TLS modes encrypt the control and data connections. System certificate trust
is checked; a configured `trustedCertificateSha256` may explicitly pin the exact
certificate. An untrusted certificate is refused before password authentication.
Passwords arrive only through the Host credential exchange and are not logged.

The current v2 development boundary supports directory paging, Stat, bounded
binary range reads and optimistic file uploads. Each root accepts `mutationPolicy`
as `Optimistic` (default) or `ReadOnly`. Read-only roots refuse uploads before
receiving bytes. Move, delete and directory mutations remain unavailable until
their recovery paths are implemented and verified.

Uploads use the SDK transfer lease, a verified sibling staging file, metadata and
full-content checks before publication, and a retained copy of the previous file.
Stable operation IDs bind the root, path, preconditions, length and content hash.
Repeated operations reconcile the remote result rather than blindly republish it.
Unknown results return `MutationOutcomeAmbiguous` with a root-relative recovery
path; previous data and operation evidence are retained for inspection. The remote
names `.mp-stage-<operationId>`, `.mp-recovery-<operationId>` and
`.mp-journal-<operationId>` are reserved and excluded from normal directory pages.
Recovery copies and receipts are retained after success and consume remote space;
they must not be removed while a result is unknown. The Worker stores no local
persistent state; its local transfer lease is removed after completion or failure.

Generic FTP supplies no atomic version condition. Metadata revisions detect visible
size/time changes but do not prove a snapshot against same-size changes with the
same timestamp. Full-content checks detect additional changes, but an external
writer can still race the final check, preservation or rename. Retained copies do
not guarantee capture of the last concurrent edit. This is optimistic synchronization,
with no CAS or exactly-once guarantee.

FluentFTP 54.2.1 supplies the mature connection, TLS, passive data transport and
listing parsers. Raw directory input is limited to 4 MiB, 8,192 lines and 8,192
characters per line before parsing. Unparseable entries, symbolic links and
escaping paths are refused. Pagination is bound to root and path; it does not
bypass the listing budget. Each content frame contains at most 1 MiB. Absolute
paths, traversal, backslashes and control characters are rejected before any FTP
command. Source settings are never stored by the Worker.

## Verification and publication

Run `pwsh ./eng/verify.ps1` for the fixed SDK check, locked restore, Release builds,
complete formatting and actual Worker process tests against disposable FTP/FTPS
endpoints. They exercise two authenticated sources, cursor boundaries, plaintext
consent, stale read rejection, explicit/implicit TLS, invalid certificates and
oversized or escaping listings, multi-frame and empty uploads, retained originals,
operation replay, unchanged-metadata edits, ambiguous publication and upload
cancellation. Test fixtures use no user files or live servers.

Preview candidates are resolved from `develop` as `X.Y.Z-preview.N`. Run the
release workflow with `publish=false` to verify a disposable candidate. Actual
preview publication requires `publish=true` and the exact `PUBLISH` confirmation.
Stable publication starts from a reviewed `develop` PR merged into `main`, with
one `breaking`, `feature` or `fix` classification, and requires the protected
`stable` environment approval.

Each candidate is built once, signed, frozen with its exact source and hashes,
and tested on native x64 and ARM64. The controller consumes fixed SDK 0.2.1
conformance assets and the fixed production Host verifier. The Host profile
checks separate TLS sources, root credentials, CfSharp reads, disabled roots,
private runtime loading and the existing mutation refusal profile. The production
Host mutation profile and remaining write capabilities must pass before formal v2
publication. Production signing keys are supplied only in the
protected signing job; no private key file is read or exported. Existing releases
and tags remain immutable.

Licensed under Apache-2.0. See [LICENSE](LICENSE).

## Package execution

The development package includes a private .NET runtime for `win-x64` and
`win-arm64`, including `createdump.exe`, runtime notices and the exact locked
third-party dependency licenses. Signing and verification use the shared ordinal
canonical inventory. Conformance launches the signed payload with shared runtime
lookup disabled and checks the actual loaded `coreclr.dll` path.

CI runs the source and signed package profiles on native x64 and ARM64 runners.
Original TRX and package hash receipts are retained as artifacts. Organization
signing, production Host acceptance and protected publication remain separate
release gates.

System TLS validates the certificate on each control and data connection through
FluentFTP's public custom stream interface. A data connection using an untrusted
certificate is refused before projecting directory entries or content.

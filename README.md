# MirrorPulse FTP and FTPS Adapter

This optional Adapter connects MirrorPulse to FTP and FTPS servers. Install
the signed `.mpadapter` from this repository's releases in Adapter management
or with `MirrorPulse adapter install PATH`. The Worker uses the published
Adapter SDK 1.0.0 and negotiates protocol v2 over a current-user Named Pipe.
A deployment declares one root. Install the package once and create a
separate deployment for each server or folder.

## Configuration and capabilities

Enter an `ftp://` folder URL without credentials, the username, and a
password in the signed deployment form. The Host saves the password and
provides an opaque reference to the Worker. Choose explicit TLS (the default)
or implicit TLS according to the server. Unencrypted FTP requires an explicit
opt-in in the form.
Both TLS modes encrypt the control and data connections. The system trust
store validates the certificate. The optional `trustedCertificateSha256`
also permits the exact matching certificate, for example on a private server.
A certificate that meets neither condition is refused before password authentication.
Passwords arrive only through the Host credential exchange and are not logged.

The current v2 development boundary supports directory paging, Stat, bounded
binary range reads, optimistic file uploads, same-root file moves, retained file
deletion, directory creation and empty-directory deletion. Each root accepts `mutationPolicy`
as `Optimistic` (default) or `ReadOnly`. Read-only roots refuse uploads before
receiving bytes or issuing mutation commands. Cross-root moves, directory-tree
moves and replacement of an existing move destination are refused explicitly.
File moves retain a verified previous copy; file deletion preserves the source
under its recovery name. Empty-directory deletion uses one nonrecursive `RMD`.
Directories containing user data or reserved recovery evidence cannot be deleted.

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

A broken transport is reconnected through FluentFTP before result readback;
an uncertain mutation is never retried just to restore the connection. Remote
receipts survive Worker restart. Corrupt or oversized receipts block mutation.
Cancellation releases an upload that is still receiving bytes. Publication runs
to a terminal result before queued commands are processed, so a cancellation
acknowledgement does not prove that a remote write was rolled back. Unknown
results still require readback with the original operation ID.

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
cancellation, actual file moves/deletes, nested directory creation, conservative
empty deletion and cross-root/destination refusal, real disconnects, Worker restart,
corrupt evidence and cancellation during publication. Test fixtures use no user files or live servers.

Preview candidates are resolved from `develop` as `X.Y.Z-preview.N`. Run the
release workflow with `publish=false` to verify a disposable candidate. Actual
preview publication requires `publish=true` and the exact `PUBLISH` confirmation.
Stable publication starts from a reviewed `develop` PR merged into `main`, with
one `breaking`, `feature` or `fix` classification, and requires the protected
`stable` environment approval.

Each candidate is built once, signed, frozen with its exact source and hashes,
and tested on native x64 and ARM64. The controller consumes the fixed SDK 1.0.0
conformance assets and pins the product Host source. The exact signed Worker
runs the provider's network suite on both architectures. The product gate
installs the single-root package, validates the signed form, saves credentials
through the Host, and verifies an encrypted listing, a revision-bound range read
and stable upload recovery after the Worker restarts.
Both native profiles must pass against the exact signed candidate before formal
v2 publication. Production signing keys are supplied only in the
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

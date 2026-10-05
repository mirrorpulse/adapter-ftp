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

The current v2 development boundary supports directory paging, Stat and bounded
binary range reads. It refuses destructive or conditional mutations with
`ConditionalMutationUnavailable`; the safe mutation policy is being completed
before a formal v2 release. Generic FTP supplies no atomic version condition.
Metadata revisions detect visible size/time changes but do not prove a snapshot
against same-size changes with the same timestamp. They are never treated as CAS.

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
oversized or escaping listings. Test fixtures use no user files or live servers.

The earlier staged release workflow is retained until the v2 native package and
production Host controller are connected. Its fixed v1 product gate must not be
bypassed to publish a v2 candidate. Private runtimes, dual-architecture signed
conformance and the unified preview/stable controller remain pending development.
Production signing keys are supplied only in the protected signing job; no
private key file is read or exported. Existing releases and tags are not changed.

Licensed under Apache-2.0. See [LICENSE](LICENSE).

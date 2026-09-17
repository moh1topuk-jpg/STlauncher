# Security Policy

## Supported versions

Only the latest release is supported with security fixes.

## Reporting a vulnerability

Please report vulnerabilities privately using GitHub Security Advisories
("Report a vulnerability" on the Security tab) rather than opening a public issue.

Include:

- a description of the issue and its impact,
- steps to reproduce,
- affected version or commit,
- any proof-of-concept or logs.

We aim to acknowledge reports within 72 hours.

## Security model

STlauncher downloads and executes remote code by design (game files, Java runtimes,
mod loaders, mods). The following protections are implemented:

- **Transport** — HTTPS only, certificate validation via the .NET default handler.
- **Integrity** — every Mojang artifact (client jar, libraries, assets) is verified
  against the SHA-1 from the official manifest before use. Third-party artifacts
  (Java runtime) are verified against SHA-256 from the vendor API.
- **Process execution** — arguments are passed via `ProcessStartInfo.ArgumentList`,
  never through string concatenation, preventing argument injection.
- **Archive extraction** — ZipSlip protection when extracting natives and Java
  runtimes: entries resolving outside the destination are rejected.
- **Credentials** — offline mode stores no real credentials. If account-based
  authentication is added later, tokens must be stored in the OS credential store
  (Windows Credential Manager / DPAPI) and never in plaintext.

## Secrets

- The CurseForge API key is **never** committed to the repository. It is read from
  `settings.json` (outside the repo), the `CURSEFORGE_API_KEY` environment variable,
  or the CLI `--api-key` flag.
- Never paste production API keys into issues, pull requests or chat. If a key is
  exposed, revoke it in the CurseForge console and issue a new one.
- `.gitignore` excludes build output; `settings.json` lives in `%APPDATA%` and is
  therefore outside the working tree by design.

## Non-goals

- Verification of mods downloaded from third-party indexes is limited to the hashes
  provided by those indexes.

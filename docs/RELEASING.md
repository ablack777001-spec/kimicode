# Trusted release process

KClaude Desktop separates unsigned development artifacts from trusted release artifacts.

## Development build

```powershell
.\build.ps1
```

This produces only files whose names contain `-unsigned`. GitHub Actions uploads them under the artifact name `KClaudeDesktop-win-x64-UNSIGNED`.

## Signed release build

Before building, install a publicly trusted Authenticode code-signing certificate with its private key in `Cert:\CurrentUser\My`. Do not place a PFX file or password in the repository.

```powershell
.\build.ps1 -Release `
  -SigningCertificateThumbprint '<40-character SHA-1 certificate thumbprint>' `
  -TimestampUrl '<RFC 3161 timestamp URL supplied by the certificate provider>'
```

The release build:

1. requires the .NET 10 SDK and Windows SDK `signtool.exe`;
2. verifies that the selected certificate exists, has a private key, is currently valid, and permits code signing;
3. signs with SHA-256 and requests an RFC 3161 SHA-256 timestamp;
4. runs `signtool verify /pa /all /tw` and independently checks the Authenticode status and signer thumbprint;
5. creates the canonical `KClaudeDesktop.exe` and `KClaudeDesktop-Setup.exe` only after verification;
6. computes `SHA256SUMS.txt` after signing.

Any missing prerequisite, warning, signer mismatch, or verification error stops the release. The repository intentionally does not create self-signed certificates or import signing credentials.

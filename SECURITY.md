# Security

Do not attach passwords, private keys, saved profiles, credential files, terminal
output, or unredacted logs to public issues. Logs and crash reports may contain
hostnames, usernames, file paths and remote error messages. Review them first.

For a suspected vulnerability, use the repository's private vulnerability
reporting feature if enabled. Do not disclose credentials in a public issue.
Rotate any exposed credentials immediately; deleting a file from the current
branch does not remove it from Git history, forks, caches or existing clones.

Before publishing, run:

```powershell
python tools/check-repository.py
python tools/check-repository.py --history
dotnet list SurgeLink.slnx package --vulnerable --include-transitive
```

The scanner checks common key/token formats and risky filenames. It does not
recognize every provider or arbitrary passwords. Also review new configuration,
screenshots and fixtures manually. Keep build output under `artifacts/`; signing
keys, local application data and environment files must remain untracked.

Public Store identity fields and public certificates are not private signing
keys. The packaging manifest is intentionally source controlled. Never publish
a PFX, PEM private key or certificate-store export containing a private key.

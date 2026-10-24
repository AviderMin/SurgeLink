# SurgeLink privacy policy

Effective date: 2026-10-24

SurgeLink is a Windows desktop SSH terminal, file transfer and remote monitoring
application. The application does not include analytics, advertising or an
automatic diagnostic upload service.

Connections are initiated by the user to the servers they select. Usernames,
authentication data, terminal input, remote commands and transferred files are
sent to those servers as required by the selected operation over SSH. Remote
monitoring runs system queries on the connected server. The server operator's
data handling policies apply to information received by that server.

Saved connection profiles (including hostnames, usernames and key file paths),
settings and trusted host fingerprints are stored locally. Remembered passwords
and private-key passphrases are protected using Windows DPAPI for the current
Windows user. Private key files remain at the location selected by the user.
SurgeLink does not copy them into saved profiles.

Optional local CLI access is disabled for every connection until the user explicitly
shares it in the desktop. Sharing allows programs running as the same Windows user
to read that connection's label, host information, visible terminal screen and system
metrics. Directory listings and file reads require confirmation for each request.
Terminal and file contents may contain secrets. SurgeLink does not select or contact
an AI model provider; external agents receiving this data may send it to their own
providers according to their configuration. Desktop privacy masking does not redact
the CLI payload. Sharing is held only in memory and ends on revocation or connection
closure; reconnecting requires new consent. Local operation logs record the method,
opaque session handle and result, without terminal/file payloads.

Diagnostic logs and crash reports are stored locally and can contain hostnames,
usernames, paths and remote error details. They are not automatically sent to
the developer. Review and redact them before sharing. Files downloaded or saved
by the user remain in their chosen locations.

Application-managed connection data and logs are located under
`%LOCALAPPDATA%\SurgeLink` (Windows may redirect this location for a packaged
installation). Close the application before manually removing this directory to
erase these local records. Downloaded files and separately stored SSH keys must
be managed separately. Do not assume uninstall removes every file.

For privacy questions, contact the maintainer through
[the SurgeLink repository](https://github.com/AviderMin/SurgeLink).
Do not post credentials or other private information in a public issue.

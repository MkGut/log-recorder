# Security policy

## Reporting a vulnerability

Please report vulnerabilities privately through GitHub:
**Security > Report a vulnerability** on this repository
(<https://github.com/MkGut/log-recorder/security/advisories/new>). Do not open a public issue or post a
proof of concept publicly before a fix is released.

Include the log version (Settings > Apps, or the file version of `log.exe`), your Windows build, and steps to
reproduce. You should get an acknowledgement within a week.

High-priority areas:

- Recording without the Windows capture picker, without the capture border, or without the tray indicator.
- Any path that uploads, transmits or logs captured pixels, window titles, app names or full user paths.
- Bypasses of the update signature, hash or version checks (`src/log.Shell/Updater.cs`), or ways to make the
  installed copy run code it did not get from a signed release.
- Installer or uninstaller actions outside log's own files and registry keys.
- Local storage exposure beyond the current user's normal profile permissions.

## Supported versions

Only the latest release receives fixes. Installed copies update automatically unless the user turned updates
off.

## Product boundary

log is local-first software. It does not and cannot protect captured media from malware or an administrator
running as or over the same user. It makes no secure-erase or anti-forensic promise.

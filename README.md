# log

`log` is a small, local-first Windows recorder for your daily work. It waits quietly in the tray, asks before it
records anything, shows one big elapsed timer while it records, and turns the finished recording into a
timelapse video you can preview and save.

## How it works

1. **Invitation:** an optional reminder offers `Let's go` or `Not now` without stealing focus.
2. **Consent:** you pick what to share in the Windows capture picker, every time.
3. **Recording:** the window shows the elapsed timer and what is being recorded; Windows draws its capture
   border and the tray icon shows the recording state.
4. **Goodbye:** `Keep going` or `Finish & preview`.
5. **Preview and save:** log renders the timelapse with one fixed preset and saves an MP4 to `Videos\log`.

## Privacy

- Recordings stay on your PC (`%LOCALAPPDATA%\log` and `Videos\log`). No account, upload, telemetry or audio.
- Recordings capture whatever is on screen in the window or display you pick; see
  [known limitations](docs/known-limitations.md) before recording anything sensitive.
- The installed copy checks this project's GitHub Releases for signed updates about twice a day and installs
  them when log is idle. Nothing about you or your recordings is sent. Turn it off from the tray menu
  (**Automatic updates**).

## Install

Download `log-<version>-win-x64.zip` from [Releases](https://github.com/MkGut/log-recorder/releases), extract it
and double-click `log.exe`. It offers a per-user install (no administrator) with a Start menu shortcut, an
Installed apps entry and an optional start at sign-in; updates then arrive automatically. `log.exe --install`
installs silently and `log.exe --uninstall` removes it; recordings are always kept.

The `-portable.zip` keeps everything next to the exe and never installs or updates itself.

Releases are not code-signed yet, so Windows SmartScreen shows a warning on first run. Every release lists
SHA-256 hashes (`SHA256SUMS.txt`) and has a GitHub build provenance attestation:

```powershell
gh attestation verify log.exe --repo MkGut/log-recorder
```

## Code signing policy

log has applied for free code signing provided by [SignPath.io](https://about.signpath.io), certificate by
[SignPath Foundation](https://signpath.org). Until that is approved, releases are unsigned (see above). Once it
is, every Windows release is built by GitHub Actions from this repository and signed only after manual approval.

Team roles:

- Committers and reviewers: [MkGut](https://github.com/MkGut)
- Approvers: [MkGut](https://github.com/MkGut)

Privacy: this program will not transfer any information to other networked systems unless specifically
requested by the user or the person installing or operating it, except the automatic update check described
under [Privacy](#privacy), which only downloads files from this project's GitHub Releases and can be turned off.

## Develop

Windows 11 (build 26100+), x64, .NET 10 SDK and the Visual Studio C++ build tools; see
[environment setup](docs/development/environment.md).

```powershell
powershell -NoProfile -File build\verify.ps1
.\tools\Start-Log.ps1 -Rebuild -DataRoot $env:TEMP\log-test -Show   # isolated test data
```

`log.exe` is a Native AOT Win32 app (`src/log.Shell`), about 7 MB, plus the native Media Foundation encoder
bridge (`src/log.Windows/MediaFoundationBridge.vcxproj`). Releasing is one tag push; see the
[release guide](docs/development/release.md).

## License

Copyright (C) 2026 MkGut. log is free software licensed under the
[GNU Affero General Public License v3.0 only](LICENSE) (`AGPL-3.0-only`). You may use, study, share and
modify it; anyone who distributes a modified version, or offers one to users over a network, must release its
complete source code under the same license. log comes with no warranty.

Third-party notices: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

# Releasing log

`log.exe` is one Native AOT file (about 7 MB) plus `log.MediaFoundationBridge.dll`. There is no runtime to
install and no MSIX.

## Ship an update

1. Merge what you want to release into `main`.
2. Tag and push:

   ```powershell
   git tag v0.2.0
   git push origin v0.2.0
   ```

That is all. `.github/workflows/release.yml` checks the tag, audits dependencies, builds, runs the tests,
packages, signs the update manifest, attests build provenance and publishes the GitHub Release. Installed
copies pick it up on their own within about 12 hours (see below). Versions must increase: the app ignores a
release that is not newer than itself.

Release assets:

| File | Purpose |
|---|---|
| `log-<v>-win-x64.zip` | download and double-click `log.exe` to install |
| `log-<v>-win-x64-portable.zip` | portable mode (`portable.txt`): data beside the exe, no install, no auto-update |
| `log.exe`, `log.MediaFoundationBridge.dll` | fetched by the updater |
| `update.json`, `update.json.sig` | signed update manifest |
| `SHA256SUMS.txt` | hashes of every asset |

Each asset also has a GitHub build provenance attestation: `gh attestation verify log.exe --repo MkGut/log-recorder`.

## How automatic updates work

Only the per-user installed copy (`%LOCALAPPDATA%\Programs\log`) updates itself. About every 12 hours it
downloads `update.json` and `update.json.sig` from the latest release and:

1. verifies the ECDSA P-256 signature against the public key compiled into `src/log.Shell/Updater.cs`;
2. accepts the manifest only if its version is newer than the running exe (no downgrades or replays);
3. downloads `log.exe` and the bridge DLL from that release's tag and checks each SHA-256 against the manifest;
4. stages them as `*.new` and, once log is idle and hidden in the tray (never during a recording or while a
   window is open), swaps them in and restarts quietly in the tray.

A failed check, a bad signature or a hash mismatch changes nothing. Users can turn updates off from the tray
menu (**Automatic updates**) or with the `LOG_NO_UPDATE` environment variable. The only network traffic log
makes is these GitHub downloads; nothing about the user or their recordings is sent.

## The update-signing key

- The private key is the `UPDATE_SIGNING_KEY` Actions secret (PKCS#8 PEM). Only the "Sign update manifest"
  step of the release workflow receives it, after the build has finished.
- Keep an offline backup. If the key is lost, installed copies cannot be updated automatically any more; users
  would have to install a release with a new key by hand.
- If the key leaks, an attacker who can also serve files from this repository's releases could push an update.
  Rotate it: generate a new P-256 key, put its public key in `Updater.cs`, ship that release while the old key
  still signs, then replace the secret.
- `tools/Sign-UpdateManifest.ps1` refuses to sign with a key that does not match `Updater.PublicKey`.

## Code signing (recommended)

Releases are not Authenticode-signed yet, so SmartScreen warns on first run and Smart App Control may block the
exe. To sign, run `tools/Publish-Release.ps1` with `-CertificateThumbprint` or with `-DlibPath` and
`-MetadataPath` (Azure Artifact Signing); `build/Sign-LogBuild.ps1` signs and verifies both binaries. See
[signing.md](signing.md). Wiring signing into CI needs your signing account's credentials as secrets.

## Local package build

```powershell
.\tools\Publish-Release.ps1 -Version 0.2.0
```

Output goes to `artifacts/release/<version>/` (`assets/` and `winget/`). A local build has no
`update.json.sig`, so installed copies ignore it. For winget, validate the generated manifests with
`winget validate` and submit them to `microsoft/winget-pkgs`; winget installs are portable and are updated by
winget, not by log.

## What a user sees

- **Double-click `log.exe`:** it offers a per-user install (no admin): copy to `%LOCALAPPDATA%\Programs\log`, a
  Start menu shortcut, an entry in Settings > Apps, and an optional start at sign-in. Saying no is remembered
  and log runs from where it is.
- **Silent install:** `log.exe --install [--sign-in]`. **Remove:** Settings > Apps, or `log.exe --uninstall`
  (`--quiet` for no prompts). Uninstall deletes only log's own files; recordings, saved videos and anything else
  in the folder are kept.

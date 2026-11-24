# Contributing

Thanks for helping. log is deliberately small: a tray recorder with one consent flow, one timer and one render
preset. Please open an issue to discuss new features before writing them.

Licensing of contributions: log is licensed under the [GNU AGPL v3.0 only](LICENSE). By submitting a
contribution you confirm you wrote it (or have the right to submit it) and agree that it is licensed under
AGPL-3.0-only, the same terms as the rest of the project.

Ground rules:

- Keep it offline and local-first. The only network use is the signed update check; do not add telemetry,
  accounts or uploads.
- Capture stays overt: the Windows picker every time, the capture border, the tray indicator. No audio.
- Encoding uses in-box Media Foundation; no FFmpeg/x264.
- Never commit recordings, screenshots of your desktop, or personal paths. Tests use temporary data
  (`LOG_DATA_ROOT`).

Before sending a pull request:

```powershell
powershell -NoProfile -File build\verify.ps1
powershell -NoProfile -File tools\public\Scan-PublicTree.ps1 -Root .
```

Keep changes narrow, test the failure mode rather than restating the implementation, and say in the pull
request what you could not verify (live capture, hardware, accessibility).

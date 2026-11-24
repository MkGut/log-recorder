# Known limitations

log is pre-release software. This page lists what it does not do, what it cannot protect against, and what
still needs testing on real hardware.

## Trust boundary

log protects against *other local users* (recordings live in your own profile, under per-user permissions) and
against *its own accidental leakage* (no telemetry, no upload, and a log file that records only error types).
It does **not** and cannot protect captured media from malware or an administrator running as or over your
account. It makes no disk-encryption or secure-erase promise: deleted recordings may be recoverable from disk.

## What a recording contains

A screen recording captures everything visible in the window or display you pick, including notifications,
messages from other people, and anything typed in plain view. Pick a single window when you can, close what you
do not want recorded, and review the preview before you save or share the video.

## Network

The only network traffic is the automatic update check of the installed copy: about every 12 hours it downloads
a small signed manifest (and, when there is a new version, the new files) from this project's GitHub Releases.
Like any download, GitHub sees your IP address. Nothing about you or your recordings is sent. Turn it off from
the tray menu (**Automatic updates**) or with the `LOG_NO_UPDATE` environment variable. Portable and winget
copies never update themselves.

## Product limits

- No audio or microphone capture.
- No account, cloud sync, upload or telemetry.
- No covert or hidden recording mode: every recording starts from the Windows capture picker, Windows draws its
  capture border, and the tray icon shows recording state.
- No bundled FFmpeg/x264: encoding uses the Windows Media Foundation H.264 encoder.
- Windows 11 build 26100 or later, x64.
- One fixed render preset (see [render-preset.md](render-preset.md)).
- Adaptive sampling slows disk growth on long recordings (one sample per second at first, stretching to at most
  one per minute) but does not cap it: a recording that runs for days keeps using disk space.
- Releases are not yet Authenticode-signed, so SmartScreen warns on first run and Smart App Control may block
  the exe.

## Still needs hardware verification

- Capture behavior across lock, sleep/resume, secure desktop and minimized windows.
- Power loss and low-disk behavior while recording.
- Hardware versus software encoder selection and capture-time cost.
- Accessibility with Narrator and high-contrast themes on real displays.
- The automatic update path end to end on a range of machines.

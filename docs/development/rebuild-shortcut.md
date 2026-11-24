# Rebuild and launch

`tools\Start-Log.ps1` starts the Native AOT `log.exe` from `artifacts\shell-publish`:

```powershell
.\tools\Start-Log.ps1                       # start the last build (or build it if missing)
.\tools\Start-Log.ps1 -Rebuild              # publish src\log.Shell with Native AOT, then start
.\tools\Start-Log.ps1 -Rebuild -DataRoot $env:TEMP\log-test -Show   # isolated data, window open
```

- It never stops a running `log.exe` for you. A rebuild refuses while the old one is running (the file would be
  locked); exit it from its tray icon first.
- `-DataRoot` sets `LOG_DATA_ROOT`, so a test never touches your real recordings and gets its own
  single-instance lock (an installed copy can run beside it).
- Native AOT needs the Visual Studio C++ build tools; the script puts `vswhere` on `PATH` for you.

A desktop shortcut to this script is intentionally not committed because it holds a machine-specific checkout path.

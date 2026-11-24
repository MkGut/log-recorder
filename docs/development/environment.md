# Development environment

## Requirements

- Windows 11 build 26100 or later, x64.
- .NET 10 SDK (see `global.json`).
- Visual Studio 2022 Build Tools with the C++ x64 tools (the native Media Foundation bridge and the Native AOT
  link both need them).
- An interactive desktop session for anything that captures: the Windows capture picker, tray, lock and sleep
  transitions and the hardware encoder cannot be exercised in hosted CI.

## First commands

From the repository root:

```powershell
powershell -NoProfile -File build\verify.ps1        # restore, native bridge, build, tests, AOT publish
dotnet test log.slnx --configuration Release       # deterministic tests only (after one verify run)
.\tools\Start-Log.ps1 -Rebuild -DataRoot $env:TEMP\log-test -Show
```

`-DataRoot` (the `LOG_DATA_ROOT` variable) keeps test data away from your real recordings and gives the test
copy its own single-instance lock.

CI (`.github/workflows/ci.yml`) runs the same deterministic tests with synthetic sources. They do not prove live
capture, accessibility or signing.

## Windows Smart App Control

On a PC with Smart App Control on, freshly built unsigned test assemblies can be blocked
(`0x800711C7`, "An Application Control policy has blocked this file"). That is not a code failure; run the
affected test project again or use a machine with Smart App Control off.

# Third-party notices

## Shipped in log.exe

`log.exe` is compiled with .NET Native AOT, so it contains portions of the .NET runtime and base class libraries
(MIT License, Copyright (c) .NET Foundation and Contributors). Their license and the notices for components
they include are at <https://github.com/dotnet/runtime/blob/main/LICENSE.TXT> and
<https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT>.

`log.exe` and `log.MediaFoundationBridge.dll` reference no NuGet packages. Windows Media Foundation, Windows
Graphics Capture, Direct2D and the other Windows APIs they call are platform components and are not bundled.
No FFmpeg or x264 code is included.

## Used only to build and test (not shipped)

| Package | License |
|---|---|
| Microsoft.NET.Test.Sdk | MIT |
| xunit | Apache-2.0 |
| xunit.runner.visualstudio | Apache-2.0 |
| CycloneDX (CI SBOM tool) | Apache-2.0 |

Versions are pinned in the project files and `.github/workflows/ci.yml`. CI produces a CycloneDX SBOM for
`log.exe` on every run.

This file is an engineering inventory, not legal advice.

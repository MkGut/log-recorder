# Windows code signing

The shipped files are `log.exe` and `log.MediaFoundationBridge.dll`. `tools/Publish-Release.ps1` calls
`build/Sign-LogBuild.ps1` when it is given signing parameters.

Options:

- **Azure Artifact Signing** (paid, low monthly cost): pass the provider DLL and metadata file.
- **A public RSA code-signing certificate** installed in `CurrentUser\My`: pass its thumbprint.
- **SignPath Foundation** offers free signing to approved open-source projects with a controlled build pipeline.

A self-signed certificate does not satisfy SmartScreen or Smart App Control.

```powershell
pwsh -File .\build\Sign-LogBuild.ps1 -BuildRoot <folder> -CertificateThumbprint '<thumbprint>' -WhatIf
pwsh -File .\build\Sign-LogBuild.ps1 -BuildRoot <folder> -DlibPath '<provider.dll>' -MetadataPath '<metadata.json>'
```

The script needs `signtool.exe` (on `PATH` or in the x64 Windows 10 SDK), uses SHA-256 digests and an RFC 3161
timestamp, rejects reparse points and paths outside the build root, and verifies every signature afterwards.

Code signing is separate from the update-manifest signature described in [release.md](release.md); updates are
verified even when the binaries are not Authenticode-signed.

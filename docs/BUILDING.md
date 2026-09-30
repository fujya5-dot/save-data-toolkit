# Building the Core preview

These instructions apply to the clean public source set. They do not require a paid pack, private verification worker, emulator, ROM, or commercial save. Copy the HTTPS clone URL from this repository's GitHub page:

```powershell
git clone 'PASTE_PUBLIC_REPOSITORY_URL_HERE' save-data-toolkit
cd save-data-toolkit
```

Use Windows 11 x64 and .NET SDK 10.0.400. `global.json` pins that SDK without roll-forward. Windows compatibility details beyond the confirmed environment are being finalized.

From the repository root:

```powershell
dotnet --version
dotnet restore src/YuniRetroToolkit.App/YuniRetroToolkit.App.csproj
dotnet build src/YuniRetroToolkit.App/YuniRetroToolkit.App.csproj -c Debug --no-restore
dotnet run --project src/YuniRetroToolkit.App/YuniRetroToolkit.App.csproj -c Debug --no-restore
```

For a local Release configuration build:

```powershell
dotnet build src/YuniRetroToolkit.App/YuniRetroToolkit.App.csproj -c Release --no-restore
```

The public test entry reuses OSS-safe Generic Safe File and Synthetic Demo tests. It checks generated fixture hashes, pack and definition loading, validation, change previews, backup safety, and fail-closed behavior without commercial data:

```powershell
dotnet restore tests/YuniRetroToolkit.PublicTests/YuniRetroToolkit.PublicTests.csproj
dotnet build tests/YuniRetroToolkit.PublicTests/YuniRetroToolkit.PublicTests.csproj -c Release --no-restore
dotnet run --project tests/YuniRetroToolkit.PublicTests/YuniRetroToolkit.PublicTests.csproj -c Release --no-build --no-restore
```

The runner exits nonzero on a failed test or if Windows Application Control blocks a Core assembly before tests run. It prints a final pass/fail count when tests complete. This is a focused Core smoke suite, not the private full-release test suite.

These commands build locally. They do not produce an approved public release, signed binary, or complete distribution notices. A later public binary release must preserve the Microsoft and upstream license/notice files listed in [Third-party notices](../THIRD_PARTY_NOTICES.md).

## Planned unsigned portable preview

The repository-controlled `tools/release/build-unsigned-preview.ps1` creates a self-contained Windows x64 portable ZIP, its SHA-256 file, and a signing-boundary inventory. The public GitHub Actions workflow runs it after the 19 Core tests and uploads the generated files. This does not create or publish a GitHub Release and does not sign binaries. The ZIP contains the Core, Synthetic Demo, MPL-2.0 license, upstream notices, and per-file hashes; it contains no paid Compatibility Pack.

To use a published portable ZIP, extract it to a folder you control and run `YuniRetroToolkit.App.exe` there. No installer, administrator privilege, or system configuration change is required. To remove the application, close it and delete that extracted folder. Local settings, diagnostics, backups, imported packs, and exported saves may exist outside that folder at locations you chose or within the app's local data folder; review them before deleting. See [Privacy](../PRIVACY.md) for data handling. There is no uninstaller because no installer is provided.

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

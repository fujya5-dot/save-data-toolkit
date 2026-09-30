[CmdletBinding()]
param([string] $OutputRoot = 'artifacts/public-preview')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$out = [IO.Path]::GetFullPath((Join-Path $repo $OutputRoot))
$name = 'SaveDataToolkit-v0.1.0-win-x64'
$folder = Join-Path $out $name
$publish = Join-Path $out '.publish'
if (Test-Path -LiteralPath $out) { throw 'Output root already exists; choose a new path.' }
$sdkOutput = & dotnet --version
if ($LASTEXITCODE -ne 0 -or -not $sdkOutput) { throw 'SDK 10.0.400 is unavailable.' }
$sdk = $sdkOutput.Trim()
if ($sdk -ne '10.0.400') { throw "SDK 10.0.400 required; found $sdk" }
New-Item -ItemType Directory -Path $out | Out-Null
& dotnet publish (Join-Path $repo 'src/YuniRetroToolkit.App/YuniRetroToolkit.App.csproj') -c Release -r win-x64 --self-contained true --no-restore -p:PublishProfile=win-x64-self-contained -p:DebugType=None -p:DebugSymbols=false -p:Deterministic=true -o $publish
if ($LASTEXITCODE -ne 0) { throw 'App publish failed.' }
New-Item -ItemType Directory -Path $folder | Out-Null
Get-ChildItem -LiteralPath $publish -Force | Where-Object Extension -ne '.pdb' | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $folder -Recurse }

$dotnetRoot = Split-Path -Parent (Get-Command dotnet -ErrorAction Stop).Source
$notices = [ordered]@{
    (Join-Path $dotnetRoot 'LICENSE.txt') = 'DOTNET-LICENSE.txt'
    (Join-Path $dotnetRoot 'ThirdPartyNotices.txt') = 'DOTNET-THIRD-PARTY-NOTICES.txt'
    (Join-Path $dotnetRoot 'sdk/10.0.400/Sdks/Microsoft.NET.Sdk.WindowsDesktop/LICENSE.TXT') = 'WPF-LICENSE.txt'
    (Join-Path $dotnetRoot 'sdk/10.0.400/Sdks/Microsoft.NET.Sdk.WindowsDesktop/THIRD-PARTY-NOTICES.TXT') = 'WPF-THIRD-PARTY-NOTICES.txt'
    (Join-Path $repo 'licenses/WINDOWS-SDK-LICENSE.rtf') = 'WINDOWS-SDK-LICENSE.rtf'
    (Join-Path $repo 'licenses/WINDOWS-SDK-THIRD-PARTY-NOTICES.rtf') = 'WINDOWS-SDK-THIRD-PARTY-NOTICES.rtf'
    (Join-Path $repo 'licenses/CSWINRT-LICENSE.txt') = 'CSWINRT-LICENSE.txt'
    (Join-Path $repo 'LICENSE') = 'LICENSE'
    (Join-Path $repo 'THIRD_PARTY_NOTICES.md') = 'THIRD_PARTY_NOTICES.md'
    (Join-Path $repo 'CODE_SIGNING_POLICY.md') = 'CODE_SIGNING_POLICY.md'
    (Join-Path $repo 'PRIVACY.md') = 'PRIVACY.md'
}
foreach ($entry in $notices.GetEnumerator()) {
    if (-not (Test-Path -LiteralPath $entry.Key -PathType Leaf)) { throw "Missing notice: $($entry.Key)" }
    Copy-Item -LiteralPath $entry.Key -Destination (Join-Path $folder $entry.Value)
}
$required = @('YuniRetroToolkit.App.exe', 'hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll', 'PresentationFramework.dll', 'Microsoft.Windows.SDK.NET.dll', 'WinRT.Runtime.dll', 'demo/yuni.demo.synthetic-1.0.0.yrtpack')
foreach ($relative in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $folder $relative) -PathType Leaf)) { throw "Missing distribution file: $relative" }
}
$runtime = Get-Content -Raw -LiteralPath (Join-Path $folder 'YuniRetroToolkit.App.runtimeconfig.json') | ConvertFrom-Json
if (@($runtime.runtimeOptions.PSObject.Properties.Name) -match '^frameworks?$') { throw 'Not self-contained.' }
$bad = @(Get-ChildItem -LiteralPath $folder -File -Recurse | Where-Object {
    $_.Extension -in @('.pdb','.lib','.exp','.ilk','.tmp','.pfx','.p12','.pem','.key','.snk','.smc','.sfc','.rom') -or
    $_.Name -match '(?i)(private.?key|secret|shin.?momotaro|ares|verifier.?worker|compatibility.?pack)'
})
if ($bad.Count) { throw "Unexpected private, commercial, debug, or secret-shaped file: $($bad.Name -join ', ')" }
$actualExe = @(Get-ChildItem -LiteralPath $folder -File -Recurse -Filter '*.exe' | ForEach-Object { [IO.Path]::GetRelativePath($folder, $_.FullName).Replace('\','/') })
if (Compare-Object ($actualExe | Sort-Object) @('YuniRetroToolkit.App.exe','createdump.exe')) { throw 'Unexpected EXE inventory.' }

$firstParty = @(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'windows-authenticode-files.txt') | ForEach-Object Trim | Where-Object { $_ -and -not $_.StartsWith('#') })
if ($firstParty.Count -ne 8) { throw 'First-party signing boundary must contain eight files.' }
$inventory = foreach ($relative in $firstParty) {
    $file = Join-Path $folder $relative
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing first-party binary: $relative" }
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($file)
    if ($info.ProductName -ne 'Save Data Toolkit') { throw "Wrong ProductName in $relative`: $($info.ProductName)" }
    if ($info.ProductVersion -notin @('0.1.0','0.1.0.0')) { throw "Wrong ProductVersion in $relative`: $($info.ProductVersion)" }
    [ordered]@{ path = $relative; sha256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant(); owner = 'YuniWorks'; futureSigning = 'SIGN_YUNIWORKS'; productName = $info.ProductName; productVersion = $info.ProductVersion }
}
$allPe = @(Get-ChildItem -LiteralPath $folder -File -Recurse | Where-Object Extension -in @('.exe','.dll'))
$upstream = @($allPe | Where-Object { [IO.Path]::GetRelativePath($folder, $_.FullName).Replace('\','/') -notin $firstParty })
if ($upstream.Count -lt 1) { throw 'Expected upstream binaries missing.' }
[ordered]@{
    schemaVersion = '1.0.0'; product = 'Save Data Toolkit'; version = '0.1.0'; deployment = 'portable-self-contained-win-x64'; signingStatus = 'UNSIGNED'
    firstParty = $inventory; upstreamBinaryCount = $upstream.Count; upstreamRule = 'DO_NOT_SIGN_UPSTREAM'
    upstreamPaths = @($upstream | ForEach-Object { [IO.Path]::GetRelativePath($folder, $_.FullName).Replace('\','/') } | Sort-Object)
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $out 'signing-boundary.json') -Encoding utf8NoBOM

$hashes = foreach ($file in Get-ChildItem -LiteralPath $folder -File -Recurse | Sort-Object FullName) {
    $relative = [IO.Path]::GetRelativePath($folder, $file.FullName).Replace('\','/')
    "{0}  {1}" -f (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $relative
}
[IO.File]::WriteAllLines((Join-Path $folder 'SHA256SUMS.txt'), $hashes, [Text.UTF8Encoding]::new($false))
$zip = Join-Path $out ($name + '-UNSIGNED.zip')
Compress-Archive -Path $folder -DestinationPath $zip -CompressionLevel Optimal
$zipHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $out 'ARTIFACTS-SHA256.txt'), "$zipHash  $([IO.Path]::GetFileName($zip))`n", [Text.UTF8Encoding]::new($false))
Write-Output "UNSIGNED_ZIP=$zip"
Write-Output "SHA256=$zipHash"
Write-Output "FIRST_PARTY=$($firstParty.Count)"
Write-Output "UPSTREAM_DO_NOT_SIGN=$($upstream.Count)"

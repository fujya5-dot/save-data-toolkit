param([Parameter(Mandatory = $true)][string]$OutputDirectory)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($output) | Out-Null
[byte[]]$identity = [byte[]]::new(32)
[Array]::Fill[byte]($identity, 0x44)
[byte[]]$save = [byte[]]::new(64)
$save[0] = 0x53
$save[1] = 1
$save[2] = 1
$save[3] = 0x44
$save[4] = 0x54
$save[5] = 0x4b
$sum = 0
for ($index = 0; $index -lt 63; $index++) { $sum = ($sum + $save[$index]) -band 0xff }
$save[63] = [byte]$sum
$identityPath = Join-Path $output 'synthetic-demo.identity'
$savePath = Join-Path $output 'synthetic-demo.srm'
[IO.File]::WriteAllBytes($identityPath, $identity)
[IO.File]::WriteAllBytes($savePath, $save)
$identityHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($identity)).ToLowerInvariant()
$saveHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($save)).ToLowerInvariant()
if ($identityHash -cne 'bb391415c05e39d77ca17381d3be3f7d0cd5e5332e5a579311adaa0aa62106e9') { throw 'Synthetic identity hash mismatch.' }
if ($saveHash -cne '106bd535d0575ab6e52cd87749dce6c7017ad8e6176d43a2e9bd048dd62454e7') { throw 'Synthetic save hash mismatch.' }
[ordered]@{identityPath=$identityPath;identitySha256=$identityHash;savePath=$savePath;saveSha256=$saveHash;checksum=$save[63]} | ConvertTo-Json -Compress

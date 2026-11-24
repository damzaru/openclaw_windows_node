[CmdletBinding()]
param(
  [string]$RepoPath = (Join-Path $PSScriptRoot '..'),
  [string]$GatewaySourcePath = '',
  [string]$NodeVersion = 'v24.14.0',
  [string]$McpVersion = '1.12.0'
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath $RepoPath).Path
$runtime = Join-Path $repo 'src\OpenClaw.Node\browser-runtime'
if (-not $GatewaySourcePath) { $GatewaySourcePath = Join-Path (Split-Path $repo -Parent) 'openclaw_source' }
$patchName = "chrome-devtools-mcp@$McpVersion.patch"
$patchSource = Join-Path $GatewaySourcePath "patches\$patchName"
if ($McpVersion -ne '1.12.0' -or -not (Test-Path -LiteralPath $patchSource)) {
  throw 'This migration is pinned to Gateway chrome-devtools-mcp 1.12.0 and its snapshot-identity backport.'
}
$stage = Join-Path ([IO.Path]::GetTempPath()) ("OpenClawBrowserVendor-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
function Remove-CheckedDirectory([string]$Path, [string]$Parent) {
  $full = [IO.Path]::GetFullPath($Path)
  $root = [IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
  if (-not $full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing cleanup outside $root" }
  if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
}
try {
  $package = Join-Path $stage 'package'
  $metadata = (npm view "chrome-devtools-mcp@$McpVersion" dist --json | Out-String | ConvertFrom-Json)
  if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the pinned npm artifact.' }
  $integrity = 'sha512-lAGpH0zXT1ZdaPefP1KK/MvxwihOlu7VGQ7p4wbhD9OhN17vUC4KWk/ImUYR9DE4s+7r4r0ySV4xr2OkTa7ZMA=='
  if ($metadata.integrity -ne $integrity) { throw 'Pinned MCP artifact integrity changed.' }
  $tarball = Join-Path $stage 'mcp.tgz'
  Invoke-WebRequest -UseBasicParsing -Uri $metadata.tarball -OutFile $tarball
  $hash = [Security.Cryptography.SHA512]::Create()
  try { $actual = 'sha512-' + [Convert]::ToBase64String($hash.ComputeHash([IO.File]::ReadAllBytes($tarball))) } finally { $hash.Dispose() }
  if ($actual -ne $integrity) { throw 'MCP tarball integrity check failed.' }
  tar -xzf $tarball -C $stage
  if ($LASTEXITCODE -ne 0) { throw 'Could not extract MCP artifact.' }
  git -C $package apply --check $patchSource
  if ($LASTEXITCODE -ne 0) { throw 'Gateway snapshot patch does not apply to the pinned MCP artifact.' }
  git -C $package apply $patchSource
  if ($LASTEXITCODE -ne 0) { throw 'Could not apply Gateway snapshot patch.' }
  Push-Location $package
  try {
    npm install --omit=dev --ignore-scripts --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw 'MCP production dependency installation failed.' }
  } finally { Pop-Location }
  $nodeStage = Join-Path $stage 'node'
  New-Item -ItemType Directory -Path $nodeStage | Out-Null
  $nodeZip = Join-Path $stage 'node.zip'
  $dist = "node-$NodeVersion-win-x64"
  Invoke-WebRequest -UseBasicParsing -Uri "https://nodejs.org/dist/$NodeVersion/$dist.zip" -OutFile $nodeZip
  $sums = (Invoke-WebRequest -UseBasicParsing -Uri "https://nodejs.org/dist/$NodeVersion/SHASUMS256.txt").Content
  $expected = ([regex]::Match($sums, "(?m)^([a-f0-9]{64})\s+$([regex]::Escape($dist)).zip\r?$")).Groups[1].Value
  if (-not $expected -or (Get-FileHash -LiteralPath $nodeZip -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw 'Node distribution checksum failed.' }
  Expand-Archive -LiteralPath $nodeZip -DestinationPath $stage
  foreach ($name in @('node.exe','LICENSE','README.md')) { Copy-Item -LiteralPath (Join-Path $stage "$dist\$name") -Destination $nodeStage }
  # All verification happens before replacing vendored payloads. Runtime user profiles are not touched.
  New-Item -ItemType Directory -Force -Path $runtime | Out-Null
  foreach ($name in @('chrome-devtools-mcp','node')) { Remove-CheckedDirectory (Join-Path $runtime $name) $runtime }
  Move-Item -LiteralPath $package -Destination (Join-Path $runtime 'chrome-devtools-mcp')
  Move-Item -LiteralPath $nodeStage -Destination (Join-Path $runtime 'node')
  $patchDir = Join-Path $repo 'src\OpenClaw.Node\browser-runtime-patches'
  New-Item -ItemType Directory -Force -Path $patchDir | Out-Null
  Copy-Item -LiteralPath $patchSource -Destination (Join-Path $patchDir $patchName)
  [ordered]@{ nodeVersion=$NodeVersion; chromeDevtoolsMcpVersion=$McpVersion; npmIntegrity=$integrity; gatewayPatchSha256=(Get-FileHash -LiteralPath $patchSource -Algorithm SHA256).Hash.ToLowerInvariant() } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runtime 'versions.json') -Encoding UTF8
  Write-Output "Vendored Chrome DevTools MCP $McpVersion with Gateway snapshot patch, Node $NodeVersion."
} finally { Remove-CheckedDirectory $stage ([IO.Path]::GetTempPath()) }

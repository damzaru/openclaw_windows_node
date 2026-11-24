[CmdletBinding(SupportsShouldProcess)]
param(
  [string]$RepoPath = "",
  [string]$GatewaySourcePath = "",
  [string]$GatewayVersion = "",
  [switch]$Check
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($RepoPath)) { $RepoPath = Join-Path $PSScriptRoot '..' }
$repo = (Resolve-Path -LiteralPath $RepoPath).Path
if ([string]::IsNullOrWhiteSpace($GatewaySourcePath)) { $GatewaySourcePath = Join-Path (Split-Path $repo -Parent) 'openclaw_source' }
$gateway = (Resolve-Path -LiteralPath $GatewaySourcePath).Path
$sourceVersion = (Get-Content -LiteralPath (Join-Path $gateway 'package.json') -Raw | ConvertFrom-Json).version
if ($sourceVersion -notmatch '^\d{4}\.\d{1,2}\.\d{1,2}(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$') {
  throw "Gateway source version '$sourceVersion' cannot be represented by this Windows date-version build."
}
$gatewayCommit = (git -C $gateway rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $gatewayCommit -notmatch '^[0-9a-f]{40}$') { throw 'Cannot read Gateway source commit.' }
$path = Join-Path $repo 'src\OpenClaw.Node\Version.props'
[xml]$version = Get-Content -LiteralPath $path -Raw
$fields = $version.Project.PropertyGroup
$publicVersion = if ([string]::IsNullOrWhiteSpace($GatewayVersion)) { [string]$fields.GatewayVersion } else { $GatewayVersion }
if ($publicVersion -notmatch '^\d{4}\.\d{1,2}\.\d{1,2}(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$') { throw 'Invalid public Gateway version.' }
$matchesGateway = $fields.GatewaySourceVersion -eq $sourceVersion -and $fields.GatewaySourceCommit -eq $gatewayCommit -and $fields.GatewayVersion -eq $publicVersion
if ($Check) {
  if (-not $matchesGateway) { throw 'Companion version metadata is not synchronized with the Gateway checkout. Run bump-build.ps1.' }
  Write-Output "App $publicVersion; reference source $sourceVersion ($gatewayCommit); synced $($fields.GatewaySyncDate); internal revision $($fields.CompanionRevision)"
  return
}
$revision = if ($matchesGateway) { [int]$fields.CompanionRevision + 1 } else { 0 }
if ($revision -gt 65534) { throw 'Companion revision exceeds the Windows file-version limit.' }
if ($PSCmdlet.ShouldProcess($path, "Sync source $sourceVersion ($gatewayCommit), public version $publicVersion, internal revision $revision")) {
  $fields.GatewayVersion = $publicVersion
  $fields.GatewaySourceVersion = $sourceVersion
  $fields.GatewaySourceCommit = $gatewayCommit
  $fields.GatewaySyncDate = Get-Date -Format 'yyyy-MM-dd'
  $fields.CompanionRevision = [string]$revision
  $version.Save($path)
}
Write-Output "$publicVersion (reference $sourceVersion/$($gatewayCommit.Substring(0,12)), internal revision $revision)"

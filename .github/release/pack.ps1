# Run from the root of a clean Windows checkout. Never package a running installation.
[CmdletBinding(DefaultParameterSetName='Installer')]
param(
    [Parameter(Mandatory, ParameterSetName='Installer')][ValidatePattern('^1\.0\.[1-9][0-9]*$')][string]$Version,
    [Parameter(Mandatory, ParameterSetName='Installer')][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9_-][A-Za-z0-9_.-]*$')][string]$Repository,
    [Parameter(Mandatory)][string]$Stage,
    [Parameter(Mandatory, ParameterSetName='Installer')][string]$Vpk,
    [Parameter(Mandatory, ParameterSetName='Portable')][switch]$PublishOnly
)
$ErrorActionPreference = 'Stop'
$Stage = [IO.Path]::GetFullPath($Stage)
if (Test-Path -LiteralPath $Stage) { throw 'Packaging requires a new, empty staging location.' }
if (!(Test-Path -LiteralPath 'src/OpenClaw.Node/Version.props')) { throw 'Run from the checkout root.' }
$null = New-Item -ItemType Directory -Path $Stage
$publish = Join-Path $Stage 'publish'
$release = Join-Path $Stage 'release'
$pin = Get-Content -Raw src/OpenClaw.Node/browser-runtime/versions.json | ConvertFrom-Json
if ($pin.nodeVersion -notmatch '^v\d+\.\d+\.\d+$' -or $pin.nodeWinX64Sha256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'Invalid runtime pin.' }
$runtime = 'src/OpenClaw.Node/browser-runtime/node/node.exe'
if (!(Test-Path -LiteralPath $runtime)) {
    Invoke-WebRequest -UseBasicParsing "https://nodejs.org/dist/$($pin.nodeVersion)/win-x64/node.exe" -OutFile $runtime
}
if ((Get-FileHash $runtime).Hash -ne $pin.nodeWinX64Sha256) { throw 'Node runtime digest mismatch.' }
dotnet publish src/OpenClaw.Node/OpenClaw.Node.csproj -c Release -f net8.0-windows -r win-x64 --self-contained true -p:Platform=x64 "-p:ReleaseRepository=$Repository" "-p:WindowsPackageBuild=$Version" -p:ContinuousIntegrationBuild=true -p:DebugType=None -p:DebugSymbols=false -o $publish
if ($LASTEXITCODE) { throw 'Publish failed.' }
# The recorder's NuGet package unconditionally copies its native debug symbols.
$recorderSymbols = Join-Path $publish 'ScreenRecorderLib.pdb'
if (Test-Path -LiteralPath $recorderSymbols -PathType Leaf) { Remove-Item -LiteralPath $recorderSymbols }
foreach ($file in (Get-ChildItem -LiteralPath $publish -Recurse -Force)) {
    if ($file.Name -match '^(?:managed-profile|User Data|Local State|Login Data|Cookies|\.git|\.codex|\.ssh|AGENTS\.md|\.env(?:\..*)?)$|\.(?:pdb|log|pfx|pem|key)$' -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Refusing to package unexpected private or debug material.'
    }
}
if ((Get-FileHash (Join-Path $publish 'runtimes/browser/node/node.exe')).Hash -ne $pin.nodeWinX64Sha256) { throw 'Published runtime is missing or invalid.' }
if ($PSCmdlet.ParameterSetName -eq 'Portable') { return }
& $Vpk pack --packId OpenClawCompanion --packTitle 'OpenClaw Companion' --packAuthors 'Companion Maintainers' --packVersion $Version --packDir $publish --mainExe OpenClaw.Node.exe --icon src/OpenClaw.Node/Tray/Assets/openclaw-claw.ico --channel win --runtime win-x64 --delta None --noPortable --instLocation PerUser --shortcuts StartMenuRoot --outputDir $release
if ($LASTEXITCODE) { throw 'Installer packaging failed.' }
$feed = Get-Content -Raw (Join-Path $release 'releases.win.json') | ConvertFrom-Json
if (@($feed.Assets).Count -ne 1 -or $feed.Assets[0].FileName -cne "OpenClawCompanion-$Version-full.nupkg" -or $feed.Assets[0].Type -ne 'Full') { throw 'Unexpected package feed.' }
$setup = @(Get-ChildItem -LiteralPath $release -Filter '*-Setup.exe' -File)
if ($setup.Count -ne 1) { throw 'Expected one setup executable.' }
if ($setup[0].Name -cne 'OpenClawCompanion-Setup.exe') { Rename-Item -LiteralPath $setup[0].FullName -NewName 'OpenClawCompanion-Setup.exe' }

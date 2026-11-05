# Builds WinClips: runs the tests, publishes a single self-contained exe and the MSI installer.
# Output: artifacts\WinClips-<version>-x64.msi and artifacts\WinClips-<version>-x64-portable.exe
param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dotnet = 'dotnet'
if (-not (Get-Command $dotnet -ErrorAction SilentlyContinue)) { $dotnet = "$env:ProgramFiles\dotnet\dotnet.exe" }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$appProj = Join-Path $root 'src\ClipboardHistory\ClipboardHistory.csproj'
$version = ([xml](Get-Content $appProj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'publish'
Remove-Item -Recurse -Force $artifacts -ErrorAction SilentlyContinue

if (-not $SkipTests) {
    & $dotnet test (Join-Path $root 'ClipboardHistory.sln') -c Release
    if ($LASTEXITCODE) { throw 'tests failed' }
}

& $dotnet publish $appProj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -o $publish
if ($LASTEXITCODE) { throw 'publish failed' }

& $dotnet build (Join-Path $root 'installer\WinClips.Installer.wixproj') -c Release `
    -p:Version=$version -p:PublishDir="$publish\" -o $artifacts
if ($LASTEXITCODE) { throw 'installer build failed' }

Copy-Item (Join-Path $publish 'WinClips.exe') (Join-Path $artifacts "WinClips-$version-x64-portable.exe")
Get-ChildItem $artifacts -File | Where-Object { $_.Extension -in '.msi', '.exe' } |
    ForEach-Object { '{0}  {1:N1} MB' -f $_.Name, ($_.Length / 1MB) }

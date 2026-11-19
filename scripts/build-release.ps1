param(
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$version = '1.0.1'
$localDotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path $localDotnet) { $localDotnet } else { 'dotnet' }
$localRuntimePacks = Join-Path $root '.tools\packages'
$nugetConfig = if (Test-Path $localRuntimePacks) {
    Join-Path $root 'scripts\NuGet.offline.config'
} else {
    Join-Path $root 'NuGet.Config'
}

if ($dotnet -ne 'dotnet') {
    $env:DOTNET_CLI_HOME = Join-Path $root '.tools\dotnet-home'
    $env:APPDATA = Join-Path $root '.tools\appdata\Roaming'
    $env:LOCALAPPDATA = Join-Path $root '.tools\appdata\Local'
    $env:USERPROFILE = Join-Path $root '.tools\userprofile'
    $env:HOME = Join-Path $root '.tools\userprofile'
    New-Item -ItemType Directory -Force $env:DOTNET_CLI_HOME, $env:APPDATA, $env:LOCALAPPDATA, $env:USERPROFILE | Out-Null
}

$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:NUGET_PACKAGES = Join-Path $root '.tools\nuget'

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) {
    throw 'Не найден компилятор C# для .NET Framework 4.x (csc.exe). Установите .NET Framework 4.8.'
}

Write-Host "== Universal Search Launcher ${version}: release build =="
Write-Host "dotnet: $dotnet"
Write-Host "NuGet config: $nugetConfig"

New-Item -ItemType Directory -Force 'artifacts', 'dist' | Out-Null

Write-Host '-- Restore --'
& $dotnet restore 'src\UniversalSearchLauncher\UniversalSearchLauncher.csproj' -r win-x64 --configfile $nugetConfig --nologo
if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
& $dotnet restore 'tests\UniversalSearchLauncher.Tests\UniversalSearchLauncher.Tests.csproj' --configfile $nugetConfig --nologo
if ($LASTEXITCODE -ne 0) { throw 'Test restore failed.' }

if (-not $SkipTests) {
    Write-Host '-- Tests --'
    & $dotnet run --project 'tests\UniversalSearchLauncher.Tests\UniversalSearchLauncher.Tests.csproj' -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}

Write-Host '-- Publish self-contained app --'
if (Test-Path 'artifacts\publish') { Remove-Item 'artifacts\publish' -Recurse -Force }
& $dotnet publish 'src\UniversalSearchLauncher\UniversalSearchLauncher.csproj' -c Release -r win-x64 --self-contained true --no-restore -o 'artifacts\publish' -p:PublishSingleFile=false -p:PublishReadyToRun=false --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

Write-Host '-- Uninstaller --'
& $csc /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /win32icon:assets\app.ico /win32manifest:installer\setup.manifest /out:artifacts\Uninstall.exe installer\Uninstaller.cs /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll
if ($LASTEXITCODE -ne 0) { throw 'Uninstaller compilation failed.' }
Copy-Item 'artifacts\Uninstall.exe' 'artifacts\publish\Uninstall.exe' -Force

Write-Host '-- Payload archive --'
if (Test-Path 'artifacts\payload.zip') { Remove-Item 'artifacts\payload.zip' -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    (Resolve-Path 'artifacts\publish').Path,
    (Join-Path $root 'artifacts\payload.zip'),
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false)

Write-Host '-- Setup executable --'
$setupPath = Join-Path $root "dist\UniversalSearchLauncher-Setup-$version.exe"
& $csc /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /win32icon:assets\app.ico /win32manifest:installer\setup.manifest "/resource:artifacts\payload.zip,Payload.zip" /out:$setupPath installer\Setup.cs /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:Microsoft.CSharp.dll
if ($LASTEXITCODE -ne 0) { throw 'Setup compilation failed.' }

Write-Host '-- Portable archive --'
$portableStage = Join-Path $root 'artifacts\portable-stage'
if (Test-Path $portableStage) { Remove-Item $portableStage -Recurse -Force }
New-Item -ItemType Directory -Force $portableStage | Out-Null
Copy-Item 'artifacts\publish\*' $portableStage -Recurse -Force
Remove-Item (Join-Path $portableStage 'Uninstall.exe') -Force -ErrorAction SilentlyContinue
$portablePath = Join-Path $root "dist\UniversalSearchLauncher-$version-portable.zip"
if (Test-Path $portablePath) { Remove-Item $portablePath -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $portableStage,
    $portablePath,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false)

Write-Host '-- Source archive --'
$sourcePath = Join-Path $root "dist\UniversalSearchLauncher-$version-source.zip"
if (Test-Path $sourcePath) { Remove-Item $sourcePath -Force }
$sourceStage = Join-Path $root 'artifacts\source-stage'
if (Test-Path $sourceStage) { Remove-Item $sourceStage -Recurse -Force }
New-Item -ItemType Directory -Force $sourceStage | Out-Null
$sourceItems = @('src', 'tests', 'installer', 'scripts', 'docs', 'assets', 'README.md', 'NuGet.Config', 'UniversalSearchLauncher.sln', '.gitignore')
foreach ($item in $sourceItems) {
    if (-not (Test-Path $item)) { continue }
    $destination = Join-Path $sourceStage $item
    New-Item -ItemType Directory -Force (Split-Path -Parent $destination) | Out-Null
    Copy-Item $item $destination -Recurse -Force
}
Get-ChildItem $sourceStage -Recurse -Directory | Where-Object { $_.Name -in @('bin', 'obj', 'NuGet') } | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
Get-ChildItem $sourceStage -Recurse -Directory | Sort-Object FullName -Descending | Where-Object { -not (Get-ChildItem $_.FullName -Force -ErrorAction SilentlyContinue) } | Remove-Item -Force -ErrorAction SilentlyContinue
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $sourceStage,
    $sourcePath,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false)

Write-Host '-- Checksums --'
$artifacts = Get-ChildItem 'dist' -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | Sort-Object Name
$lines = foreach ($file in $artifacts) {
    $hash = (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($file.Name)"
}
$lines | Set-Content (Join-Path $root 'dist\SHA256SUMS.txt') -Encoding ASCII

Write-Host ''
Get-ChildItem 'dist' | Select-Object Name, @{Name = 'SizeMB'; Expression = { [Math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
Write-Host 'Build completed.'

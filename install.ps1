param(
    [string]$Dll,
    [string]$SimHub,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not $SimHub) {
    if ($env:SIMHUB_INSTALL_PATH) { $SimHub = $env:SIMHUB_INSTALL_PATH }
    else { $SimHub = "C:\Program Files (x86)\SimHub" }
}
$SimHub = $SimHub.TrimEnd('\')

$simHubExe = Join-Path $SimHub "SimHubWPF.exe"
if (-not (Test-Path $simHubExe)) {
    Write-Error "Не найден SimHub: $simHubExe. Укажите папку в переменной SIMHUB_INSTALL_PATH."
}

if (-not $SkipBuild) {
    dotnet build (Join-Path $root "User.AppVolumeController.csproj") -c Release
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if (-not $Dll) {
    $Dll = Join-Path $root "bin\Release\User.AppVolumeController.dll"
}
if (-not (Test-Path $Dll)) {
    Write-Error "Не найден файл плагина: $Dll"
}

$destination = Join-Path $SimHub "User.AppVolumeController.dll"
$previous = "$destination.old"
if (Test-Path $previous) {
    Remove-Item $previous -Force -ErrorAction SilentlyContinue
}

$copied = $false
try {
    Copy-Item -Force $Dll $destination
    $copied = $true
}
catch {
    if (Test-Path $previous) {
        Remove-Item $previous -Force -ErrorAction SilentlyContinue
    }
    [IO.File]::Move($destination, $previous)
    Copy-Item -Force $Dll $destination
    $copied = $true
    Write-Host "SimHub был открыт: старый файл переименован, новый уже на месте."
}

if (-not $copied) {
    Write-Error "Не удалось скопировать плагин в $destination"
}

Write-Host "Плагин установлен: $destination"
Write-Host "Полностью закройте SimHub, вместе со значком в трее, и откройте снова."

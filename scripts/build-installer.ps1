<#
.SYNOPSIS
    Construit l'installeur : publication autonome win-x64 (aucun .NET requis chez le client)
    puis compilation Inno Setup. Résultat : installer\Output\LEWEBMAX-MailMigrator-Setup-<version>.exe

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1
#>
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

# Version unique, lue dans Directory.Build.props.
[xml]$props = Get-Content (Join-Path $root "Directory.Build.props")
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
Write-Host "Version $version"

# Compilateur Inno Setup (winget install JRSoftware.InnoSetup).
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { Write-Error "Inno Setup 6 introuvable. Installez-le avec : winget install JRSoftware.InnoSetup" }

# 1. Tests, puis publication autonome.
dotnet test (Join-Path $root "tests\MailMigrator.Tests") -c Release -v q --nologo
if ($LASTEXITCODE -ne 0) { Write-Error "Les tests ont échoué : installeur non construit." }

$output = Join-Path $root "publish\win-x64"
if (Test-Path $output) { Remove-Item -Recurse -Force $output }
dotnet publish (Join-Path $root "src\MailMigrator.App") -c Release -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true -p:DebugType=none -o $output -v q --nologo
if ($LASTEXITCODE -ne 0) { Write-Error "Échec de la publication." }

# 2. Installeur.
& $iscc "/DAppVersion=$version" (Join-Path $root "installer\MailMigrator.iss")
if ($LASTEXITCODE -ne 0) { Write-Error "Échec de la compilation Inno Setup." }

$setup = Join-Path $root "installer\Output\LEWEBMAX-MailMigrator-Setup-$version.exe"
Write-Host ("Installeur prêt : {0} ({1:N1} Mo)" -f $setup, ((Get-Item $setup).Length / 1MB))

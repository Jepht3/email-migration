<#
.SYNOPSIS
    Lance les tests d'intégration IMAP contre un serveur GreenMail local, sans Docker.

.DESCRIPTION
    1. Vérifie Java (winget install EclipseAdoptium.Temurin.21.JRE) ;
    2. télécharge GreenMail standalone si besoin (somme SHA-1 vérifiée) ;
    3. démarre GreenMail (IMAP sur 127.0.0.1:3143, comptes source@test.local et destination@test.local) ;
    4. exécute les tests de la catégorie « Integration », puis arrête le serveur.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\test-integration.ps1
#>
param(
    [string]$GreenMailVersion = "2.1.14",
    [int]$Port = 3143
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$toolDir = Join-Path $root "tools\greenmail"
$jar = Join-Path $toolDir "greenmail-standalone.jar"

# --- Java ---------------------------------------------------------------
$javaCommand = Get-Command java -ErrorAction SilentlyContinue
$java = if ($javaCommand) { $javaCommand.Source } else { $null }
if (-not $java) {
    $java = Get-ChildItem "$env:ProgramFiles\Eclipse Adoptium\*\bin\java.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $java) {
    Write-Error "Java est introuvable. Installez-le avec : winget install EclipseAdoptium.Temurin.21.JRE"
}

# --- GreenMail ----------------------------------------------------------
if (-not (Test-Path $jar)) {
    New-Item -ItemType Directory -Force $toolDir | Out-Null
    $url = "https://repo1.maven.org/maven2/com/icegreen/greenmail-standalone/$GreenMailVersion/greenmail-standalone-$GreenMailVersion.jar"
    Write-Host "Téléchargement de GreenMail $GreenMailVersion…"
    Invoke-WebRequest $url -OutFile $jar -UseBasicParsing
    $expected = (Invoke-RestMethod "$url.sha1").Trim()
    $actual = (Get-FileHash $jar -Algorithm SHA1).Hash.ToLower()
    if ($expected -ne $actual) {
        Remove-Item $jar
        Write-Error "Somme de contrôle GreenMail invalide : fichier supprimé."
    }
}

$arguments = @(
    "-Dgreenmail.setup.test.imap",
    "-Dgreenmail.hostname=127.0.0.1",
    "-Dgreenmail.users=source:source@test.local,destination:destination@test.local",
    "-Dgreenmail.users.login=email",
    "-jar", "`"$jar`""
)
Write-Host "Démarrage de GreenMail (IMAP 127.0.0.1:$Port)…"
$server = Start-Process -FilePath $java -ArgumentList $arguments -PassThru -WindowStyle Hidden

try {
    $deadline = (Get-Date).AddSeconds(40)
    while ($true) {
        try {
            $client = [System.Net.Sockets.TcpClient]::new("127.0.0.1", $Port)
            $client.Dispose()
            break
        } catch {
            if ((Get-Date) -gt $deadline -or $server.HasExited) { Write-Error "GreenMail n'a pas démarré." }
            Start-Sleep -Milliseconds 500
        }
    }

    $env:MAILMIGRATOR_GREENMAIL = "127.0.0.1:$Port"
    dotnet test (Join-Path $root "tests\MailMigrator.Tests") --filter "Category=Integration" --logger "console;verbosity=normal"
    $code = $LASTEXITCODE
} finally {
    Remove-Item Env:\MAILMIGRATOR_GREENMAIL -ErrorAction SilentlyContinue
    if (-not $server.HasExited) { Stop-Process -Id $server.Id -Force }
    Write-Host "GreenMail arrêté."
}
exit $code

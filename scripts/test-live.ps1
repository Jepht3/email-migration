<#
.SYNOPSIS
    Validation en conditions réelles sur deux boîtes de test JETABLES.

.DESCRIPTION
    Demande les adresses et mots de passe (saisie masquée), les place uniquement dans des
    variables d'environnement de ce processus, lance le test « Live », puis les efface.
    Rien n'est écrit sur le disque ni dans le dépôt.

    Le test : détecte les serveurs, teste les connexions, ajoute 5 messages marqueurs dans
    le dossier « LWB-Test/<date> » de la source, migre TOUTE la boîte source vers la
    destination, vérifie, puis relance pour contrôler qu'aucun message n'est recopié.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\test-live.ps1
#>
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

function Read-Secret([string]$prompt) {
    $secure = Read-Host $prompt -AsSecureString
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}

Write-Host "Boîtes de test JETABLES uniquement : la destination recevra une copie de toute la source." -ForegroundColor Yellow
$env:MAILMIGRATOR_LIVE_SOURCE = Read-Host "Adresse source"
$env:MAILMIGRATOR_LIVE_SOURCE_PASSWORD = Read-Secret "Mot de passe source"
$env:MAILMIGRATOR_LIVE_DESTINATION = Read-Host "Adresse destination"
$env:MAILMIGRATOR_LIVE_DESTINATION_PASSWORD = Read-Secret "Mot de passe destination"

try {
    dotnet test (Join-Path $root "tests\MailMigrator.Tests") --filter "Category=Live" --logger "console;verbosity=detailed"
    $code = $LASTEXITCODE
} finally {
    "MAILMIGRATOR_LIVE_SOURCE", "MAILMIGRATOR_LIVE_SOURCE_PASSWORD", "MAILMIGRATOR_LIVE_DESTINATION", "MAILMIGRATOR_LIVE_DESTINATION_PASSWORD" |
        ForEach-Object { Remove-Item "Env:\$_" -ErrorAction SilentlyContinue }
}
exit $code

<#
.SYNOPSIS
    Remplit une boîte de test JETABLE avec du courrier réaliste, pour tester une migration.

.DESCRIPTION
    Ajoute (par défaut) 200 messages : boîte de réception, envoyés, brouillons et dossiers
    « Clients/Dupont », « Clients/Martin SARL », « Factures/2024 » ; drapeaux variés, dates sur
    3 ans, pièces jointes dont une de 8 Mo. Les identifiants sont demandés à l'exécution
    et ne sont jamais enregistrés.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\seed-test-mailbox.ps1
    powershell -ExecutionPolicy Bypass -File scripts\seed-test-mailbox.ps1 -Count 1000
#>
param([int]$Count = 200)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

Write-Host "Boîte de test JETABLE uniquement : $Count messages vont y être ajoutés." -ForegroundColor Yellow
$env:MAILMIGRATOR_SEED_EMAIL = Read-Host "Adresse de la boîte à remplir"
$secure = Read-Host "Mot de passe" -AsSecureString
$ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try { $env:MAILMIGRATOR_SEED_PASSWORD = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
$env:MAILMIGRATOR_SEED_COUNT = "$Count"

try {
    dotnet test (Join-Path $root "tests\MailMigrator.Tests") --filter "Category=Seed" --logger "console;verbosity=detailed"
    $code = $LASTEXITCODE
} finally {
    "MAILMIGRATOR_SEED_EMAIL", "MAILMIGRATOR_SEED_PASSWORD", "MAILMIGRATOR_SEED_COUNT" |
        ForEach-Object { Remove-Item "Env:\$_" -ErrorAction SilentlyContinue }
}
exit $code

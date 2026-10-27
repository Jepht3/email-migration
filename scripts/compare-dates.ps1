<#
.SYNOPSIS
    Diagnostic : compare les dates (en-tête « Date » et date de réception IMAP) des derniers
    messages de la boîte de réception, entre la source et la destination d'une migration.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\compare-dates.ps1
#>
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

function Read-Secret([string]$prompt) {
    $secure = Read-Host $prompt -AsSecureString
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}

$env:MAILMIGRATOR_DATES_SOURCE = Read-Host "Adresse source"
$env:MAILMIGRATOR_DATES_SOURCE_PASSWORD = Read-Secret "Mot de passe source"
$env:MAILMIGRATOR_DATES_DESTINATION = Read-Host "Adresse destination"
$env:MAILMIGRATOR_DATES_DESTINATION_PASSWORD = Read-Secret "Mot de passe destination"
try {
    dotnet test (Join-Path $root "tests\MailMigrator.Tests") --filter "Category=Dates" --logger "console;verbosity=detailed"
} finally {
    "MAILMIGRATOR_DATES_SOURCE", "MAILMIGRATOR_DATES_SOURCE_PASSWORD", "MAILMIGRATOR_DATES_DESTINATION", "MAILMIGRATOR_DATES_DESTINATION_PASSWORD" |
        ForEach-Object { Remove-Item "Env:\$_" -ErrorAction SilentlyContinue }
}

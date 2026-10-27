# LEWEBMAX Mail Migrator

Application Windows (WPF, .NET 8) de migration d'emails IMAP : jusqu'à 5 boîtes en parallèle,
détection automatique des serveurs, progression détaillée et rapport final.

> Projet en cours de développement — voir « Avancement » ci-dessous.

## Prérequis

- Windows 10 1809 ou plus récent (Windows 11 recommandé pour l'effet Mica)
- [SDK .NET 8](https://dotnet.microsoft.com/download/dotnet/8.0) (`winget install Microsoft.DotNet.SDK.8`)

## Compiler et lancer

```bash
dotnet build MailMigrator.sln
dotnet run --project src/MailMigrator.App
```

## Tests

```bash
dotnet test
```

Les tests qui interrogent de vrais serveurs (DNS, ISPDB, IMAP) sont désactivés par défaut :

```bash
MAILMIGRATOR_NETWORK_TESTS=1 dotnet test --filter Category=Network
```

## Captures du design

```bash
dotnet run --project src/MailMigrator.App -- --screenshots docs/captures
```

Parcourt tous les écrans en thème clair puis sombre ; aperçu dans `docs/captures/apercu.html`.

## Structure

| Projet | Rôle |
|---|---|
| `src/MailMigrator.Core` | Logique pure, sans interface : détection des serveurs, test de connexion, moteur de migration, rapports. Réutilisable dans un service web. |
| `src/MailMigrator.App` | Application WPF (WPF-UI, MVVM CommunityToolkit). |
| `tests/MailMigrator.Tests` | Tests unitaires xUnit. |

La couleur d'accent LEWEBMAX se règle dans `src/MailMigrator.App/Themes/Accent.xaml`,
le logo dans `src/MailMigrator.App/Assets/Logo.xaml`.

## Avancement

- [x] Étape 2 — tous les écrans, avec données fictives (5 migrations simulées)
- [x] Étape 3a — détection des serveurs (autoconfig → ISPDB → autodiscover → MX + préfixes) et test de connexion avec erreurs en français
- [x] Étape 3b — moteur de copie d'une tâche : reprise après arrêt, déduplication, reconnexion, pause, SQLite, mots de passe DPAPI
- [ ] Étape 3c — 5 tâches en parallèle, limites de débit, docker-compose de test
- [ ] Étape 3d — branchement de l'interface sur le vrai moteur, notifications Windows
- [ ] Étape 3e — rapport, vérification finale, exports HTML / CSV
- [ ] Étape 3f — installeur Inno Setup

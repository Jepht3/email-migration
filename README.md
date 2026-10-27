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

Les tests qui touchent de vrais serveurs sont désactivés par défaut (ils passent sans rien faire) :

| Catégorie | Ce qui est testé | Lancement |
|---|---|---|
| `Integration` | Adaptateur MailKit et moteur complet contre un serveur IMAP réel (GreenMail) | `scripts\test-integration.ps1` (Java, sans Docker) ou `docker compose -f docker/docker-compose.yml up -d` puis `MAILMIGRATOR_GREENMAIL=127.0.0.1:3143 dotnet test --filter Category=Integration` |
| `Live` | Détection, test de connexion et migration sur deux boîtes de test **jetables** | `scripts\test-live.ps1` (demande les identifiants, rien n'est enregistré) |
| `Network` | Détection des serveurs sur de vrais domaines (Gmail, Orange…) | `MAILMIGRATOR_NETWORK_TESTS=1 dotnet test --filter Category=Network` |

`scripts\test-integration.ps1` nécessite Java (`winget install EclipseAdoptium.Temurin.21.JRE`) ;
il télécharge GreenMail au premier lancement (somme SHA-1 vérifiée), le démarre, lance les tests puis l'arrête.

## Mode démonstration

`dotnet run --project src/MailMigrator.App -- --demo` affiche 5 migrations simulées et un historique fictif, sans toucher à aucune boîte.
Les données réelles (état des migrations, historique) sont dans `%LOCALAPPDATA%\LEWEBMAX\MailMigrator\migrations.db`.

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
- [x] Vitesse — copie par lots (un FETCH et un MULTIAPPEND pour 50 messages), téléchargement du lot suivant pendant l'envoi
- [x] Sauvegarde sur ce PC — en option de la migration directe : boîte → PC (chiffré ou .eml), mise à jour, restauration PC → boîte
- [x] Étape 3c — limites de débit : détection des refus « [LIMIT] / [UNAVAILABLE] / too many », attente de 30 s à 5 min, lots réduits et pause entre lots, puis réaccélération progressive
- [x] Étape 3d — interface branchée sur le vrai moteur (aperçu réel des dossiers, file de 1 à 5 tâches, reprise au démarrage, réessayer les échecs), notifications Windows
- [x] Étape 3e — rapport (en-tête « en cours » / terminé), vérification finale, exports HTML (design de l'app, impression, mobile) et CSV (Excel)
- [ ] Étape 3f — installeur Inno Setup

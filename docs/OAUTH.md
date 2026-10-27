# Connexion « Se connecter avec Google / Microsoft » (OAuth2)

Gmail, Google Workspace, Outlook.com et Microsoft 365 refusent de plus en plus le mot de passe
du compte pour IMAP. Avec OAuth2, le client clique sur **Se connecter avec Google**, autorise
l'accès dans son navigateur, et l'application ne voit **jamais** son mot de passe : elle reçoit
un jeton, chiffré avec DPAPI comme un mot de passe, et effacé en fin de migration.

LEWEBMAX doit créer **une fois** une « application » chez Google et chez Microsoft, puis
coller les identifiants dans `oauth.local.json`.

---

## 1. Google (Gmail et Google Workspace)

1. Ouvrez https://console.cloud.google.com/ avec un compte LEWEBMAX et créez un projet
   « LEWEBMAX Mail Migrator ».
2. **API et services → Écran de consentement OAuth** (« Google Auth Platform ») :
   - Type d'utilisateur : **Externe** ;
   - Nom de l'application : `LEWEBMAX Mail Migrator`, e-mail d'assistance, logo (facultatif) ;
   - **Accès aux données → Ajouter des champs d'application** : ajoutez `https://mail.google.com/`
     (saisie manuelle), plus `openid` et `email` ;
   - **Audience → Utilisateurs test** : ajoutez les adresses à migrer (jusqu'à 100) tant que
     l'application est en mode « Test ».
3. **Clients → Créer un client** : type **Application de bureau**, nom `Mail Migrator`.
   Copiez l'**ID client** et le **Code secret du client**.
4. Pour un compte **Google Workspace** dont l'administrateur restreint les applications tierces :
   console d'administration → Sécurité → Accès et contrôle des données → Commandes des API →
   **Gérer l'accès des applications tierces** → ajoutez l'ID client et marquez-le « Approuvé ».

> **Mise en production** : `https://mail.google.com/` est une autorisation « restreinte ».
> Pour l'ouvrir à tous les clients (au-delà de 100 utilisateurs test), Google exige une
> validation de l'application et une évaluation de sécurité annuelle (CASA). En mode
> « Test », les autorisations expirent au bout de 7 jours : suffisant pour une migration.

## 2. Microsoft (Outlook.com, Hotmail, Microsoft 365)

1. Ouvrez https://entra.microsoft.com/ → **Applications → Inscriptions d'applications → Nouvelle inscription** :
   - Nom : `LEWEBMAX Mail Migrator` ;
   - Types de comptes : **Comptes dans un annuaire d'organisation (tout locataire) et comptes Microsoft personnels** ;
   - URI de redirection : plateforme **Client public/natif (mobile et bureau)**, valeur `http://localhost`.
2. **Authentification** : « Autoriser les flux de clients publics » → **Oui**.
3. **Autorisations d'API → Ajouter → API utilisées par mon organisation → Office 365 Exchange Online**
   → Autorisations déléguées → **IMAP.AccessAsUser.All**. Ajoutez aussi Microsoft Graph → `offline_access`, `openid`, `email`.
4. Copiez l'**ID d'application (client)** (page Vue d'ensemble). Pas de secret pour une application de bureau.

> Microsoft 365 : l'IMAP doit être activé pour la boîte (centre d'administration Exchange →
> Boîtes aux lettres → Applications de messagerie → IMAP). Selon la politique du client,
> son administrateur devra peut-être consentir à l'application la première fois.

## 3. Configuration de l'application

Créez `oauth.local.json` (jamais versionné) :

- pour le développement : à côté de `src/MailMigrator.App/oauth.json` (il est copié à la compilation) ;
- sur un poste installé : dans `%LOCALAPPDATA%\LEWEBMAX\MailMigrator\oauth.local.json`.

```json
{
  "google": {
    "clientId": "123456789-abcdef.apps.googleusercontent.com",
    "clientSecret": "GOCSPX-..."
  },
  "microsoft": {
    "clientId": "00000000-0000-0000-0000-000000000000"
  }
}
```

Relancez l'application : sous une adresse Gmail / Outlook / Microsoft 365, le bouton
**Se connecter avec Google** (ou Microsoft) apparaît. Le journal indique au démarrage
« OAuth2 configuré pour : Google, Microsoft ».

Pour une application de bureau, le « code secret » Google n'est pas vraiment secret (Google le
précise lui-même) : la sécurité repose sur PKCE et la redirection locale `127.0.0.1`.
Ne le publiez pas pour autant dans un dépôt public.

## Fonctionnement technique

- Flux « code d'autorisation + PKCE (S256) » ; le navigateur revient sur un mini-serveur local
  `http://127.0.0.1:<port libre>/` (Google) ou `http://localhost:<port>/` (Microsoft) ;
- le jeton de rafraîchissement est stocké chiffré (DPAPI) à la place du mot de passe ;
- le jeton d'accès (~1 h) est renouvelé automatiquement, y compris lors des reconnexions ;
- authentification IMAP par `XOAUTH2` (MailKit `SaslMechanismOAuth2`) ;
- code : `src/MailMigrator.Core/Connectivity/OAuth/`.

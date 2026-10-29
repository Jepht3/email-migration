# Configuration OAuth — état vérifié le 29 octobre 2026

## Google

Projet dédié : `lewebmax-mail-migrator` (LEWEBMAX Mail Migrator).
Le projet existant `n8ntik` n’a pas été modifié.

- Application : LEWEBMAX Mail Migrator.
- Audience : externe ; publication : Testing.
- Client : LEWEBMAX Mail Migrator Windows, type application de bureau.
- Identifiant public : `964453715684-2dtedi03u7rfjclfb5ocmi1dp30qfq4i.apps.googleusercontent.com`.
- Un utilisateur de test ajouté et vérifié : compte du propriétaire connecté.
- Autorisation déclarée et vérifiée après rechargement : `https://mail.google.com/`,
  dans la table des autorisations restreintes.

Les identifiants publics ci-dessus ne sont pas des secrets. Aucun secret client,
jeton, cookie ou mot de passe ne doit être enregistré dans ce document.

Le parcours logiciel doit encore être intégré et testé : navigateur système,
code d’autorisation avec PKCE, redirection locale loopback, validation de state,
renouvellement et protection des jetons dans la session Windows. Ne pas utiliser
le parcours OOB déprécié. Les autorisations Gmail API `gmail.readonly` et
`gmail.modify` ne remplacent pas l’autorisation IMAP ci-dessus.

Aucun consentement d’accès réel à une boîte n’a été accordé et aucune migration
n’a été lancée pendant la configuration. La déclaration du scope ne constitue
pas une autorisation effective d’accès aux emails.

Références :
- https://developers.google.com/workspace/gmail/imap/xoauth2-protocol
- https://developers.google.com/identity/protocols/oauth2/native-app

## Microsoft

Annuaire vérifié : Default Directory,
`3045de44-6a7d-43c9-813a-9ac564abab58`.
Application créée : `LEWEBMAX Mail Migrator Windows`.
Identifiant public : `3f13b650-2c82-4164-8822-8045317d7212`.
Type : client public mobile/bureau ; redirection `http://localhost`.
Audience : `AzureADandPersonalMicrosoftAccount`.

Configuration Microsoft incomplète : la ressource Office 365 Exchange Online
est absente du sélecteur « APIs my organization uses » (recherches par nom,
par « Exchange » et par identifiant de ressource). L’autorisation IMAP n’a donc
pas été ajoutée. Le seul droit déclaré est Graph User.Read, non accordé au
niveau de l’annuaire. Ne pas présenter l’intégration Microsoft comme fonctionnelle.

Incident corrigé : TinyFish a accordé par erreur un consentement administrateur
User.Read malgré l’instruction contraire. Ce consentement a été révoqué, avec
notification de confirmation et disparition de la ligne dans les consentements
de l’application d’entreprise. Une inspection suivante confirme l’absence de
consentement global pour User.Read. L’application et sa redirection sont conservées.

Cible : client public de bureau, code d’autorisation avec PKCE,
redirection `http://localhost`, comptes professionnels et personnels,
autorisation déléguée Exchange Online `IMAP.AccessAsUser.All` et renouvellement
via `offline_access`. Aucun secret client ni autorisation applicative globale.

Référence :
https://learn.microsoft.com/en-us/exchange/client-developer/legacy-protocols/how-to-authenticate-an-imap-pop-smtp-application-by-using-oauth

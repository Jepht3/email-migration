# Configuration OAuth — état vérifié le 29 octobre 2026

## Google

Projet dédié : `lewebmax-mail-migrator` (LEWEBMAX Mail Migrator).
Le projet existant `n8ntik` n’a pas été modifié.

- Application : LEWEBMAX Mail Migrator.
- Audience : externe ; publication : En production (non validée par Google).
- Client : LEWEBMAX Mail Migrator Windows, type application de bureau.
- Identifiant public : `964453715684-2dtedi03u7rfjclfb5ocmi1dp30qfq4i.apps.googleusercontent.com`.
- Un utilisateur de test ajouté et vérifié : compte du propriétaire connecté.
- Autorisation déclarée et vérifiée après rechargement : `https://mail.google.com/`,
  dans la table des autorisations restreintes.

Les identifiants publics ci-dessus ne sont pas des secrets. Aucun secret client,
jeton, cookie ou mot de passe ne doit être enregistré dans ce document.

Le parcours Windows est intégré : navigateur système,
code d’autorisation avec PKCE, redirection locale loopback, validation de state,
renouvellement et protection des jetons dans la session Windows. L’autorisation réelle
d’une boîte Gmail puis une migration OAuth de bout en bout restent à vérifier.
Le lancement normal utilise le moteur réel ; le mode démonstration reste un argument
de développement explicite. Ne pas utiliser
le parcours OOB déprécié. Les autorisations Gmail API `gmail.readonly` et
`gmail.modify` ne remplacent pas l’autorisation IMAP ci-dessus.

Aucun consentement d’accès réel à une boîte n’a été accordé et aucune migration OAuth
n’a été lancée pendant la configuration. La déclaration du scope ne constitue
pas une autorisation effective d’accès aux emails. Le passage « En production »
ne remplace pas la validation Google pour une diffusion publique sans avertissement
ni limites d’application non validée. Cette validation n’a pas été obtenue.

Pages publiques déployées :
- Présentation : https://jepht3.github.io/email-migration/
- Confidentialité : https://jepht3.github.io/email-migration/privacy.html

La configuration du client de bureau est incluse dans la distribution locale et
transmise aux constructions GitHub via le secret GOOGLE_DESKTOP_CLIENT_JSON.
Le fichier source google-desktop-client.json est exclu du dépôt.

Références :
- https://developers.google.com/workspace/gmail/imap/xoauth2-protocol
- https://developers.google.com/identity/protocols/oauth2/native-app

## Microsoft

Annuaire vérifié : Default Directory,
`3045de44-6a7d-43c9-813a-9ac564abab58`.
Application créée : `LEWEBMAX Mail Migrator Windows`.
Identifiant public : `3f13b650-2c82-4164-8822-8045317d7212`.
Type et redirection demandés : client public mobile/bureau, `http://localhost`.
Audience demandée : `AzureADandPersonalMicrosoftAccount`.

Configuration Microsoft non vérifiée : la ressource Exchange Online était absente
du sélecteur initial. La référence Microsoft Graph expose aussi la permission
déléguée IMAP.AccessAsUser.All, identifiant
`652390e4-393a-48de-9484-05f9b1212954`. Les essais d’enregistrement par le
sélecteur et par l’éditeur de manifeste ont produit des rapports contradictoires ;
le dernier essai rapporte que l’enregistrement était désactivé. Ni l’audience,
ni la redirection, ni les autorisations IMAP persistées ne sont donc considérées
comme confirmées. Ne pas présenter OAuth Microsoft comme fonctionnel avant une
vérification fiable et un essai réel. L’intégration du parcours dans Windows ne
suffit pas à prouver la configuration du fournisseur.

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

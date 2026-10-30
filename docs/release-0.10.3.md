# LEWEBMAX Mail Migrator 0.10.3

- Les adresses Gmail et Googlemail affichent automatiquement le bouton « Se connecter avec Google ».
- Les adresses Outlook, Hotmail et Live affichent automatiquement « Se connecter avec Microsoft ».
- Pour les domaines professionnels, la détection du serveur IMAP Google ou Microsoft propose également la connexion correspondante.
- Le changement d’adresse réinitialise la méthode de connexion et l’autorisation précédente, pour éviter de conserver le mauvais fournisseur.
- La connexion par mot de passe reste sélectionnable explicitement.
- Ajout de tests automatiques de reconnaissance des fournisseurs et de changement de compte.

Inclut le correctif 0.10.2 : boutons Réduire, Agrandir/Restaurer et Fermer lisibles en thème clair et sombre.

L’autorisation du compte s’effectue dans le navigateur auprès de Google ou Microsoft après un clic sur le bouton. Aucun mot de passe Google ou Microsoft n’est demandé par ce parcours.

# Évolution du produit

## Premier lot : accueil Pilotage

Implémenté dans le code : navigation horizontale, cartes d’accès à la migration,
aux sauvegardes et aux rapports, suivi des tâches actives avec pause/reprise,
accès au détail et liste des cinq derniers rapports en erreur à examiner.
L’accueil se rafraîchit toutes les deux secondes uniquement lorsqu’il est affiché.
Les données viennent du moteur et de l’historique existants.

La validation visuelle de l’application reste à effectuer. La maquette de proposition
ne constitue pas une capture de l’application modifiée.

## Lots restant à développer

- Diagnostic préalable et estimation : droits IMAP, dossiers, volumes, avertissements
  et quotas lorsque le serveur expose cette capacité. Une estimation de durée doit
  rester indicative, avec sa méthode de calcul visible.
- Centre des erreurs : regrouper les échecs et leurs causes avec accès aux rapports,
  aux solutions et aux nouvelles tentatives déjà prises en charge par le moteur.
- OAuth2 Google et Microsoft : configuration des applications publiques, consentement,
  renouvellement et protection des jetons, puis intégration au test de connexion,
  à la migration et à la reprise. Ne pas considérer le simple ajout d’un fournisseur
  SASL comme une intégration complète.
- Projets clients et profils de serveurs : persistance, sélection dans l’assistant,
  filtre de l’historique et compatibilité des données existantes.
- Planification et synchronisation finale : dates persistées, fuseau horaire,
  absence de double exécution et comportement explicite lorsque le PC est arrêté.
- Assistant DNS : vérification en lecture seule et checklist de bascule ; aucune
  modification des zones sans autorisation spécifique.
- Rapports personnalisés : identité du client, logo, bilan vérifié et exports.
- Sauvegardes : contrôle d’intégrité, espace disque et conservation configurable.
  Toute suppression automatique exige une politique explicitement activée.
- Réglage du débit et des ressources : compléter la concurrence et le ralentissement
  automatique existants avec une limite utilisateur réellement appliquée au moteur.
- Personnalisation du dashboard et modes débutant/expert : préférences persistées,
  accès clavier et fonctionnement cohérent en clair et sombre.

## Accès externes nécessaires à la validation finale

OAuth nécessite les identifiants d’applications enregistrées chez Google et Microsoft,
les autorisations appropriées et des comptes de test. Les essais de migration réels
nécessitent des boîtes jetables autorisées. Une modification DNS ou une publication
doit faire l’objet d’une autorisation distincte. Aucun secret ne doit être ajouté au dépôt.

Les contacts et calendriers constituent un périmètre séparé de la migration IMAP.

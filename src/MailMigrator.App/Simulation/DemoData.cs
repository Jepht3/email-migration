using MailMigrator.Core.Models;
using MailMigrator.Core.Reporting;

namespace MailMigrator.App.Simulation;

/// <summary>Données fictives réalistes pour valider le design (étape 2).</summary>
public static class DemoData
{
    public sealed record DemoFolder(string Source, string Destination, SpecialFolder Role, int Count, int AvgKb);

    public static readonly DemoFolder[] Folders =
    [
        new("INBOX", "INBOX", SpecialFolder.Inbox, 1840, 85),
        new("Sent", "Envoyés", SpecialFolder.Sent, 620, 140),
        new("Drafts", "Brouillons", SpecialFolder.Drafts, 14, 40),
        new("Archives/2023", "Archives/2023", SpecialFolder.Archive, 940, 110),
        new("Clients/Dupont", "Clients/Dupont", SpecialFolder.None, 85, 420),
        new("Factures", "Factures", SpecialFolder.None, 210, 260),
        new("Trash", "Corbeille", SpecialFolder.Trash, 140, 60),
    ];

    public static readonly (string Source, string Destination)[] Pairs =
    [
        ("contact@boulangerie-martin.fr", "contact@boulangerie-martin.com"),
        ("j.dupont@cabinet-dupont.fr", "jean.dupont@cabinet-dupont.fr"),
        ("compta@garage-leroy.fr", "comptabilite@garage-leroy.fr"),
        ("sophie.bernard@gmail.com", "sophie@atelier-bernard.fr"),
        ("info@hotel-les-pins.fr", "reception@hotel-les-pins.fr"),
        ("direction@sarl-moreau.fr", "direction@moreau-groupe.fr"),
    ];

    public static readonly string[] Senders =
    [
        "Marie Lefèvre", "Thomas Girard", "Service client Orange", "URSSAF", "Paul Fontaine",
        "Claire Dubois", "Amazon.fr", "Banque Populaire", "Nicolas Petit", "Julie Morel",
        "LinkedIn", "Impots.gouv", "Antoine Roux", "Camille Laurent", "EDF Entreprises",
    ];

    public static readonly string[] Subjects =
    [
        "Devis client Dupont", "Facture n° 2024-0387", "Re: Planning de la semaine prochaine",
        "Confirmation de commande", "Relevé de compte – mars", "Proposition commerciale révisée",
        "Rendez-vous jeudi 14 h", "Votre attestation de vigilance", "Photos du chantier",
        "Contrat de maintenance 2024", "Re: Re: Livraison retardée", "Bon de commande signé",
        "Invitation : assemblée générale", "Rappel : échéance TVA", "Compte rendu de réunion",
        "Fwd: Tarifs fournisseurs 2024", "Demande de remboursement", "Votre colis est en route",
    ];

    public static readonly string[] Errors =
    [
        "Message trop volumineux pour le serveur de destination (52 Mo, limite 50 Mo)",
        "En-tête mal formé : date de réception illisible",
        "Le serveur a fermé la connexion pendant l'envoi (nouvelle tentative épuisée)",
    ];

    public static IReadOnlyList<MigrationHistoryEntry> History { get; } = BuildHistory();

    private static List<MigrationHistoryEntry> BuildHistory()
    {
        var random = new Random(42);
        var domains = new[]
        {
            ("cabinet-renard.fr", "cabinet-renard.fr"), ("pharmacie-centrale.fr", "pharmacie-centrale.com"),
            ("ecole-musique-arles.fr", "emarles.fr"), ("transports-garnier.fr", "garnier-logistique.fr"),
            ("immo-sud.fr", "immo-sud.fr"), ("fleurs-et-jardins.fr", "fleursetjardins.fr"),
            ("asso-les-tilleuls.org", "lestilleuls.org"), ("menuiserie-bonnet.fr", "bonnet-bois.fr"),
            ("dentiste-roche.fr", "cabinet-roche.fr"), ("restaurant-le-quai.fr", "lequai.fr"),
            ("avocat-mercier.fr", "mercier-avocats.fr"), ("atelier-velo.fr", "atelier-velo.fr"),
        };
        var names = new[] { "contact", "accueil", "direction", "secretariat", "admin", "compta", "info", "bonjour" };
        var list = new List<MigrationHistoryEntry>();
        var now = DateTimeOffset.Now;
        for (var i = 0; i < 18; i++)
        {
            var (src, dst) = domains[i % domains.Length];
            var name = names[random.Next(names.Length)];
            var total = random.Next(350, 9200);
            var status = i switch
            {
                3 or 11 => JobStatus.CompletedWithErrors,
                7 => JobStatus.Failed,
                14 => JobStatus.Cancelled,
                _ => JobStatus.Completed,
            };
            var failed = status switch
            {
                JobStatus.CompletedWithErrors => random.Next(2, 14),
                JobStatus.Failed => total,
                _ => 0,
            };
            var skipped = status == JobStatus.Failed ? 0 : random.Next(0, total / 12);
            var copied = status switch
            {
                JobStatus.Failed => 0,
                JobStatus.Cancelled => total / 3,
                _ => total - skipped - failed,
            };
            list.Add(new MigrationHistoryEntry
            {
                Id = new Guid(i + 1, 0, 0, new byte[8]),
                ColorIndex = i % 5,
                SourceEmail = $"{name}@{src}",
                DestinationEmail = $"{name}@{dst}",
                StartedAt = now.AddHours(-(i * 19 + random.Next(1, 12))).AddMinutes(-random.Next(60)),
                Duration = TimeSpan.FromSeconds(status == JobStatus.Failed ? 14 : total * random.Next(25, 60) / 100.0),
                Status = status,
                SourceMessages = total,
                Copied = copied,
                Skipped = skipped,
                Failed = status == JobStatus.Failed ? 0 : failed,
                Bytes = (long)copied * random.Next(60, 180) * 1024,
            });
        }
        return list;
    }
}

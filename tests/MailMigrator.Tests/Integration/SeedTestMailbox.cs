using MailKit;
using MailMigrator.Core.Discovery;
using MimeKit;
using Xunit.Abstractions;

namespace MailMigrator.Tests.Integration;

/// <summary>
/// Remplit une boîte de test JETABLE avec du courrier réaliste (dossiers imbriqués, drapeaux,
/// dates sur 3 ans, pièces jointes dont une de 8 Mo). Lancé par scripts/seed-test-mailbox.ps1.
/// </summary>
[Trait("Category", "Seed")]
public sealed class SeedTestMailbox(ITestOutputHelper output)
{
    private static readonly string[] Senders =
    [
        "Marie Lefèvre <marie.lefevre@exemple.fr>", "Thomas Girard <t.girard@exemple.fr>", "URSSAF <ne-pas-repondre@exemple.fr>",
        "Paul Fontaine <paul@fontaine-btp.exemple.fr>", "Claire Dubois <claire.dubois@exemple.fr>", "Banque Populaire <info@banque.exemple.fr>",
        "Nicolas Petit <n.petit@exemple.fr>", "Julie Morel <julie@morel-conseil.exemple.fr>", "EDF Entreprises <service@edf.exemple.fr>",
    ];

    private static readonly string[] Subjects =
    [
        "Devis client Dupont", "Facture n° {0}", "Re: Planning de la semaine prochaine", "Confirmation de commande {0}",
        "Relevé de compte", "Proposition commerciale révisée", "Rendez-vous jeudi 14 h", "Votre attestation de vigilance",
        "Photos du chantier", "Contrat de maintenance", "Re: Re: Livraison retardée", "Bon de commande signé",
        "Invitation : assemblée générale", "Rappel : échéance TVA", "Compte rendu de réunion", "Fwd: Tarifs fournisseurs",
    ];

    [Fact]
    public async Task Fill_test_mailbox()
    {
        var email = Environment.GetEnvironmentVariable("MAILMIGRATOR_SEED_EMAIL");
        var password = Environment.GetEnvironmentVariable("MAILMIGRATOR_SEED_PASSWORD");
        if (email is null || password is null)
        {
            output.WriteLine("Variables MAILMIGRATOR_SEED_* absentes : rien à faire (voir scripts/seed-test-mailbox.ps1).");
            return;
        }
        var count = int.TryParse(Environment.GetEnvironmentVariable("MAILMIGRATOR_SEED_COUNT"), out var n) ? Math.Clamp(n, 10, 5000) : 200;

        using var http = new HttpClient();
        var discovery = await ServerDiscoveryService.CreateDefault(http).DiscoverAsync(email);
        Assert.True(discovery.Found, $"Serveur IMAP non détecté pour {email}");
        var endpoint = discovery.Server!.Endpoint;
        output.WriteLine($"Serveur : {endpoint.Describe()}");

        using var client = await ImapTestKit.ConnectAsync(endpoint, password);
        var root = client.GetFolder(client.PersonalNamespaces[0]);
        var sent = TrySpecial(client, SpecialFolder.Sent) ?? await Ensure(root, "Sent");
        var drafts = TrySpecial(client, SpecialFolder.Drafts) ?? await Ensure(root, "Drafts");
        var targets = new (IMailFolder Folder, double Share, bool Outgoing)[]
        {
            (client.Inbox, 0.45, false),
            (sent, 0.2, true),
            (drafts, 0.03, true),
            (await Ensure(root, "Clients/Dupont"), 0.12, false),
            (await Ensure(root, "Clients/Martin SARL"), 0.08, false),
            (await Ensure(root, "Factures/2024"), 0.12, false),
        };

        var random = new Random(count);
        var total = 0;
        foreach (var (folder, share, outgoing) in targets)
        {
            var howMany = Math.Max(1, (int)(count * share));
            for (var i = 0; i < howMany; i++)
            {
                var date = DateTimeOffset.Now.AddDays(-random.Next(0, 1100)).AddMinutes(-random.Next(1440));
                var attachment = random.NextDouble() < 0.15 ? random.Next(20_000, 1_500_000) : 0;
                if (total == 3) attachment = 8 * 1024 * 1024; // un gros message pour le téléchargement par morceaux
                var message = Build(random, date, outgoing, attachment, total);
                var flags = folder == drafts ? MessageFlags.Draft
                    : outgoing ? MessageFlags.Seen
                    : (random.NextDouble() < 0.75 ? MessageFlags.Seen : MessageFlags.None)
                      | (random.NextDouble() < 0.1 ? MessageFlags.Flagged : MessageFlags.None)
                      | (random.NextDouble() < 0.2 ? MessageFlags.Answered : MessageFlags.None);
                await folder.AppendAsync(new AppendRequest(message, flags, date));
                total++;
            }
            output.WriteLine($"{folder.FullName} : {howMany} messages");
        }
        await client.DisconnectAsync(true);
        output.WriteLine($"Terminé : {total} messages ajoutés dans {email}.");
    }

    private static MimeMessage Build(Random random, DateTimeOffset date, bool outgoing, int attachmentBytes, int index)
    {
        var message = new MimeMessage();
        var other = MailboxAddress.Parse(Senders[random.Next(Senders.Length)]);
        var me = new MailboxAddress("Boîte de test", "test@lewebmax.exemple");
        message.From.Add(outgoing ? me : other);
        message.To.Add(outgoing ? other : me);
        message.Subject = string.Format(Subjects[random.Next(Subjects.Length)], $"2024-{random.Next(1, 999):000}");
        message.Date = date;
        message.MessageId = $"lwb-seed-{Guid.NewGuid():N}@test.lewebmax";
        var body = new BodyBuilder
        {
            TextBody = $"Bonjour,\n\nMessage de test n° {index + 1} généré pour valider la migration.\n\nCordialement,\n{other.Name}",
            HtmlBody = $"<p>Bonjour,</p><p>Message de test <b>n° {index + 1}</b> généré pour valider la migration.</p><p>Cordialement,<br>{other.Name}</p>",
        };
        if (attachmentBytes > 0) body.Attachments.Add($"document-{index + 1}.pdf", ImapTestKit.Payload(attachmentBytes, index));
        message.Body = body.ToMessageBody();
        return message;
    }

    private static IMailFolder? TrySpecial(MailKit.Net.Imap.ImapClient client, SpecialFolder role)
    {
        try { return client.GetFolder(role); } catch (NotSupportedException) { return null; }
    }

    private static async Task<IMailFolder> Ensure(IMailFolder root, string path)
    {
        var parent = root;
        foreach (var name in path.Split('/'))
        {
            IMailFolder? next = null;
            try { next = await parent.GetSubfolderAsync(name); } catch (FolderNotFoundException) { }
            parent = next ?? await parent.CreateAsync(name, true) ?? throw new InvalidOperationException(name);
            await parent.SubscribeAsync();
        }
        return parent;
    }
}

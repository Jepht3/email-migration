using MailKit;
using MailKit.Search;
using MailMigrator.Core.Discovery;
using Xunit.Abstractions;

namespace MailMigrator.Tests.Integration;

/// <summary>
/// Diagnostic : pour quelques messages présents des deux côtés (même Message-ID), affiche
/// l'en-tête « Date » et la date de réception IMAP (INTERNALDATE) de la source et de la destination.
/// Lancé par scripts/compare-dates.ps1 (identifiants demandés à l'exécution, jamais enregistrés).
/// </summary>
[Trait("Category", "Dates")]
public sealed class CompareDatesDiagnostic(ITestOutputHelper output)
{
    [Fact]
    public async Task Compare_source_and_destination_dates()
    {
        string? Env(string n) => Environment.GetEnvironmentVariable(n);
        var (srcEmail, srcPass, dstEmail, dstPass) = (Env("MAILMIGRATOR_DATES_SOURCE"), Env("MAILMIGRATOR_DATES_SOURCE_PASSWORD"),
            Env("MAILMIGRATOR_DATES_DESTINATION"), Env("MAILMIGRATOR_DATES_DESTINATION_PASSWORD"));
        if (srcEmail is null || srcPass is null || dstEmail is null || dstPass is null)
        {
            output.WriteLine("Identifiants absents : diagnostic ignoré (voir scripts/compare-dates.ps1).");
            return;
        }

        using var http = new HttpClient();
        var discovery = ServerDiscoveryService.CreateDefault(http);
        var srcEndpoint = (await discovery.DiscoverAsync(srcEmail)).Server!.Endpoint;
        var dstEndpoint = (await discovery.DiscoverAsync(dstEmail)).Server!.Endpoint;
        using var source = await ImapTestKit.ConnectAsync(srcEndpoint, srcPass);
        using var destination = await ImapTestKit.ConnectAsync(dstEndpoint, dstPass);
        output.WriteLine($"Source : {srcEndpoint.Describe()}  |  Destination : {dstEndpoint.Describe()}");
        output.WriteLine($"Capacités destination : {destination.Capabilities}");

        await source.Inbox.OpenAsync(FolderAccess.ReadOnly);
        await destination.Inbox.OpenAsync(FolderAccess.ReadOnly);
        const MessageSummaryItems items = MessageSummaryItems.Envelope | MessageSummaryItems.InternalDate | MessageSummaryItems.UniqueId;
        var sample = (await source.Inbox.FetchAsync(Math.Max(0, source.Inbox.Count - 5), -1, items)).ToList();

        var differences = 0;
        foreach (var s in sample)
        {
            if (s.Envelope?.MessageId is not { } mid) continue;
            var found = await destination.Inbox.SearchAsync(SearchQuery.HeaderContains("Message-ID", mid));
            if (found.Count == 0)
            {
                output.WriteLine($"✘ {s.Envelope.Subject} : absent de la destination");
                continue;
            }
            var d = (await destination.Inbox.FetchAsync([found[^1]], items)).Single();
            var same = s.InternalDate?.ToUniversalTime() == d.InternalDate?.ToUniversalTime() && s.Envelope.Date == d.Envelope?.Date;
            if (!same) differences++;
            output.WriteLine($"{(same ? "✔" : "✘")} {s.Envelope.Subject}");
            output.WriteLine($"    en-tête Date   : source {s.Envelope.Date:dd/MM/yyyy HH:mm zzz}  →  destination {d.Envelope?.Date:dd/MM/yyyy HH:mm zzz}");
            output.WriteLine($"    réception IMAP : source {s.InternalDate:dd/MM/yyyy HH:mm zzz}  →  destination {d.InternalDate:dd/MM/yyyy HH:mm zzz}");
        }
        output.WriteLine(differences == 0 ? "Résultat : dates identiques des deux côtés." : $"Résultat : {differences} message(s) avec une date différente.");
    }
}

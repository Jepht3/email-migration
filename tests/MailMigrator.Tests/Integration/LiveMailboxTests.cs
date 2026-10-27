using MailKit;
using MailMigrator.Core.Connectivity;
using MailMigrator.Core.Discovery;
using MailMigrator.Core.Engine;
using MailMigrator.Core.Imap;
using MailMigrator.Core.Models;
using MailMigrator.Core.Persistence;
using MailMigrator.Tests.Engine;
using Xunit.Abstractions;

namespace MailMigrator.Tests.Integration;

/// <summary>
/// Validation en conditions réelles sur deux boîtes de test JETABLES (hébergement LEWEBMAX).
/// Lancée par scripts/test-live.ps1, qui demande les identifiants sans les enregistrer.
/// ATTENTION : la boîte destination reçoit une copie de toute la boîte source.
/// </summary>
[Trait("Category", "Live")]
[Collection("GreenMail")]
public sealed class LiveMailboxTests(ITestOutputHelper output)
{
    private static string? Env(string name) => Environment.GetEnvironmentVariable(name);

    private static readonly string? SourceEmail = Env("MAILMIGRATOR_LIVE_SOURCE");
    private static readonly string? SourcePassword = Env("MAILMIGRATOR_LIVE_SOURCE_PASSWORD");
    private static readonly string? DestinationEmail = Env("MAILMIGRATOR_LIVE_DESTINATION");
    private static readonly string? DestinationPassword = Env("MAILMIGRATOR_LIVE_DESTINATION_PASSWORD");

    private bool Enabled
    {
        get
        {
            var ok = SourceEmail is not null && SourcePassword is not null && DestinationEmail is not null && DestinationPassword is not null;
            if (!ok) output.WriteLine("Identifiants des boîtes de test absents : test ignoré (voir scripts/test-live.ps1).");
            return ok;
        }
    }

    private async Task<ImapEndpoint> DiscoverAsync(string email)
    {
        using var http = new HttpClient();
        var outcome = await ServerDiscoveryService.CreateDefault(http).DiscoverAsync(email);
        foreach (var a in outcome.Attempts) output.WriteLine($"  {(a.Success ? "✔" : "✘")} {a.Step} : {a.Detail}");
        Assert.True(outcome.Found, $"Serveur IMAP non détecté pour {email}");
        output.WriteLine($"→ {email} : {outcome.Server!.Endpoint.Describe()} (via {outcome.Server.Source})");
        return outcome.Server.Endpoint;
    }

    [Fact]
    public async Task Detects_tests_and_migrates_real_mailboxes()
    {
        if (!Enabled) return;

        // 1. Détection automatique et test de connexion, comme dans l'assistant.
        var source = await DiscoverAsync(SourceEmail!);
        var destination = await DiscoverAsync(DestinationEmail!);
        var tester = new ConnectionTester();
        var sourceTest = await tester.TestAsync(source, new PasswordAuthProvider(SourcePassword!));
        var destinationTest = await tester.TestAsync(destination, new PasswordAuthProvider(DestinationPassword!));
        output.WriteLine($"Test source : {sourceTest.Info.Title} {sourceTest.TechnicalDetail}");
        output.WriteLine($"Test destination : {destinationTest.Info.Title} {destinationTest.TechnicalDetail}");
        Assert.True(sourceTest.Success && destinationTest.Success);

        // 2. Quelques messages marqueurs, propres à cette exécution, dans un sous-dossier de la source.
        var run = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        var marker = $"LWB-Test/{run}";
        await ImapTestKit.SeedAsync(source, SourcePassword!, Enumerable.Range(1, 5).Select(i =>
            (marker, ImapTestKit.Message($"<lwb-{run}-{i}@test.lewebmax>", $"Test LEWEBMAX {run} n°{i}", DateTimeOffset.Now.AddDays(-i)), i % 2 == 0 ? MessageFlags.Seen : MessageFlags.Flagged)));

        // 3. Migration complète, puis relance qui ne doit rien recopier.
        var db = Path.Combine(Path.GetTempPath(), $"mailmigrator-live-{run}.db");
        try
        {
            using var store = new SqliteMigrationStore(db);
            var service = new MigrationService(store, new MailKitConnectionFactory(), new FakeProtector());
            var id = service.CreateJob(new MigrationJobDefinition
            {
                SourceEmail = SourceEmail!,
                Source = source,
                DestinationEmail = DestinationEmail!,
                Destination = destination,
                Options = new MigrationOptions { ErasePasswordsWhenDone = false },
            }, SourcePassword!, DestinationPassword!);

            var first = await service.Open(id).RunAsync();
            var report = store.GetReport(id)!;
            output.WriteLine($"1re passe : {first} — {report.Summary.Copied} copiés, {report.Summary.Skipped} déjà présents, {report.Summary.Failed} échecs, {report.Summary.Duration}");
            foreach (var f in report.Folders)
                output.WriteLine($"  {f.SourcePath} → {f.DestinationPath} : {f.SourceCount} source, {f.Copied} copiés, {f.Skipped} ignorés, {f.Failed} échecs, {f.DestinationCountAfter} en destination");
            foreach (var m in report.Messages.Where(m => m.Status == MessageStatus.Failed))
                output.WriteLine($"  ✘ [{m.Folder}] {m.Subject} : {m.Error}");
            Assert.Equal(JobStatus.Completed, first);

            var dest = await ImapTestKit.InventoryAsync(destination, DestinationPassword!);
            var copiedFolder = dest.Single(d => d.Key.EndsWith(run, StringComparison.Ordinal));
            Assert.Equal(5, copiedFolder.Value.Count);

            var second = await service.Open(id).RunAsync();
            var after = store.GetReport(id)!;
            output.WriteLine($"2e passe : {second} — {after.Summary.Copied} copiés au total (inchangé attendu)");
            Assert.Equal(report.Summary.Copied, after.Summary.Copied);
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                try { File.Delete(db + suffix); } catch (IOException) { }
        }
    }
}

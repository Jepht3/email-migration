using MailKit;
using MailMigrator.Core.Abstractions;
using MailMigrator.Core.Connectivity;
using MailMigrator.Core.Engine;
using MailMigrator.Core.Imap;
using MailMigrator.Core.Models;
using MailMigrator.Core.Persistence;
using MailMigrator.Tests.Engine;
using MimeKit;
using Xunit.Abstractions;

namespace MailMigrator.Tests.Integration;

/// <summary>
/// Tests contre un vrai serveur IMAP (GreenMail), lancé par scripts/test-integration.ps1
/// ou docker/docker-compose.yml. Inactifs si MAILMIGRATOR_GREENMAIL n'est pas défini.
/// </summary>
[Trait("Category", "Integration")]
[Collection("GreenMail")]
public sealed class GreenMailTests(ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly string? Server = Environment.GetEnvironmentVariable("MAILMIGRATOR_GREENMAIL");
    private static readonly DateTimeOffset Day = new(2024, 3, 12, 9, 30, 0, TimeSpan.FromHours(1));

    private static ImapEndpoint Endpoint(string user)
    {
        var parts = (Server ?? "127.0.0.1:3143").Split(':');
        return new ImapEndpoint(parts[0], int.Parse(parts[1]), SecurityMode.None, user);
    }

    private static readonly ImapEndpoint Source = Endpoint("source@test.local");
    private static readonly ImapEndpoint Destination = Endpoint("destination@test.local");

    private readonly string _db = Path.Combine(Path.GetTempPath(), $"mailmigrator-it-{Guid.NewGuid():N}.db");

    private bool Enabled
    {
        get
        {
            if (Server is null) output.WriteLine("MAILMIGRATOR_GREENMAIL non défini : test ignoré (voir scripts/test-integration.ps1).");
            return Server is not null;
        }
    }

    public async Task InitializeAsync()
    {
        if (Server is null) return;
        await ImapTestKit.ResetAsync(Source, "source");
        await ImapTestKit.ResetAsync(Destination, "destination");
    }

    public Task DisposeAsync()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_db + suffix); } catch (IOException) { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Connection_tester_accepts_valid_and_rejects_wrong_password()
    {
        if (!Enabled) return;
        var tester = new ConnectionTester();

        var ok = await tester.TestAsync(Source, new PasswordAuthProvider("source"));
        var refused = await tester.TestAsync(Source, new PasswordAuthProvider("mauvais"));

        Assert.True(ok.Success, ok.TechnicalDetail);
        Assert.Equal(ConnectionError.AuthenticationFailed, refused.Error);
    }

    [Fact]
    public async Task MailKit_adapter_creates_nested_folders_and_preserves_flags_and_dates()
    {
        if (!Enabled) return;
        await using var dest = new MailKitConnectionFactory().Create(Destination, new PasswordAuthProvider("destination"));
        await dest.ConnectAsync(default);

        await dest.CreateFolderAsync("Clients/Dupont", default);
        var raw = new MemoryStream();
        await ImapTestKit.Message("<adaptateur@test>", "Devis client Dupont", Day).WriteToAsync(raw);
        raw.Position = 0;
        await dest.AppendAsync("Clients/Dupont", raw, MailFlags.Seen | MailFlags.Flagged | MailFlags.Answered, Day, default);

        var folders = await dest.ListFoldersAsync(default);
        var message = Assert.Single(await dest.ListMessagesAsync("Clients/Dupont", default));
        Assert.Contains(folders, f => f.Path == "Clients/Dupont");
        Assert.Equal(MailFlags.Seen | MailFlags.Flagged | MailFlags.Answered, message.Flags);
        Assert.Equal(Day.ToUniversalTime(), message.InternalDate.ToUniversalTime());
        Assert.Equal("adaptateur@test", message.MessageId!.Trim('<', '>'));
        Assert.True(await dest.ContainsMessageIdAsync("Clients/Dupont", "<adaptateur@test>", default));
    }

    [Fact]
    public async Task Large_message_is_streamed_through_a_temp_file_and_stays_intact()
    {
        if (!Enabled) return;
        const int size = 6 * 1024 * 1024;
        await ImapTestKit.SeedAsync(Source, "source", [("INBOX", ImapTestKit.Message("<gros@test>", "Plans du chantier", Day, size, seed: 7), MessageFlags.Seen)]);
        await using var source = new MailKitConnectionFactory().Create(Source, new PasswordAuthProvider("source"));
        await source.ConnectAsync(default);
        var summary = Assert.Single(await source.ListMessagesAsync("INBOX", default));

        await using var stream = await source.DownloadAsync("INBOX", summary.Uid, summary.Size, default);

        Assert.IsType<FileStream>(stream);
        Assert.Equal(summary.Size, stream.Length);
        var parsed = await MimeMessage.LoadAsync(stream);
        var attachment = (MimePart)parsed.Attachments.Single();
        using var content = new MemoryStream();
        await attachment.Content!.DecodeToAsync(content);
        Assert.Equal(ImapTestKit.Payload(size, 7), content.ToArray());
    }

    private static async Task SeedTypicalMailbox()
    {
        var messages = new List<(string, MimeMessage, MessageFlags)>();
        for (var i = 1; i <= 25; i++)
            messages.Add(("INBOX", ImapTestKit.Message($"<inbox{i}@test>", $"Message {i}", Day.AddDays(-i)), i % 3 == 0 ? MessageFlags.Seen | MessageFlags.Flagged : MessageFlags.Seen));
        for (var i = 1; i <= 5; i++)
            messages.Add(("Sent", ImapTestKit.Message($"<sent{i}@test>", $"Réponse {i}", Day.AddHours(-i)), MessageFlags.Seen | MessageFlags.Answered));
        for (var i = 1; i <= 8; i++)
            messages.Add(("Archives/2023", ImapTestKit.Message($"<archive{i}@test>", $"Facture n° 2023-{i:000}", Day.AddYears(-1).AddDays(i)), MessageFlags.Seen));
        messages.Add(("Archives/2023", ImapTestKit.Message(null, "Sans identifiant", Day.AddYears(-1)), MessageFlags.None));
        messages.Add(("Archives/2023", ImapTestKit.Message("<pj@test>", "Pièce jointe lourde", Day.AddYears(-1), 5 * 1024 * 1024, seed: 3), MessageFlags.Seen));
        await ImapTestKit.SeedAsync(Source, "source", messages);
    }

    private (MigrationService Service, SqliteMigrationStore Store, Guid JobId) NewJob()
    {
        var store = new SqliteMigrationStore(_db);
        var service = new MigrationService(store, new MailKitConnectionFactory(), new FakeProtector());
        var id = service.CreateJob(new MigrationJobDefinition
        {
            SourceEmail = Source.UserName,
            Source = Source,
            DestinationEmail = Destination.UserName,
            Destination = Destination,
            Options = new MigrationOptions { ErasePasswordsWhenDone = false },
        }, "source", "destination");
        return (service, store, id);
    }

    [Fact]
    public async Task Engine_migrates_a_real_mailbox_and_rerun_copies_nothing()
    {
        if (!Enabled) return;
        await SeedTypicalMailbox();
        var (service, store, id) = NewJob();
        using var _ = store;

        var status = await service.Open(id).RunAsync();

        var report = store.GetReport(id)!;
        output.WriteLine($"{status} : {report.Summary.Copied} copiés, {report.Summary.Skipped} ignorés, {report.Summary.Failed} échecs");
        Assert.Equal(JobStatus.Completed, status);
        Assert.Equal(40, report.Summary.Copied);
        Assert.All(report.Folders, f => Assert.Equal(0, f.Gap));

        var source = await ImapTestKit.InventoryAsync(Source, "source");
        var dest = await ImapTestKit.InventoryAsync(Destination, "destination");
        foreach (var (folder, messages) in source)
        {
            var copied = dest[folder];
            Assert.Equal(messages.Count, copied.Count);
            foreach (var m in messages)
            {
                var twin = copied.Single(c => c.Envelope!.Subject == m.Envelope!.Subject);
                Assert.Equal(m.Flags & ~MessageFlags.Recent, twin.Flags & ~MessageFlags.Recent);
                Assert.Equal(m.InternalDate?.ToUniversalTime(), twin.InternalDate?.ToUniversalTime());
            }
        }

        var rerun = await service.Open(id).RunAsync();
        var after = await ImapTestKit.InventoryAsync(Destination, "destination");
        Assert.Equal(JobStatus.Completed, rerun);
        Assert.Equal(dest.Sum(f => f.Value.Count), after.Sum(f => f.Value.Count));
    }

    [Fact]
    public async Task Scheduler_runs_queued_jobs_and_resumes_pending_ones_after_restart()
    {
        if (!Enabled) return;
        await SeedTypicalMailbox();
        var definition = new MigrationJobDefinition
        {
            SourceEmail = Source.UserName,
            Source = Source,
            DestinationEmail = Destination.UserName,
            Destination = Destination,
        };

        // 1re « session » : la tâche démarre puis l'application se ferme en pleine copie.
        using (var store = new SqliteMigrationStore(_db))
        {
            var scheduler = new MigrationScheduler(new MigrationService(store, new MailKitConnectionFactory(), new FakeProtector()));
            var job = scheduler.Enqueue(definition, "source", "destination");
            while (job.GetSnapshot().Copied < 10) await Task.Delay(20);
            await scheduler.DisposeAsync();
            Assert.Equal(JobStatus.Queued, store.GetJob(definition.Id)!.Status);
        }

        // 2e session : la tâche est reprise automatiquement et se termine.
        using (var store = new SqliteMigrationStore(_db))
        {
            var scheduler = new MigrationScheduler(new MigrationService(store, new MailKitConnectionFactory(), new FakeProtector()));
            var finished = new TaskCompletionSource<IMigrationJob>();
            scheduler.JobFinished += (_, j) => finished.TrySetResult(j);

            Assert.Equal(1, scheduler.ResumePending());
            var job = await finished.Task.WaitAsync(TimeSpan.FromSeconds(60));

            Assert.Equal(JobStatus.Completed, job.GetSnapshot().Status);
            var dest = await ImapTestKit.InventoryAsync(Destination, "destination");
            Assert.Equal(40, dest.Sum(f => f.Value.Count));
            await scheduler.DisposeAsync();
        }
    }

    [Fact]
    public async Task Engine_resumes_after_interruption_without_duplicates()
    {
        if (!Enabled) return;
        await SeedTypicalMailbox();
        var (service, store, id) = NewJob();
        using var _ = store;

        using var stop = new CancellationTokenSource();
        var job = service.Open(id);
        var run = job.RunAsync(stop.Token);
        while (!run.IsCompleted && job.GetSnapshot().Copied < 12) await Task.Delay(20);
        stop.Cancel();
        Assert.Equal(JobStatus.Queued, await run);
        var partial = (await ImapTestKit.InventoryAsync(Destination, "destination")).Sum(f => f.Value.Count);
        output.WriteLine($"Interrompu après {partial} messages copiés.");

        Assert.Equal(JobStatus.Completed, await service.Open(id).RunAsync());

        var dest = await ImapTestKit.InventoryAsync(Destination, "destination");
        Assert.Equal(40, dest.Sum(f => f.Value.Count));
        Assert.All(dest.Values, msgs => Assert.Equal(msgs.Count, msgs.Select(m => m.Envelope!.Subject).Distinct().Count()));
    }
}

[CollectionDefinition("GreenMail", DisableParallelization = true)]
public sealed class GreenMailCollection;

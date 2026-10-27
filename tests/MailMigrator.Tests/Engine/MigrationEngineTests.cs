using MailMigrator.Core.Engine;
using MailMigrator.Core.Imap;
using MailMigrator.Core.Models;
using MailMigrator.Core.Persistence;

namespace MailMigrator.Tests.Engine;

public sealed class MigrationEngineTests : IDisposable
{
    private static readonly DateTimeOffset Day = new(2024, 3, 12, 9, 0, 0, TimeSpan.FromHours(1));

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mailmigrator-test-{Guid.NewGuid():N}.db");
    private readonly SqliteMigrationStore _store;
    private readonly FakeMailServer _source = new("imap.source.fr");
    private readonly FakeMailServer _destination = new("imap.destination.fr");
    private readonly MigrationService _service;

    public MigrationEngineTests()
    {
        _store = new SqliteMigrationStore(_dbPath);
        var noWait = new RetryPolicy { Delay = (_, _) => Task.CompletedTask };
        _service = new MigrationService(_store, new FakeConnectionFactory(_source, _destination), new FakeProtector(), noWait);

        _source.AddFolder("Sent", SpecialFolder.Sent);
        _source.AddFolder("Trash", SpecialFolder.Trash);
        _source.AddFolder("Clients/Dupont");
        _destination.AddFolder("Envoyés", SpecialFolder.Sent);
        _destination.AddFolder("Corbeille", SpecialFolder.Trash);

        for (var i = 1; i <= 6; i++) _source.Add("INBOX", $"<in{i}@source>", $"Message {i}", Day.AddDays(-i), i % 2 == 0 ? MailFlags.Seen : MailFlags.None);
        _source.Add("Sent", "<sent1@source>", "Devis client Dupont", Day, MailFlags.Seen | MailFlags.Answered);
        _source.Add("Sent", null, "Sans Message-ID", Day.AddHours(-3), MailFlags.Seen | MailFlags.Flagged);
        _source.Add("Clients/Dupont", "<dupont@source>", "Contrat", Day.AddDays(-40), MailFlags.Draft);
        _source.Add("Trash", "<trash@source>", "À jeter", Day);
    }

    private Guid NewJob(MigrationOptions? options = null) => _service.CreateJob(new MigrationJobDefinition
    {
        SourceEmail = "contact@source.fr",
        Source = new ImapEndpoint("imap.source.fr", 993, SecurityMode.SslOnConnect, "contact@source.fr"),
        DestinationEmail = "contact@destination.fr",
        Destination = new ImapEndpoint("imap.destination.fr", 993, SecurityMode.SslOnConnect, "contact@destination.fr"),
        Options = options ?? new MigrationOptions(),
    }, "secret-source", "secret-destination");

    private Task<JobStatus> Run(Guid id, CancellationToken ct = default) => _service.Open(id).RunAsync(ct);

    private IEnumerable<RemoteMessage> Dest(string folder) => _destination.Folders[folder].Messages.Select(m => m.Meta);

    [Fact]
    public async Task Copies_every_folder_with_flags_dates_and_mapping()
    {
        var id = NewJob();

        var status = await Run(id);

        Assert.Equal(JobStatus.Completed, status);
        Assert.Equal(6, Dest("INBOX").Count());
        Assert.Equal(2, Dest("Envoyés").Count());
        Assert.Single(Dest("Clients/Dupont"));
        Assert.Empty(Dest("Corbeille")); // corbeille exclue par défaut

        var devis = Dest("Envoyés").Single(m => m.Subject == "Devis client Dupont");
        Assert.Equal(MailFlags.Seen | MailFlags.Answered, devis.Flags);
        Assert.Equal(Day, devis.InternalDate);
        Assert.Equal(MailFlags.Draft, Dest("Clients/Dupont").Single().Flags);
    }

    [Fact]
    public async Task Records_state_and_erases_passwords_when_done()
    {
        var id = NewJob();

        await Run(id);

        var report = _store.GetReport(id)!;
        Assert.Equal(9, report.Summary.Copied);
        Assert.All(report.Folders, f => Assert.Equal(0, f.Gap));
        Assert.Equal((null, null), _store.GetSecrets(id));
        Assert.Equal(JobStatus.Completed, _store.GetJob(id)!.Status);
    }

    [Fact]
    public async Task Rerun_is_incremental_and_never_duplicates()
    {
        var id = NewJob(new MigrationOptions { ErasePasswordsWhenDone = false });
        await Run(id);

        _source.Add("INBOX", "<nouveau@source>", "Arrivé après", Day.AddDays(1));
        await Run(id);

        Assert.Equal(10, _destination.AppendCount);
        Assert.Equal(7, Dest("INBOX").Count());
        Assert.Equal(Dest("INBOX").Count(), Dest("INBOX").Select(m => m.MessageId).Distinct().Count());
    }

    [Fact]
    public async Task Messages_already_in_destination_are_skipped()
    {
        _destination.Add("INBOX", "<IN1@SOURCE>", "Message 1", Day.AddDays(-1));
        _destination.Add("Envoyés", null, "Sans Message-ID", Day.AddHours(-3));
        var id = NewJob();

        await Run(id);

        var report = _store.GetReport(id)!;
        Assert.Equal(2, report.Summary.Skipped);
        Assert.Equal(6, Dest("INBOX").Count());
        Assert.Equal(2, Dest("Envoyés").Count());
    }

    [Fact]
    public async Task Resumes_after_a_crash_without_duplicates()
    {
        using var crash = new CancellationTokenSource();
        _destination.CrashAfterAppends = (3, crash);
        var id = NewJob();

        var interrupted = await Run(id, crash.Token);

        Assert.Equal(JobStatus.Queued, interrupted);
        Assert.Equal(3, _destination.AppendCount);
        Assert.NotNull(_store.GetSecrets(id).Source); // gardés pour la reprise

        _destination.CrashAfterAppends = null;
        var resumed = await Run(id);

        Assert.Equal(JobStatus.Completed, resumed);
        Assert.Equal(9, _destination.AppendCount);
        Assert.Equal(6, Dest("INBOX").Select(m => m.MessageId).Distinct().Count());
    }

    [Fact]
    public async Task Reconnects_after_network_loss_without_duplicating_the_message_in_flight()
    {
        _destination.DropConnectionAfterAppends = 2; // le 3e APPEND aboutit, puis la connexion tombe
        var id = NewJob();

        var status = await Run(id);

        Assert.Equal(JobStatus.Completed, status);
        Assert.True(_destination.ConnectCount >= 2);
        Assert.Equal(6, Dest("INBOX").Count());
        Assert.Equal(6, Dest("INBOX").Select(m => m.MessageId).Distinct().Count());
    }

    [Fact]
    public async Task Refused_message_is_failed_then_retried_on_demand()
    {
        _destination.RejectedSubjects.Add("Message 3");
        var id = NewJob();

        var first = await Run(id);

        Assert.Equal(JobStatus.CompletedWithErrors, first);
        var failure = _store.GetReport(id)!.Messages.Single(m => m.Status == MessageStatus.Failed);
        Assert.Contains("trop volumineux", failure.Error);
        Assert.NotNull(_store.GetSecrets(id).Source); // gardés pour « Réessayer les échecs »

        _destination.RejectedSubjects.Clear();
        Assert.Equal(1, _store.ResetFailed(id));
        var second = await Run(id);

        Assert.Equal(JobStatus.Completed, second);
        Assert.Equal(6, Dest("INBOX").Count());
    }

    [Fact]
    public async Task Applies_date_range_and_trash_option()
    {
        var id = NewJob(new MigrationOptions { From = DateOnly.FromDateTime(Day.AddDays(-3).Date), IncludeTrash = true });

        await Run(id);

        Assert.Equal(3, Dest("INBOX").Count()); // messages des 1, 2 et 3 derniers jours
        Assert.Empty(Dest("Clients/Dupont"));
        Assert.Single(Dest("Corbeille"));
    }

    [Fact]
    public async Task Source_uidvalidity_change_triggers_reanalysis_without_duplicates()
    {
        var id = NewJob(new MigrationOptions { ErasePasswordsWhenDone = false });
        await Run(id);

        _source.Folders["INBOX"].UidValidity = 99;
        await Run(id);

        Assert.Equal(6, Dest("INBOX").Count());
        Assert.Equal(6, _store.GetReport(id)!.Folders.Single(f => f.SourcePath == "INBOX").SourceCount);
    }

    [Fact]
    public async Task Pause_holds_the_copy_until_resume()
    {
        var id = NewJob();
        var job = _service.Open(id);
        job.Pause();

        var run = job.RunAsync();
        await WaitUntil(() => job.GetSnapshot().Status == JobStatus.Paused);
        Assert.Equal(0, _destination.AppendCount);

        job.Resume();
        Assert.Equal(JobStatus.Completed, await run.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(9, _destination.AppendCount);
    }

    [Fact]
    public async Task Cancel_marks_the_job_cancelled()
    {
        var id = NewJob();
        var job = _service.Open(id);
        job.Pause();
        var run = job.RunAsync();
        await WaitUntil(() => job.GetSnapshot().Status == JobStatus.Paused);

        job.Cancel();

        Assert.Equal(JobStatus.Cancelled, await run.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(JobStatus.Cancelled, _store.GetJob(id)!.Status);
    }

    [Fact]
    public async Task Missing_passwords_fail_with_a_clear_message()
    {
        var id = NewJob();
        _store.ClearSecrets(id);

        var status = await Run(id);

        Assert.Equal(JobStatus.Failed, status);
        Assert.Contains("mots de passe", _store.GetJob(id)!.LastError);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition());
    }

    public void Dispose()
    {
        _store.Dispose();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch (IOException) { }
    }
}

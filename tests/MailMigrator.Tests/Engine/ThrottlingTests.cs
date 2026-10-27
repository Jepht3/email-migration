using MailKit.Net.Imap;
using MailMigrator.Core.Engine;
using MailMigrator.Core.Imap;
using MailMigrator.Core.Models;
using MailMigrator.Core.Persistence;
using MailMigrator.Core.Progress;

namespace MailMigrator.Tests.Engine;

public sealed class ThrottlingTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"mailmigrator-throttle-{Guid.NewGuid():N}.db");
    private static ImapCommandException Limit() => new(ImapCommandResponse.No, "[UNAVAILABLE] Account exceeded command or bandwidth limits");

    [Fact]
    public void Governor_slows_down_then_recovers_gradually()
    {
        var governor = new ThrottleGovernor();

        governor.OnThrottled();
        governor.OnThrottled();
        Assert.Equal((12, TimeSpan.FromSeconds(4)), (governor.BatchSize, governor.Pause));
        Assert.True(governor.IsSlowedDown);

        for (var i = 0; i < 9; i++) Assert.False(governor.OnSuccess());
        Assert.True(governor.OnSuccess());
        Assert.Equal((24, TimeSpan.FromSeconds(2)), (governor.BatchSize, governor.Pause));

        for (var i = 0; i < 30; i++) governor.OnSuccess();
        Assert.False(governor.IsSlowedDown);
        Assert.Equal(ThrottleGovernor.MaxBatchSize, governor.BatchSize);
    }

    [Fact]
    public void Backoff_grows_from_30_seconds_to_5_minutes()
    {
        Assert.Equal([30, 60, 120, 240, 300, 300], Enumerable.Range(1, 6).Select(a => ThrottleGovernor.BackoffFor(a).TotalSeconds));
    }

    [Fact]
    public async Task Retry_policy_waits_on_throttling_instead_of_failing()
    {
        var waits = new List<TimeSpan>();
        var policy = new RetryPolicy { Delay = (d, _) => { waits.Add(d); return Task.CompletedTask; } };
        var calls = 0;
        var notified = 0;

        var result = await policy.ExecuteAsync(_ => ++calls <= 2 ? throw Limit() : Task.FromResult("ok"), (_, _, _) => { notified++; return Task.CompletedTask; }, default);

        Assert.Equal("ok", result);
        Assert.Equal(2, notified);
        Assert.Equal([TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)], waits);
    }

    [Fact]
    public async Task Retry_policy_gives_up_after_repeated_throttling()
    {
        var policy = new RetryPolicy { Delay = (_, _) => Task.CompletedTask, MaxThrottleAttempts = 3 };

        await Assert.ThrowsAsync<ImapCommandException>(() =>
            policy.ExecuteAsync<int>(_ => throw Limit(), (_, _, _) => Task.CompletedTask, default));
    }

    [Fact]
    public async Task Engine_slows_down_and_completes_when_destination_throttles()
    {
        var source = new FakeMailServer("imap.source.fr");
        var destination = new FakeMailServer("imap.destination.fr") { ThrottleNextAppends = 2 };
        for (var i = 1; i <= 120; i++) source.Add("INBOX", $"<m{i}@source>", $"Message {i}", DateTimeOffset.Now.AddDays(-i));
        var waits = new List<TimeSpan>();
        using var store = new SqliteMigrationStore(_db);
        var service = new MigrationService(store, new FakeConnectionFactory(source, destination), new FakeProtector(),
            new RetryPolicy { Delay = (d, _) => { waits.Add(d); return Task.CompletedTask; } });
        var id = service.CreateJob(new MigrationJobDefinition
        {
            SourceEmail = "a@source.fr",
            Source = new ImapEndpoint("imap.source.fr", 993, SecurityMode.SslOnConnect, "a"),
            DestinationEmail = "a@destination.fr",
            Destination = new ImapEndpoint("imap.destination.fr", 993, SecurityMode.SslOnConnect, "a"),
        }, "p", "p");
        var job = service.Open(id);

        var status = await job.RunAsync();

        Assert.Equal(JobStatus.Completed, status);
        Assert.Equal(120, destination.Folders["INBOX"].Messages.Count);
        Assert.Equal(120, destination.Folders["INBOX"].Messages.Select(m => m.Meta.MessageId).Distinct().Count());
        Assert.Contains(TimeSpan.FromSeconds(30), waits);
        Assert.Contains(TimeSpan.FromSeconds(4), waits); // pause entre les lots après deux refus (2 s puis 4 s)
        var activity = new List<ActivityEvent>();
        job.DrainActivity(activity, 1000);
        Assert.Contains(activity, a => a.Kind == ActivityKind.Warning && a.Detail!.Contains("limite le débit"));
        Assert.Equal(0, store.GetReport(id)!.Summary.Failed);
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_db + suffix); } catch (IOException) { }
    }
}

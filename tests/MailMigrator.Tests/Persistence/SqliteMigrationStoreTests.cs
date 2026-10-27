using MailMigrator.Core.Imap;
using MailMigrator.Core.Models;
using MailMigrator.Core.Persistence;

namespace MailMigrator.Tests.Persistence;

public sealed class SqliteMigrationStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"mailmigrator-store-{Guid.NewGuid():N}.db");

    private static readonly MigrationJobDefinition Job = new()
    {
        ColorIndex = 3,
        SourceEmail = "a@source.fr",
        Source = new ImapEndpoint("imap.source.fr", 993, SecurityMode.SslOnConnect, "a@source.fr"),
        DestinationEmail = "a@dest.fr",
        Destination = new ImapEndpoint("mail.dest.fr", 143, SecurityMode.StartTls, "a"),
        Options = new MigrationOptions { IncludeJunk = true, ExcludedFolders = ["Archives"] },
    };

    private static RemoteMessage Msg(uint uid, string subject = "Sujet") =>
        new(uid, $"<{uid}@x>", DateTimeOffset.Now, null, "Jean", "jean@x.fr", subject, 1000 + uid, MailFlags.Seen);

    [Fact]
    public void State_survives_reopening_the_database()
    {
        using (var store = new SqliteMigrationStore(_path))
        {
            store.CreateJob(Job, [1, 2, 3], [4, 5]);
            var folder = store.UpsertFolder(Job.Id, "INBOX", "INBOX", SpecialFolder.Inbox, 7);
            store.AddMessages(folder.Id, [(Msg(1), "k1"), (Msg(2), "k2")]);
            store.SetMessageStatus(store.GetMessages(folder.Id)[0].Id, MessageStatus.Copied);
            store.UpdateJobStatus(Job.Id, JobStatus.Copying);
        }

        using var reopened = new SqliteMigrationStore(_path);
        var job = reopened.GetJob(Job.Id)!;
        Assert.Equal(Job, job.Definition with { Options = job.Definition.Options with { ExcludedFolders = Job.Options.ExcludedFolders, FolderOverrides = Job.Options.FolderOverrides } });
        Assert.Equal(["Archives"], job.Definition.Options.ExcludedFolders);
        Assert.Equal(JobStatus.Copying, job.Status);
        Assert.NotNull(job.StartedAt);
        Assert.Equal(new FolderTotals(1, 1, 0, 0, 1001), reopened.GetTotals(reopened.GetFolders(Job.Id).Single().Id));
    }

    [Fact]
    public void Adding_the_same_messages_twice_is_idempotent()
    {
        using var store = new SqliteMigrationStore(_path);
        store.CreateJob(Job, null, null);
        var folder = store.UpsertFolder(Job.Id, "INBOX", "INBOX", SpecialFolder.Inbox, 7);

        Assert.Equal(2, store.AddMessages(folder.Id, [(Msg(1), "k1"), (Msg(2), "k2")]));
        Assert.Equal(1, store.AddMessages(folder.Id, [(Msg(1), "k1"), (Msg(2), "k2"), (Msg(3), "k3")]));
        Assert.Equal(3, store.GetFolders(Job.Id).Single().SourceCount);
    }

    [Fact]
    public void Uidvalidity_change_forgets_previous_messages()
    {
        using var store = new SqliteMigrationStore(_path);
        store.CreateJob(Job, null, null);
        var folder = store.UpsertFolder(Job.Id, "INBOX", "INBOX", SpecialFolder.Inbox, 7);
        store.AddMessages(folder.Id, [(Msg(1), "k1")]);

        var same = store.UpsertFolder(Job.Id, "INBOX", "INBOX", SpecialFolder.Inbox, 7);
        Assert.Equal(1, same.SourceCount);

        var changed = store.UpsertFolder(Job.Id, "INBOX", "INBOX", SpecialFolder.Inbox, 8);
        Assert.Equal(folder.Id, changed.Id);
        Assert.Equal(0, changed.SourceCount);
    }

    [Fact]
    public void Failed_messages_can_be_reset_and_secrets_cleared()
    {
        using var store = new SqliteMigrationStore(_path);
        store.CreateJob(Job, [1], [2]);
        var folder = store.UpsertFolder(Job.Id, "INBOX", "INBOX", SpecialFolder.Inbox, 1);
        store.AddMessages(folder.Id, [(Msg(1), "k1"), (Msg(2), "k2")]);
        foreach (var m in store.GetMessages(folder.Id)) store.SetMessageStatus(m.Id, MessageStatus.Failed, "refusé");

        Assert.Equal(2, store.ResetFailed(Job.Id));
        Assert.Equal(2, store.GetMessages(folder.Id, MessageStatus.Pending).Count);
        Assert.All(store.GetMessages(folder.Id), m => Assert.Null(m.Error));

        store.ClearSecrets(Job.Id);
        Assert.Equal((null, null), store.GetSecrets(Job.Id));
        Assert.False(store.GetJob(Job.Id)!.HasSecrets);
    }

    [Fact]
    public void Destination_keys_are_replaced_and_extended()
    {
        using var store = new SqliteMigrationStore(_path);
        store.CreateJob(Job, null, null);

        store.ReplaceDestinationKeys(Job.Id, "INBOX", ["a", "b", "a"]);
        store.AddDestinationKey(Job.Id, "INBOX", "c");
        Assert.Equal(["a", "b", "c"], store.GetDestinationKeys(Job.Id, "INBOX").Order());

        store.ReplaceDestinationKeys(Job.Id, "INBOX", ["z"]);
        Assert.Equal(["z"], store.GetDestinationKeys(Job.Id, "INBOX"));
        Assert.Empty(store.GetDestinationKeys(Job.Id, "Sent"));
    }

    [Fact]
    public void History_and_report_aggregate_message_states()
    {
        using var store = new SqliteMigrationStore(_path);
        store.CreateJob(Job, null, null);
        var inbox = store.UpsertFolder(Job.Id, "INBOX", "INBOX", SpecialFolder.Inbox, 1);
        store.AddMessages(inbox.Id, [(Msg(1), "k1"), (Msg(2), "k2"), (Msg(3), "k3")]);
        var m = store.GetMessages(inbox.Id);
        store.SetMessageStatus(m[0].Id, MessageStatus.Copied);
        store.SetMessageStatus(m[1].Id, MessageStatus.Skipped);
        store.SetMessageStatus(m[2].Id, MessageStatus.Failed, "trop volumineux");
        store.SetDestinationCount(inbox.Id, 2);
        store.UpdateJobStatus(Job.Id, JobStatus.CompletedWithErrors);

        var entry = Assert.Single(store.GetAll());
        Assert.Equal((3, 1, 1, 1, 1001L), (entry.SourceMessages, entry.Copied, entry.Skipped, entry.Failed, entry.Bytes));
        Assert.Equal(3, entry.ColorIndex);

        var report = store.GetReport(Job.Id)!;
        Assert.Equal(1, report.Folders.Single().Gap);
        Assert.Equal("trop volumineux", report.Messages.Single(x => x.Status == MessageStatus.Failed).Error);
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_path + suffix); } catch (IOException) { }
    }
}

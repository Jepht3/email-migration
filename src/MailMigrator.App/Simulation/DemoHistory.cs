using MailMigrator.Core.Models;
using MailMigrator.Core.Reporting;

namespace MailMigrator.App.Simulation;

/// <summary>Historique fictif ; sera remplacé par le magasin SQLite à l'étape 3b.</summary>
public sealed class DemoHistory : IMigrationHistory
{
    private readonly List<MigrationHistoryEntry> _entries = [.. DemoData.History];
    private readonly Dictionary<Guid, JobReport> _reports = [];

    public IReadOnlyList<MigrationHistoryEntry> GetAll() => _entries.OrderByDescending(e => e.StartedAt).ToList();

    public void Add(MigrationHistoryEntry entry, JobReport report)
    {
        _entries.Add(entry);
        _reports[entry.Id] = report;
    }

    public JobReport? GetReport(Guid id)
    {
        if (_reports.TryGetValue(id, out var report)) return report;
        var entry = _entries.FirstOrDefault(e => e.Id == id);
        return entry is null ? null : _reports[id] = Build(entry);
    }

    /// <summary>Fabrique un rapport cohérent avec les totaux de l'entrée d'historique.</summary>
    internal static JobReport Build(MigrationHistoryEntry entry)
    {
        var random = new Random(entry.Id.GetHashCode());
        var weights = DemoData.Folders.Select(f => (double)f.Count).ToArray();
        var sum = weights.Sum();
        var folders = new List<FolderReport>();
        int copiedLeft = entry.Copied, skippedLeft = entry.Skipped, failedLeft = entry.Failed;
        for (var i = 0; i < DemoData.Folders.Length; i++)
        {
            var f = DemoData.Folders[i];
            var last = i == DemoData.Folders.Length - 1;
            var share = weights[i] / sum;
            var copied = last ? copiedLeft : (int)(entry.Copied * share);
            var skipped = last ? skippedLeft : (int)(entry.Skipped * share);
            var failed = last ? failedLeft : Math.Min(failedLeft, (int)Math.Round(entry.Failed * share));
            copiedLeft -= copied; skippedLeft -= skipped; failedLeft -= failed;
            var source = copied + skipped + failed;
            folders.Add(new FolderReport(f.Source, f.Destination, f.Role, source, copied, skipped, failed,
                (long)(entry.Bytes * share), source - failed));
        }

        var messages = new List<MessageReportLine>();
        foreach (var folder in folders)
        {
            var sample = Math.Min(folder.SourceCount, 60);
            var failedSample = folder.Failed;
            for (var j = 0; j < sample + failedSample; j++)
            {
                var status = j < failedSample ? MessageStatus.Failed : random.NextDouble() < 0.07 ? MessageStatus.Skipped : MessageStatus.Copied;
                messages.Add(new MessageReportLine(
                    folder.DestinationPath,
                    entry.StartedAt.AddDays(-random.Next(1, 900)).AddMinutes(-random.Next(1440)),
                    DemoData.Senders[random.Next(DemoData.Senders.Length)],
                    DemoData.Subjects[random.Next(DemoData.Subjects.Length)],
                    random.Next(4, 900) * 1024L,
                    status,
                    status == MessageStatus.Failed ? DemoData.Errors[random.Next(DemoData.Errors.Length)] : null));
            }
        }
        return new JobReport(entry, folders, messages.OrderByDescending(m => m.Date).ToList());
    }
}

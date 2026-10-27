using System.Collections.Concurrent;
using System.Diagnostics;
using MailMigrator.Core.Models;

namespace MailMigrator.Core.Progress;

/// <summary>
/// Compteurs vivants d'une tâche, alimentés par le moteur (thread de travail) et lus
/// par l'interface via <see cref="Snapshot"/> (thread UI). Aucun appel au Dispatcher ici.
/// </summary>
public sealed class JobProgress(Guid jobId)
{
    private const int MaxQueuedActivity = 5000;

    private sealed class FolderCounter(string source, string destination, SpecialFolder role)
    {
        public readonly string Source = source, Destination = destination;
        public readonly SpecialFolder Role = role;
        public int Total, Copied, Skipped, Failed;
        public long TotalBytes, BytesProcessed;
    }

    private readonly object _gate = new();
    private readonly List<FolderCounter> _folders = [];
    private readonly ConcurrentQueue<ActivityEvent> _activity = new();
    private readonly RateMeter _rate = new(TimeSpan.FromSeconds(20));
    private readonly Stopwatch _clock = new();
    private TimeSpan _elapsedBefore;
    private JobStatus _status = JobStatus.Queued;
    private int _current = -1;
    private string? _lastError;

    public Guid JobId { get; } = jobId;

    public JobStatus Status
    {
        get { lock (_gate) return _status; }
    }

    public TimeSpan Elapsed
    {
        get { lock (_gate) return _elapsedBefore + _clock.Elapsed; }
    }

    /// <summary>Temps déjà écoulé lors des exécutions précédentes (reprise).</summary>
    public void RestoreElapsed(TimeSpan elapsed)
    {
        lock (_gate) _elapsedBefore = elapsed;
    }

    public void SetStatus(JobStatus status, string? error = null)
    {
        lock (_gate)
        {
            _status = status;
            if (error is not null) _lastError = error;
            if (status.IsRunning()) _clock.Start();
            else _clock.Stop();
            if (status != JobStatus.Copying) _rate.Reset();
        }
    }

    /// <summary>Déclare un dossier ; renvoie son index pour les appels suivants.</summary>
    public int AddFolder(string source, string destination, SpecialFolder role, int total, long totalBytes,
        int copied = 0, int skipped = 0, int failed = 0, long bytesProcessed = 0)
    {
        lock (_gate)
        {
            _folders.Add(new FolderCounter(source, destination, role)
            {
                Total = total, TotalBytes = totalBytes, Copied = copied, Skipped = skipped, Failed = failed, BytesProcessed = bytesProcessed,
            });
            return _folders.Count - 1;
        }
    }

    public void ClearFolders()
    {
        lock (_gate)
        {
            _folders.Clear();
            _current = -1;
        }
    }

    public void SetCurrentFolder(int index)
    {
        lock (_gate) _current = index;
    }

    /// <summary>Un message vient d'être traité.</summary>
    public void Record(int folder, ActivityKind kind, RemoteMessageInfo message, string? detail = null)
    {
        lock (_gate)
        {
            var f = _folders[folder];
            switch (kind)
            {
                case ActivityKind.Copied: f.Copied++; f.BytesProcessed += message.Size; break;
                case ActivityKind.Skipped: f.Skipped++; f.BytesProcessed += message.Size; break;
                case ActivityKind.Failed: f.Failed++; f.BytesProcessed += message.Size; break;
            }
            _rate.Record(DateTimeOffset.UtcNow, _folders.Sum(x => x.Copied + x.Skipped + x.Failed), _folders.Sum(x => x.BytesProcessed));
            Enqueue(new ActivityEvent(DateTimeOffset.Now, kind, f.Destination, message.Date, message.Sender, message.Subject, message.Size, detail));
        }
    }

    /// <summary>Annule le comptage d'un échec avant une nouvelle tentative.</summary>
    public void Uncount(int folder, long size)
    {
        lock (_gate)
        {
            _folders[folder].Failed--;
            _folders[folder].BytesProcessed -= size;
        }
    }

    public void Info(string text) => Enqueue(new ActivityEvent(DateTimeOffset.Now, ActivityKind.Info, null, null, null, null, null, text));

    public void Warning(string text) => Enqueue(new ActivityEvent(DateTimeOffset.Now, ActivityKind.Warning, null, null, null, null, null, text));

    private void Enqueue(ActivityEvent e)
    {
        _activity.Enqueue(e);
        while (_activity.Count > MaxQueuedActivity) _activity.TryDequeue(out _);
    }

    public int DrainActivity(ICollection<ActivityEvent> into, int max)
    {
        var n = 0;
        while (n < max && _activity.TryDequeue(out var e))
        {
            into.Add(e);
            n++;
        }
        return n;
    }

    public JobSnapshot Snapshot()
    {
        lock (_gate)
        {
            var total = _folders.Sum(f => f.Total);
            var processed = _folders.Sum(f => f.Copied + f.Skipped + f.Failed);
            var (perMinute, bytesPerSecond) = _status == JobStatus.Copying ? _rate.Current : (0, 0);
            return new JobSnapshot
            {
                JobId = JobId,
                Status = _status,
                CurrentFolder = _current >= 0 && _current < _folders.Count && _status is JobStatus.Copying or JobStatus.Paused
                    ? _folders[_current].Destination
                    : null,
                TotalMessages = total,
                Copied = _folders.Sum(f => f.Copied),
                Skipped = _folders.Sum(f => f.Skipped),
                Failed = _folders.Sum(f => f.Failed),
                TotalBytes = _folders.Sum(f => f.TotalBytes),
                BytesProcessed = _folders.Sum(f => f.BytesProcessed),
                Elapsed = _elapsedBefore + _clock.Elapsed,
                MessagesPerMinute = perMinute,
                BytesPerSecond = bytesPerSecond,
                Remaining = perMinute > 1 ? TimeSpan.FromMinutes((total - processed) / perMinute) : null,
                Folders = _folders.Select(f => new FolderProgress(f.Source, f.Destination, f.Role, f.Total, f.Copied, f.Skipped, f.Failed, f.TotalBytes, f.BytesProcessed)).ToList(),
                LastError = _lastError,
            };
        }
    }
}

/// <summary>Informations d'un message utiles au flux d'activité.</summary>
public readonly record struct RemoteMessageInfo(DateTimeOffset? Date, string Sender, string Subject, long Size);

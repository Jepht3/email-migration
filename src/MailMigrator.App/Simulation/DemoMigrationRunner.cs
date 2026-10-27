using System.Collections.Concurrent;
using MailMigrator.Core.Abstractions;
using MailMigrator.Core.Models;
using MailMigrator.Core.Progress;
using MailMigrator.Core.Reporting;

namespace MailMigrator.App.Simulation;

/// <summary>
/// Simulateur de migrations : même contrat que le vrai moteur (IMigrationRunner),
/// pour valider l'interface avec 5 tâches qui progressent réellement.
/// </summary>
public sealed class DemoMigrationRunner : IMigrationRunner, IDisposable
{
    private readonly List<SimJob> _jobs = [];
    private readonly object _gate = new();
    private readonly DemoHistory _history;
    private readonly CancellationTokenSource _stop = new();
    private int _maxConcurrency = 5;

    public DemoMigrationRunner(DemoHistory history)
    {
        _history = history;
        _ = Task.Run(LoopAsync);
    }

    public IReadOnlyList<IMigrationJob> Jobs
    {
        get { lock (_gate) return _jobs.ToList(); }
    }

    public int MaxConcurrency
    {
        get => _maxConcurrency;
        set => _maxConcurrency = Math.Clamp(value, 1, 5);
    }

    public event EventHandler<IMigrationJob>? JobAdded;
    public event EventHandler<IMigrationJob>? JobFinished;

    public IMigrationJob Enqueue(MigrationJobDefinition definition) => Enqueue(definition, 0, JobStatus.Queued, 1.0);

    /// <summary>Ajoute une tâche déjà partiellement avancée (jeu de démonstration).</summary>
    public IMigrationJob Enqueue(MigrationJobDefinition definition, double prefill, JobStatus initial, double sizeFactor)
    {
        var job = new SimJob(definition, sizeFactor, prefill, initial, new Random(definition.Id.GetHashCode()));
        lock (_gate) _jobs.Add(job);
        JobAdded?.Invoke(this, job);
        return job;
    }

    /// <summary>Les 5 migrations simultanées + 1 en file d'attente, à différents stades.</summary>
    public void SeedDemo()
    {
        (double Prefill, JobStatus Status, double Size)[] scenarios =
        [
            (0.46, JobStatus.Copying, 1.0),
            (0.23, JobStatus.Copying, 1.3),
            (0.0, JobStatus.Analyzing, 0.8),
            (0.63, JobStatus.Paused, 0.7),
            (0.84, JobStatus.Copying, 0.5),
            (0.0, JobStatus.Queued, 0.35),
        ];
        for (var i = 0; i < scenarios.Length; i++)
        {
            var (src, dst) = DemoData.Pairs[i];
            var s = scenarios[i];
            Enqueue(new MigrationJobDefinition { ColorIndex = i % 5, SourceEmail = src, DestinationEmail = dst }, s.Prefill, s.Status, s.Size);
        }
    }

    private async Task LoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        var last = DateTimeOffset.UtcNow;
        while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
        {
            var now = DateTimeOffset.UtcNow;
            var dt = (now - last).TotalSeconds;
            last = now;

            List<SimJob> jobs;
            lock (_gate) jobs = _jobs.ToList();

            var active = jobs.Count(j => j.Status.IsActive());
            foreach (var queued in jobs.Where(j => j.Status == JobStatus.Queued))
            {
                if (active >= _maxConcurrency) break;
                queued.Start();
                active++;
            }

            foreach (var job in jobs)
            {
                if (job.Tick(dt, now))
                {
                    var report = job.BuildReport();
                    _history.Add(report.Summary, report);
                    JobFinished?.Invoke(this, job);
                }
            }
        }
    }

    public void Dispose() => _stop.Cancel();

    private sealed class SimJob : IMigrationJob
    {
        private readonly object _gate = new();
        private readonly Random _random;
        private readonly ConcurrentQueue<ActivityEvent> _activity = new();
        private readonly RateMeter _rate = new(TimeSpan.FromSeconds(8));
        private readonly (DemoData.DemoFolder Info, int Total, long AvgBytes)[] _folders;
        private readonly int[] _copied, _skipped, _failed;
        private readonly long[] _bytes;
        private readonly double _baseSpeed;
        private readonly double _failureRate;
        private readonly List<MessageReportLine> _lines = [];
        private JobStatus _status;
        private JobStatus _statusBeforePause;
        private double _phaseTime;
        private double _budget;
        private TimeSpan _elapsed;
        private DateTimeOffset _startedAt = DateTimeOffset.Now;

        public SimJob(MigrationJobDefinition definition, double sizeFactor, double prefill, JobStatus initial, Random random)
        {
            Definition = definition;
            _random = random;
            _folders = DemoData.Folders
                .Select(f => (f, Math.Max(3, (int)(f.Count * sizeFactor * (0.7 + random.NextDouble() * 0.6))), f.AvgKb * 1024L))
                .ToArray();
            _copied = new int[_folders.Length];
            _skipped = new int[_folders.Length];
            _failed = new int[_folders.Length];
            _bytes = new long[_folders.Length];
            _baseSpeed = 7 + random.NextDouble() * 9;
            _failureRate = definition.ColorIndex == 4 ? 0.012 : 0.003;

            if (prefill > 0)
            {
                var target = (int)(_folders.Sum(f => f.Total) * prefill);
                for (var i = 0; i < target; i++) Process(DateTimeOffset.UtcNow, emit: false);
                _elapsed = TimeSpan.FromSeconds(target / _baseSpeed);
                _startedAt = DateTimeOffset.Now - _elapsed;
            }
            _status = initial;
            if (initial == JobStatus.Paused) _statusBeforePause = JobStatus.Copying;
            if (initial is JobStatus.Copying or JobStatus.Analyzing)
                Emit(ActivityKind.Info, null, $"Reprise de la migration de {definition.SourceEmail}");
        }

        public MigrationJobDefinition Definition { get; }

        public JobStatus Status
        {
            get { lock (_gate) return _status; }
        }

        public void Start()
        {
            lock (_gate)
            {
                if (_status != JobStatus.Queued) return;
                _status = JobStatus.Connecting;
                _phaseTime = 0;
                _startedAt = DateTimeOffset.Now;
            }
            Emit(ActivityKind.Info, null, $"Connexion à {Host(Definition.SourceEmail)} et {Host(Definition.DestinationEmail)}…");
        }

        public void Pause()
        {
            lock (_gate)
            {
                if (!_status.IsRunning()) return;
                _statusBeforePause = _status;
                _status = JobStatus.Paused;
            }
            Emit(ActivityKind.Info, null, "Migration mise en pause");
        }

        public void Resume()
        {
            lock (_gate)
            {
                if (_status != JobStatus.Paused) return;
                _status = _statusBeforePause;
                _rate.Reset();
            }
            Emit(ActivityKind.Info, null, "Reprise de la migration");
        }

        public void Cancel()
        {
            lock (_gate)
            {
                if (_status.IsFinished()) return;
                _status = JobStatus.Cancelled;
            }
            Emit(ActivityKind.Warning, null, "Migration annulée par l'utilisateur");
        }

        /// <summary>Fait avancer la simulation ; vrai si la tâche vient de se terminer.</summary>
        public bool Tick(double dt, DateTimeOffset now)
        {
            lock (_gate)
            {
                if (!_status.IsRunning()) return false;
                _elapsed += TimeSpan.FromSeconds(dt);
                _phaseTime += dt;

                switch (_status)
                {
                    case JobStatus.Connecting when _phaseTime > 1.4:
                        _status = JobStatus.Analyzing;
                        _phaseTime = 0;
                        Emit(ActivityKind.Info, null, "Connecté — analyse des dossiers");
                        break;
                    case JobStatus.Analyzing when _phaseTime > 3.0:
                        _status = JobStatus.Copying;
                        _phaseTime = 0;
                        Emit(ActivityKind.Info, null, $"Analyse terminée : {_folders.Sum(f => f.Total):N0} messages dans {_folders.Length} dossiers");
                        break;
                    case JobStatus.Copying:
                        // Débit irrégulier, comme sur un vrai serveur.
                        _budget += dt * _baseSpeed * (0.55 + _random.NextDouble() * 0.9);
                        while (_budget >= 1)
                        {
                            _budget--;
                            if (!Process(now, emit: true))
                            {
                                _status = JobStatus.Verifying;
                                _phaseTime = 0;
                                Emit(ActivityKind.Info, null, "Vérification : recomptage des messages par dossier");
                                break;
                            }
                        }
                        break;
                    case JobStatus.Verifying when _phaseTime > 2.2:
                        _status = _failed.Sum() > 0 ? JobStatus.CompletedWithErrors : JobStatus.Completed;
                        Emit(ActivityKind.Info, null, "Migration terminée");
                        return true;
                }
                _rate.Record(now, Processed, _bytes.Sum());
                return false;
            }
        }

        private int Processed => _copied.Sum() + _skipped.Sum() + _failed.Sum();

        private bool Process(DateTimeOffset now, bool emit)
        {
            var index = -1;
            for (var i = 0; i < _folders.Length; i++)
            {
                if (_copied[i] + _skipped[i] + _failed[i] < _folders[i].Total) { index = i; break; }
            }
            if (index < 0) return false;

            var folder = _folders[index];
            var size = (long)(folder.AvgBytes * Math.Exp(_random.NextDouble() * 1.6 - 0.8));
            var roll = _random.NextDouble();
            var kind = roll < _failureRate ? ActivityKind.Failed : roll < 0.08 ? ActivityKind.Skipped : ActivityKind.Copied;
            switch (kind)
            {
                case ActivityKind.Failed: _failed[index]++; break;
                case ActivityKind.Skipped: _skipped[index]++; _bytes[index] += size; break;
                default: _copied[index]++; _bytes[index] += size; break;
            }

            var date = DateTimeOffset.Now.AddDays(-_random.Next(1, 1100)).AddMinutes(-_random.Next(1440));
            var sender = DemoData.Senders[_random.Next(DemoData.Senders.Length)];
            var subject = DemoData.Subjects[_random.Next(DemoData.Subjects.Length)];
            var error = kind == ActivityKind.Failed ? DemoData.Errors[_random.Next(DemoData.Errors.Length)] : null;
            if (_lines.Count < 4000 || kind == ActivityKind.Failed)
            {
                var status = kind switch { ActivityKind.Failed => MessageStatus.Failed, ActivityKind.Skipped => MessageStatus.Skipped, _ => MessageStatus.Copied };
                _lines.Add(new MessageReportLine(folder.Info.Destination, date, sender, subject, size, status, error));
            }
            if (emit)
            {
                _activity.Enqueue(new ActivityEvent(now, kind, folder.Info.Destination, date, sender, subject, size,
                    kind == ActivityKind.Skipped ? "Déjà présent dans la destination" : error));
                while (_activity.Count > 3000) _activity.TryDequeue(out _);
            }
            return true;
        }

        private void Emit(ActivityKind kind, string? folder, string detail) =>
            _activity.Enqueue(new ActivityEvent(DateTimeOffset.Now, kind, folder, null, null, null, null, detail));

        public JobSnapshot GetSnapshot()
        {
            lock (_gate)
            {
                var processed = Processed;
                var total = _folders.Sum(f => f.Total);
                var totalBytes = _folders.Sum(f => f.Total * f.AvgBytes);
                var (perMinute, bytesPerSecond) = _status == JobStatus.Copying ? _rate.Current : (0, 0);
                var current = _status is JobStatus.Copying or JobStatus.Paused
                    ? _folders.Select((f, i) => (f, i)).FirstOrDefault(x => _copied[x.i] + _skipped[x.i] + _failed[x.i] < x.f.Total).f.Info?.Destination
                    : null;
                return new JobSnapshot
                {
                    JobId = Definition.Id,
                    Status = _status,
                    CurrentFolder = current,
                    TotalMessages = _status is JobStatus.Queued or JobStatus.Connecting ? 0 : total,
                    Copied = _copied.Sum(),
                    Skipped = _skipped.Sum(),
                    Failed = _failed.Sum(),
                    TotalBytes = totalBytes,
                    BytesProcessed = Math.Min(totalBytes, _bytes.Sum()),
                    Elapsed = _elapsed,
                    MessagesPerMinute = perMinute,
                    BytesPerSecond = bytesPerSecond,
                    Remaining = perMinute > 1 ? TimeSpan.FromMinutes((total - processed) / perMinute) : null,
                    Folders = (_status is JobStatus.Queued or JobStatus.Connecting or JobStatus.Analyzing)
                        ? []
                        : _folders.Select((f, i) => new FolderProgress(f.Info.Source, f.Info.Destination, f.Info.Role, f.Total,
                            _copied[i], _skipped[i], _failed[i], f.Total * f.AvgBytes, _bytes[i])).ToList(),
                };
            }
        }

        public int DrainActivity(ICollection<ActivityEvent> into, int max)
        {
            var count = 0;
            while (count < max && _activity.TryDequeue(out var e))
            {
                into.Add(e);
                count++;
            }
            return count;
        }

        public JobReport BuildReport()
        {
            lock (_gate)
            {
                var summary = new MigrationHistoryEntry
                {
                    Id = Definition.Id,
                    ColorIndex = Definition.ColorIndex,
                    SourceEmail = Definition.SourceEmail,
                    DestinationEmail = Definition.DestinationEmail,
                    StartedAt = _startedAt,
                    Duration = _elapsed,
                    Status = _status,
                    SourceMessages = _folders.Sum(f => f.Total),
                    Copied = _copied.Sum(),
                    Skipped = _skipped.Sum(),
                    Failed = _failed.Sum(),
                    Bytes = _bytes.Sum(),
                };
                var folders = _folders.Select((f, i) => new FolderReport(f.Info.Source, f.Info.Destination, f.Info.Role, f.Total,
                    _copied[i], _skipped[i], _failed[i], _bytes[i], f.Total - _failed[i])).ToList();
                return new JobReport(summary, folders, _lines.OrderByDescending(l => l.Date).ToList());
            }
        }

        private static string Host(string email) => "imap." + email.Split('@').Last();
    }
}

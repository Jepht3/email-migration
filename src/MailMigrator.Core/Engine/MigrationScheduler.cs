using MailMigrator.Core.Abstractions;
using MailMigrator.Core.Models;

namespace MailMigrator.Core.Engine;

/// <summary>
/// File d'attente des tâches réelles : 1 à 5 exécutées en parallèle, chacune avec ses propres
/// connexions, les suivantes en attente. Les tâches inachevées reprennent au démarrage.
/// </summary>
public sealed class MigrationScheduler(MigrationService service) : IMigrationRunner, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly List<EngineJob> _jobs = [];
    private readonly Queue<EngineJob> _queue = new();
    private readonly Dictionary<EngineJob, Task> _running = [];
    private readonly CancellationTokenSource _shutdown = new();
    private int _maxConcurrency = 5;

    public IReadOnlyList<IMigrationJob> Jobs
    {
        get { lock (_gate) return _jobs.ToList(); }
    }

    public int MaxConcurrency
    {
        get => _maxConcurrency;
        set
        {
            _maxConcurrency = Math.Clamp(value, 1, 5);
            Pump();
        }
    }

    public event EventHandler<IMigrationJob>? JobAdded;
    public event EventHandler<IMigrationJob>? JobFinished;

    public IMigrationJob Enqueue(MigrationJobDefinition definition, string sourcePassword, string destinationPassword)
    {
        service.CreateJob(definition, sourcePassword, destinationPassword);
        var job = service.Open(definition.Id);
        Add(job);
        return job;
    }

    /// <summary>Remet en file les tâches interrompues (fermeture de l'application, plantage).</summary>
    public int ResumePending()
    {
        var pending = service.Store.GetAll().Where(e => !e.Status.IsFinished()).OrderBy(e => e.StartedAt).ToList();
        foreach (var entry in pending)
        {
            lock (_gate)
            {
                if (_jobs.Any(j => j.Definition.Id == entry.Id)) continue;
            }
            Add(service.Open(entry.Id));
        }
        return pending.Count;
    }

    public bool RetryFailed(Guid jobId)
    {
        EngineJob? job;
        lock (_gate)
        {
            job = _jobs.FirstOrDefault(j => j.Definition.Id == jobId);
            if (job is not null && (_running.ContainsKey(job) || _queue.Contains(job))) return false;
        }
        if (service.Store.ResetFailed(jobId) == 0) return false;

        if (job is null)
        {
            Add(service.Open(jobId));
        }
        else
        {
            job.Progress.Info("Nouvelle tentative des messages en échec.");
            lock (_gate) _queue.Enqueue(job);
            Pump();
        }
        return true;
    }

    private void Add(EngineJob job)
    {
        lock (_gate)
        {
            _jobs.Add(job);
            _queue.Enqueue(job);
        }
        JobAdded?.Invoke(this, job);
        Pump();
    }

    private void Pump()
    {
        lock (_gate)
        {
            while (_running.Count < _maxConcurrency && _queue.Count > 0 && !_shutdown.IsCancellationRequested)
            {
                var job = _queue.Dequeue();
                _running[job] = Task.Run(() => RunAsync(job));
            }
        }
    }

    private async Task RunAsync(EngineJob job)
    {
        JobStatus status;
        try
        {
            status = await job.RunAsync(_shutdown.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) _running.Remove(job);
        }
        if (status.IsFinished()) JobFinished?.Invoke(this, job);
        Pump();
    }

    /// <summary>Arrêt de l'application : les tâches s'interrompent proprement et reprendront au prochain lancement.</summary>
    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        Task[] running;
        lock (_gate) running = _running.Values.ToArray();
        await Task.WhenAny(Task.WhenAll(running), Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
    }
}

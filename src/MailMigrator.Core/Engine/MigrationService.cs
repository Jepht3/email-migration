using MailMigrator.Core.Abstractions;
using MailMigrator.Core.Connectivity;
using MailMigrator.Core.Imap;
using MailMigrator.Core.Models;
using MailMigrator.Core.Persistence;
using MailMigrator.Core.Progress;
using Microsoft.Extensions.Logging;

namespace MailMigrator.Core.Engine;

/// <summary>Création des tâches (mots de passe chiffrés) et exécution par le moteur.</summary>
public sealed class MigrationService(
    IMigrationStore store,
    IMailboxConnectionFactory connections,
    ISecretProtector secrets,
    RetryPolicy? retry = null,
    ILogger<MigrationEngine>? logger = null)
{
    public IMigrationStore Store => store;

    /// <summary>Enregistre une tâche ; les mots de passe ne sont stockés que chiffrés.</summary>
    public Guid CreateJob(MigrationJobDefinition definition, string sourcePassword, string destinationPassword)
    {
        store.CreateJob(definition, secrets.Protect(sourcePassword), secrets.Protect(destinationPassword));
        return definition.Id;
    }

    /// <summary>Poignée d'exécution sur une tâche existante (nouvelle ou à reprendre).</summary>
    public EngineJob Open(Guid jobId)
    {
        var job = store.GetJob(jobId) ?? throw new InvalidOperationException($"Tâche {jobId} introuvable.");
        return new EngineJob(job.Definition, this);
    }

    internal async Task<JobStatus> ExecuteAsync(EngineJob job, CancellationToken cancellationToken)
    {
        var (source, destination) = store.GetSecrets(job.Definition.Id);
        if (source is null || destination is null)
            throw new InvalidOperationException("Les mots de passe de cette tâche ont été effacés : saisissez-les à nouveau pour la relancer.");

        var engine = new MigrationEngine(store, connections, retry, logger);
        return await engine.RunAsync(job.Definition.Id,
            new PasswordAuthProvider(secrets.Unprotect(source)),
            new PasswordAuthProvider(secrets.Unprotect(destination)),
            job.Progress, job.PauseController, cancellationToken);
    }
}

/// <summary>Une tâche exécutée par le vrai moteur, vue par l'interface (<see cref="IMigrationJob"/>).</summary>
public sealed class EngineJob : IMigrationJob
{
    private readonly MigrationService _service;
    private CancellationTokenSource? _cts;
    private bool _cancelledByUser;

    internal EngineJob(MigrationJobDefinition definition, MigrationService service)
    {
        Definition = definition;
        _service = service;
        Progress = new JobProgress(definition.Id);
    }

    public MigrationJobDefinition Definition { get; }
    public JobProgress Progress { get; }
    internal PauseController PauseController { get; } = new();

    /// <summary>
    /// Exécute la tâche jusqu'au bout. Si elle est interrompue (fermeture de l'application),
    /// elle reste « en file d'attente » et reprendra au même point au prochain lancement.
    /// </summary>
    public async Task<JobStatus> RunAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            return await _service.ExecuteAsync(this, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            var status = _cancelledByUser ? JobStatus.Cancelled : JobStatus.Queued;
            _service.Store.UpdateJobStatus(Definition.Id, status);
            Progress.SetStatus(status);
            if (_cancelledByUser) Progress.Warning("Migration annulée par l'utilisateur.");
            return status;
        }
        catch (InvalidOperationException ex)
        {
            _service.Store.UpdateJobStatus(Definition.Id, JobStatus.Failed, ex.Message);
            Progress.SetStatus(JobStatus.Failed, ex.Message);
            Progress.Warning(ex.Message);
            return JobStatus.Failed;
        }
    }

    public JobSnapshot GetSnapshot() => Progress.Snapshot();

    public int DrainActivity(ICollection<ActivityEvent> into, int max) => Progress.DrainActivity(into, max);

    public void Pause()
    {
        PauseController.Pause();
        if (Progress.Status == JobStatus.Copying) Progress.SetStatus(JobStatus.Paused);
        Progress.Info("Mise en pause demandée.");
    }

    public void Resume()
    {
        PauseController.Resume();
        if (Progress.Status == JobStatus.Paused) Progress.SetStatus(JobStatus.Copying);
        Progress.Info("Reprise de la migration.");
    }

    public void Cancel()
    {
        _cancelledByUser = true;
        _cts?.Cancel();
        PauseController.Resume();
    }
}

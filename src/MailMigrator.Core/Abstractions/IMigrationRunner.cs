using MailMigrator.Core.Models;
using MailMigrator.Core.Progress;

namespace MailMigrator.Core.Abstractions;

/// <summary>
/// Orchestrateur des tâches : file d'attente + exécution de 1 à 5 tâches en parallèle.
/// Implémenté par le vrai moteur IMAP et par le simulateur de démonstration.
/// </summary>
public interface IMigrationRunner
{
    IReadOnlyList<IMigrationJob> Jobs { get; }

    /// <summary>Nombre maximal de tâches exécutées simultanément (1 à 5).</summary>
    int MaxConcurrency { get; set; }

    event EventHandler<IMigrationJob>? JobAdded;
    event EventHandler<IMigrationJob>? JobFinished;

    IMigrationJob Enqueue(MigrationJobDefinition definition);
}

/// <summary>Poignée sur une tâche en cours ou terminée.</summary>
public interface IMigrationJob
{
    MigrationJobDefinition Definition { get; }

    JobSnapshot GetSnapshot();

    /// <summary>Transfère au plus <paramref name="max"/> événements d'activité en attente.</summary>
    int DrainActivity(ICollection<ActivityEvent> into, int max);

    void Pause();
    void Resume();
    void Cancel();
}

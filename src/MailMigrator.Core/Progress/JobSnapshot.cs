using MailMigrator.Core.Models;

namespace MailMigrator.Core.Progress;

/// <summary>Avancement d'un dossier à un instant donné.</summary>
public sealed record FolderProgress(
    string SourcePath,
    string DestinationPath,
    SpecialFolder Role,
    int Total,
    int Copied,
    int Skipped,
    int Failed,
    long TotalBytes,
    long BytesProcessed)
{
    public int Processed => Copied + Skipped + Failed;
    public double Fraction => Total == 0 ? 1 : Math.Clamp(Processed / (double)Total, 0, 1);
    public bool IsCompleted => Processed >= Total;
}

/// <summary>
/// Photographie immuable de l'état d'une tâche. Le moteur la produit à la demande ;
/// l'interface la lit au rythme qui lui convient (~10 fois par seconde).
/// </summary>
public sealed record JobSnapshot
{
    public required Guid JobId { get; init; }
    public JobStatus Status { get; init; }
    public string? CurrentFolder { get; init; }
    public int TotalMessages { get; init; }
    public int Copied { get; init; }
    public int Skipped { get; init; }
    public int Failed { get; init; }
    public long TotalBytes { get; init; }
    public long BytesProcessed { get; init; }
    public TimeSpan Elapsed { get; init; }
    public double MessagesPerMinute { get; init; }
    public double BytesPerSecond { get; init; }
    public TimeSpan? Remaining { get; init; }
    public IReadOnlyList<FolderProgress> Folders { get; init; } = [];
    public string? LastError { get; init; }

    public int Processed => Copied + Skipped + Failed;
    public double Fraction => TotalMessages == 0 ? 0 : Math.Clamp(Processed / (double)TotalMessages, 0, 1);
}

public enum ActivityKind
{
    Copied,
    Skipped,
    Failed,
    Info,
    Warning,
}

/// <summary>Ligne du flux d'activité en direct.</summary>
public sealed record ActivityEvent(
    DateTimeOffset Timestamp,
    ActivityKind Kind,
    string? Folder,
    DateTimeOffset? MessageDate,
    string? Sender,
    string? Subject,
    long? Size,
    string? Detail);

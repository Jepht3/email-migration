using MailMigrator.Core.Models;

namespace MailMigrator.Core.Reporting;

/// <summary>Ligne de l'historique des migrations.</summary>
public sealed record MigrationHistoryEntry
{
    public required Guid Id { get; init; }
    public int ColorIndex { get; init; }
    public required string SourceEmail { get; init; }
    public required string DestinationEmail { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public TimeSpan Duration { get; init; }
    public JobStatus Status { get; init; }
    public int SourceMessages { get; init; }
    public int Copied { get; init; }
    public int Skipped { get; init; }
    public int Failed { get; init; }
    public long Bytes { get; init; }

    public double SuccessRate => SourceMessages == 0 ? 1 : (Copied + Skipped) / (double)SourceMessages;
}

/// <summary>Bilan d'un dossier, avec le recomptage final source / destination.</summary>
public sealed record FolderReport(
    string SourcePath,
    string DestinationPath,
    SpecialFolder Role,
    int SourceCount,
    int Copied,
    int Skipped,
    int Failed,
    long Bytes,
    int DestinationCountAfter)
{
    /// <summary>Écart après migration : messages présents à la source mais absents de la destination.</summary>
    public int Gap => Math.Max(0, SourceCount - DestinationCountAfter);
}

/// <summary>Un message traité, tel qu'il apparaît dans la liste détaillée du rapport.</summary>
public sealed record MessageReportLine(
    string Folder,
    DateTimeOffset Date,
    string Sender,
    string Subject,
    long Size,
    MessageStatus Status,
    string? Error);

/// <summary>Rapport complet d'une tâche.</summary>
public sealed record JobReport(
    MigrationHistoryEntry Summary,
    IReadOnlyList<FolderReport> Folders,
    IReadOnlyList<MessageReportLine> Messages);

/// <summary>Accès à l'historique des migrations (SQLite en production, données fictives en démo).</summary>
public interface IMigrationHistory
{
    IReadOnlyList<MigrationHistoryEntry> GetAll();
    JobReport? GetReport(Guid id);
}

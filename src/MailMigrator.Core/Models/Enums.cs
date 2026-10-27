namespace MailMigrator.Core.Models;

/// <summary>Cycle de vie d'une tâche de migration.</summary>
public enum JobStatus
{
    Queued,
    Connecting,
    Analyzing,
    Copying,
    Verifying,
    Paused,
    Completed,
    CompletedWithErrors,
    Failed,
    Cancelled,
}

/// <summary>État d'un message dans le journal de migration (persisté).</summary>
public enum MessageStatus
{
    Pending,
    Copied,
    Skipped,
    Failed,
}

/// <summary>Mode de chiffrement de la connexion IMAP.</summary>
public enum SecurityMode
{
    SslOnConnect,
    StartTls,
    None,
}

/// <summary>Rôle d'un dossier, issu des attributs SPECIAL-USE (RFC 6154) ou déduit du nom.</summary>
public enum SpecialFolder
{
    None,
    Inbox,
    Sent,
    Drafts,
    Trash,
    Junk,
    Archive,
}

public static class JobStatusExtensions
{
    public static bool IsRunning(this JobStatus status) =>
        status is JobStatus.Connecting or JobStatus.Analyzing or JobStatus.Copying or JobStatus.Verifying;

    public static bool IsActive(this JobStatus status) => status.IsRunning() || status == JobStatus.Paused;

    public static bool IsFinished(this JobStatus status) =>
        status is JobStatus.Completed or JobStatus.CompletedWithErrors or JobStatus.Failed or JobStatus.Cancelled;
}

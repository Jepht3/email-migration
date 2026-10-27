using MailMigrator.Core.Imap;
using MailMigrator.Core.Models;
using MailMigrator.Core.Reporting;

namespace MailMigrator.Core.Persistence;

public sealed record StoredJob(
    MigrationJobDefinition Definition,
    JobStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    TimeSpan Elapsed,
    string? LastError,
    bool HasSecrets);

public sealed record StoredFolder(
    long Id,
    string SourcePath,
    string DestinationPath,
    SpecialFolder Role,
    uint UidValidity,
    int SourceCount,
    long SourceBytes,
    int? DestinationCountAfter);

public sealed record StoredMessage(
    long Id,
    long FolderId,
    uint Uid,
    string Key,
    string? MessageId,
    DateTimeOffset InternalDate,
    DateTimeOffset? Date,
    string Sender,
    string Subject,
    long Size,
    MailFlags Flags,
    MessageStatus Status,
    string? Error);

/// <summary>Compteurs d'un dossier, pour reprendre la progression là où elle en était.</summary>
public sealed record FolderTotals(int Pending, int Copied, int Skipped, int Failed, long BytesProcessed);

/// <summary>
/// État durable des migrations : chaque message passe par en attente → copié / ignoré / échec,
/// enregistré au fil de l'eau pour pouvoir reprendre après un arrêt.
/// </summary>
public interface IMigrationStore : IMigrationHistory
{
    void CreateJob(MigrationJobDefinition definition, byte[]? sourceSecret, byte[]? destinationSecret);
    StoredJob? GetJob(Guid jobId);
    void UpdateJobStatus(Guid jobId, JobStatus status, string? error = null);
    void SaveElapsed(Guid jobId, TimeSpan elapsed);

    (byte[]? Source, byte[]? Destination) GetSecrets(Guid jobId);
    void ClearSecrets(Guid jobId);

    /// <summary>Crée ou retrouve le dossier ; si l'UIDVALIDITY a changé, ses messages sont oubliés.</summary>
    StoredFolder UpsertFolder(Guid jobId, string sourcePath, string destinationPath, SpecialFolder role, uint uidValidity);
    IReadOnlyList<StoredFolder> GetFolders(Guid jobId);
    void SetDestinationCount(long folderId, int count);

    /// <summary>Ajoute les messages encore inconnus (en attente) ; renvoie le nombre d'ajouts.</summary>
    int AddMessages(long folderId, IEnumerable<(RemoteMessage Message, string Key)> messages);
    IReadOnlyList<StoredMessage> GetMessages(long folderId, MessageStatus? status = null);
    void SetMessageStatus(long messageId, MessageStatus status, string? error = null);

    /// <summary>Même statut pour tout un lot, en une transaction.</summary>
    void SetMessagesStatus(IEnumerable<long> messageIds, MessageStatus status, string? error = null);
    FolderTotals GetTotals(long folderId);

    /// <summary>« Réessayer les échecs » : repasse les échecs en attente.</summary>
    int ResetFailed(Guid jobId);

    HashSet<string> GetDestinationKeys(Guid jobId, string destinationPath);
    void ReplaceDestinationKeys(Guid jobId, string destinationPath, IEnumerable<string> keys);
    void AddDestinationKey(Guid jobId, string destinationPath, string key);
    void AddDestinationKeys(Guid jobId, string destinationPath, IEnumerable<string> keys);
}

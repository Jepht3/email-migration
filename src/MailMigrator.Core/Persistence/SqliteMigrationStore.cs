using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MailMigrator.Core.Imap;
using MailMigrator.Core.Models;
using MailMigrator.Core.Reporting;
using Microsoft.Data.Sqlite;

namespace MailMigrator.Core.Persistence;

/// <summary>Magasin SQLite (mode WAL). Une seule connexion protégée par un verrou : sûr pour 5 tâches.</summary>
public sealed class SqliteMigrationStore : IMigrationStore, IDisposable
{
    private const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    private readonly SqliteConnection _db;
    private readonly object _gate = new();

    public SqliteMigrationStore(string databasePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        _db.Open();
        Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;");
        Migrate();
    }

    private void Migrate()
    {
        Execute("CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);");
        var version = Convert.ToInt32(Scalar("SELECT COALESCE(MAX(version), 0) FROM schema_version;"));
        if (version >= SchemaVersion) return;

        Execute("""
            CREATE TABLE jobs (
                id TEXT PRIMARY KEY,
                created_at TEXT NOT NULL,
                started_at TEXT,
                finished_at TEXT,
                status TEXT NOT NULL,
                color_index INTEGER NOT NULL,
                source_email TEXT NOT NULL,
                destination_email TEXT NOT NULL,
                definition_json TEXT NOT NULL,
                source_secret BLOB,
                destination_secret BLOB,
                elapsed_ms INTEGER NOT NULL DEFAULT 0,
                last_error TEXT
            );
            CREATE TABLE folders (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                job_id TEXT NOT NULL REFERENCES jobs(id) ON DELETE CASCADE,
                source_path TEXT NOT NULL,
                destination_path TEXT NOT NULL,
                role TEXT NOT NULL,
                uid_validity INTEGER NOT NULL,
                destination_count_after INTEGER,
                UNIQUE (job_id, source_path)
            );
            CREATE TABLE messages (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                folder_id INTEGER NOT NULL REFERENCES folders(id) ON DELETE CASCADE,
                uid INTEGER NOT NULL,
                dedup_key TEXT NOT NULL,
                message_id TEXT,
                internal_date TEXT NOT NULL,
                date TEXT,
                sender TEXT NOT NULL,
                subject TEXT NOT NULL,
                size INTEGER NOT NULL,
                flags INTEGER NOT NULL,
                status INTEGER NOT NULL DEFAULT 0,
                error TEXT,
                processed_at TEXT,
                UNIQUE (folder_id, uid)
            );
            CREATE INDEX ix_messages_status ON messages (folder_id, status);
            CREATE TABLE destination_keys (
                job_id TEXT NOT NULL REFERENCES jobs(id) ON DELETE CASCADE,
                destination_path TEXT NOT NULL,
                dedup_key TEXT NOT NULL,
                PRIMARY KEY (job_id, destination_path, dedup_key)
            ) WITHOUT ROWID;
            CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT);
            """);
        Execute("INSERT INTO schema_version (version) VALUES ($v);", ("$v", SchemaVersion));
    }

    // ---------- Tâches ----------

    public void CreateJob(MigrationJobDefinition definition, byte[]? sourceSecret, byte[]? destinationSecret) => Execute("""
        INSERT INTO jobs (id, created_at, status, color_index, source_email, destination_email, definition_json, source_secret, destination_secret)
        VALUES ($id, $now, $status, $color, $src, $dst, $json, $ss, $ds);
        """,
        ("$id", definition.Id.ToString()), ("$now", Now()), ("$status", JobStatus.Queued.ToString()),
        ("$color", definition.ColorIndex), ("$src", definition.SourceEmail), ("$dst", definition.DestinationEmail),
        ("$json", JsonSerializer.Serialize(definition, Json)), ("$ss", sourceSecret), ("$ds", destinationSecret));

    public StoredJob? GetJob(Guid jobId)
    {
        lock (_gate)
        {
            using var cmd = Command("""
                SELECT definition_json, status, created_at, started_at, finished_at, elapsed_ms, last_error,
                       source_secret IS NOT NULL OR destination_secret IS NOT NULL
                FROM jobs WHERE id = $id;
                """, ("$id", jobId.ToString()));
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return new StoredJob(
                JsonSerializer.Deserialize<MigrationJobDefinition>(r.GetString(0), Json)!,
                Enum.Parse<JobStatus>(r.GetString(1)),
                ParseDate(r.GetString(2)),
                r.IsDBNull(3) ? null : ParseDate(r.GetString(3)),
                r.IsDBNull(4) ? null : ParseDate(r.GetString(4)),
                TimeSpan.FromMilliseconds(r.GetInt64(5)),
                r.IsDBNull(6) ? null : r.GetString(6),
                r.GetBoolean(7));
        }
    }

    public void UpdateJobStatus(Guid jobId, JobStatus status, string? error = null) => Execute("""
        UPDATE jobs SET status = $status,
            last_error = COALESCE($error, CASE WHEN $finished = 1 THEN last_error ELSE NULL END),
            started_at = CASE WHEN $running = 1 THEN COALESCE(started_at, $now) ELSE started_at END,
            finished_at = CASE WHEN $finished = 1 THEN $now ELSE NULL END
        WHERE id = $id;
        """,
        ("$id", jobId.ToString()), ("$status", status.ToString()), ("$error", error), ("$now", Now()),
        ("$running", status.IsRunning() ? 1 : 0), ("$finished", status.IsFinished() ? 1 : 0));

    public void SaveElapsed(Guid jobId, TimeSpan elapsed) =>
        Execute("UPDATE jobs SET elapsed_ms = $ms WHERE id = $id;", ("$id", jobId.ToString()), ("$ms", (long)elapsed.TotalMilliseconds));

    public (byte[]? Source, byte[]? Destination) GetSecrets(Guid jobId)
    {
        lock (_gate)
        {
            using var cmd = Command("SELECT source_secret, destination_secret FROM jobs WHERE id = $id;", ("$id", jobId.ToString()));
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return (null, null);
            return (r.IsDBNull(0) ? null : (byte[])r[0], r.IsDBNull(1) ? null : (byte[])r[1]);
        }
    }

    public void ClearSecrets(Guid jobId) =>
        Execute("UPDATE jobs SET source_secret = NULL, destination_secret = NULL WHERE id = $id;", ("$id", jobId.ToString()));

    // ---------- Dossiers ----------

    public StoredFolder UpsertFolder(Guid jobId, string sourcePath, string destinationPath, SpecialFolder role, uint uidValidity)
    {
        long? id = null;
        lock (_gate)
        {
            using var tx = Begin();
            uint? knownValidity = null;
            using (var find = Command("SELECT id, uid_validity FROM folders WHERE job_id = $job AND source_path = $src;",
                       ("$job", jobId.ToString()), ("$src", sourcePath)))
            using (var r = find.ExecuteReader())
            {
                if (r.Read())
                {
                    id = r.GetInt64(0);
                    knownValidity = (uint)r.GetInt64(1);
                }
            }

            if (id is null)
            {
                using var insert = Command("""
                    INSERT INTO folders (job_id, source_path, destination_path, role, uid_validity)
                    VALUES ($job, $src, $dst, $role, $uv) RETURNING id;
                    """, ("$job", jobId.ToString()), ("$src", sourcePath), ("$dst", destinationPath), ("$role", role.ToString()), ("$uv", (long)uidValidity));
                id = (long)insert.ExecuteScalar()!;
            }
            else
            {
                // UIDVALIDITY différent : les UID source ne sont plus fiables, on réanalyse le dossier.
                // La déduplication par clé évite tout doublon côté destination.
                if (knownValidity != uidValidity)
                    using (var purge = Command("DELETE FROM messages WHERE folder_id = $id;", ("$id", id.Value))) purge.ExecuteNonQuery();
                using var update = Command("UPDATE folders SET destination_path = $dst, role = $role, uid_validity = $uv WHERE id = $id;",
                    ("$id", id.Value), ("$dst", destinationPath), ("$role", role.ToString()), ("$uv", (long)uidValidity));
                update.ExecuteNonQuery();
            }
            tx.Commit();
        }
        return GetFolders(jobId).First(f => f.Id == id);
    }

    public IReadOnlyList<StoredFolder> GetFolders(Guid jobId)
    {
        lock (_gate)
        {
            using var cmd = Command("""
                SELECT f.id, f.source_path, f.destination_path, f.role, f.uid_validity,
                       COUNT(m.id), COALESCE(SUM(m.size), 0), f.destination_count_after
                FROM folders f LEFT JOIN messages m ON m.folder_id = f.id
                WHERE f.job_id = $job GROUP BY f.id ORDER BY f.id;
                """, ("$job", jobId.ToString()));
            using var r = cmd.ExecuteReader();
            var list = new List<StoredFolder>();
            while (r.Read())
            {
                list.Add(new StoredFolder(r.GetInt64(0), r.GetString(1), r.GetString(2), Enum.Parse<SpecialFolder>(r.GetString(3)),
                    (uint)r.GetInt64(4), r.GetInt32(5), r.GetInt64(6), r.IsDBNull(7) ? null : r.GetInt32(7)));
            }
            return list;
        }
    }

    public void SetDestinationCount(long folderId, int count) =>
        Execute("UPDATE folders SET destination_count_after = $c WHERE id = $id;", ("$id", folderId), ("$c", count));

    // ---------- Messages ----------

    public int AddMessages(long folderId, IEnumerable<(RemoteMessage Message, string Key)> messages)
    {
        lock (_gate)
        {
            using var tx = Begin();
            using var cmd = Command("""
                INSERT OR IGNORE INTO messages (folder_id, uid, dedup_key, message_id, internal_date, date, sender, subject, size, flags)
                VALUES ($f, $uid, $key, $mid, $idate, $date, $sender, $subject, $size, $flags);
                """);
            var p = new Dictionary<string, SqliteParameter>();
            foreach (var name in new[] { "$f", "$uid", "$key", "$mid", "$idate", "$date", "$sender", "$subject", "$size", "$flags" })
                p[name] = cmd.Parameters.Add(name, SqliteType.Text);
            var inserted = 0;
            foreach (var (m, key) in messages)
            {
                p["$f"].Value = folderId;
                p["$uid"].Value = (long)m.Uid;
                p["$key"].Value = key;
                p["$mid"].Value = (object?)m.MessageId ?? DBNull.Value;
                p["$idate"].Value = m.InternalDate.ToString("O");
                p["$date"].Value = (object?)m.Date?.ToString("O") ?? DBNull.Value;
                p["$sender"].Value = m.Sender;
                p["$subject"].Value = m.Subject ?? "";
                p["$size"].Value = m.Size;
                p["$flags"].Value = (int)m.Flags;
                inserted += cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return inserted;
        }
    }

    public IReadOnlyList<StoredMessage> GetMessages(long folderId, MessageStatus? status = null)
    {
        lock (_gate)
        {
            using var cmd = Command($"""
                SELECT id, folder_id, uid, dedup_key, message_id, internal_date, date, sender, subject, size, flags, status, error
                FROM messages WHERE folder_id = $f {(status is null ? "" : "AND status = $s")} ORDER BY uid;
                """, ("$f", folderId), ("$s", (int?)status));
            return ReadMessages(cmd);
        }
    }

    public void SetMessageStatus(long messageId, MessageStatus status, string? error = null) =>
        Execute("UPDATE messages SET status = $s, error = $e, processed_at = $now WHERE id = $id;",
            ("$id", messageId), ("$s", (int)status), ("$e", error), ("$now", Now()));

    public void SetMessagesStatus(IEnumerable<long> messageIds, MessageStatus status, string? error = null)
    {
        lock (_gate)
        {
            using var tx = Begin();
            using var cmd = Command("UPDATE messages SET status = $s, error = $e, processed_at = $now WHERE id = $id;",
                ("$s", (int)status), ("$e", error), ("$now", Now()));
            var id = cmd.Parameters.Add("$id", SqliteType.Integer);
            foreach (var messageId in messageIds)
            {
                id.Value = messageId;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void AddDestinationKeys(Guid jobId, string destinationPath, IEnumerable<string> keys)
    {
        lock (_gate)
        {
            using var tx = Begin();
            using var cmd = Command("INSERT OR IGNORE INTO destination_keys VALUES ($job, $p, $k);", ("$job", jobId.ToString()), ("$p", destinationPath));
            var k = cmd.Parameters.Add("$k", SqliteType.Text);
            foreach (var key in keys)
            {
                k.Value = key;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public FolderTotals GetTotals(long folderId)
    {
        lock (_gate)
        {
            using var cmd = Command("""
                SELECT SUM(status = 0), SUM(status = 1), SUM(status = 2), SUM(status = 3),
                       COALESCE(SUM(CASE WHEN status IN (1, 2) THEN size ELSE 0 END), 0)
                FROM messages WHERE folder_id = $f;
                """, ("$f", folderId));
            using var r = cmd.ExecuteReader();
            r.Read();
            int Int(int i) => r.IsDBNull(i) ? 0 : r.GetInt32(i);
            return new FolderTotals(Int(0), Int(1), Int(2), Int(3), r.GetInt64(4));
        }
    }

    public int ResetFailed(Guid jobId)
    {
        lock (_gate)
        {
            using var cmd = Command("""
                UPDATE messages SET status = 0, error = NULL
                WHERE status = 3 AND folder_id IN (SELECT id FROM folders WHERE job_id = $job);
                """, ("$job", jobId.ToString()));
            return cmd.ExecuteNonQuery();
        }
    }

    // ---------- Index de déduplication côté destination ----------

    public HashSet<string> GetDestinationKeys(Guid jobId, string destinationPath)
    {
        lock (_gate)
        {
            using var cmd = Command("SELECT dedup_key FROM destination_keys WHERE job_id = $job AND destination_path = $p;",
                ("$job", jobId.ToString()), ("$p", destinationPath));
            using var r = cmd.ExecuteReader();
            var set = new HashSet<string>(StringComparer.Ordinal);
            while (r.Read()) set.Add(r.GetString(0));
            return set;
        }
    }

    public void ReplaceDestinationKeys(Guid jobId, string destinationPath, IEnumerable<string> keys)
    {
        lock (_gate)
        {
            using var tx = Begin();
            using (var delete = Command("DELETE FROM destination_keys WHERE job_id = $job AND destination_path = $p;",
                       ("$job", jobId.ToString()), ("$p", destinationPath)))
                delete.ExecuteNonQuery();
            using var insert = Command("INSERT OR IGNORE INTO destination_keys VALUES ($job, $p, $k);", ("$job", jobId.ToString()), ("$p", destinationPath));
            var k = insert.Parameters.Add("$k", SqliteType.Text);
            foreach (var key in keys)
            {
                k.Value = key;
                insert.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void AddDestinationKey(Guid jobId, string destinationPath, string key) =>
        Execute("INSERT OR IGNORE INTO destination_keys VALUES ($job, $p, $k);", ("$job", jobId.ToString()), ("$p", destinationPath), ("$k", key));

    // ---------- Historique et rapports ----------

    public IReadOnlyList<MigrationHistoryEntry> GetAll()
    {
        lock (_gate)
        {
            using var cmd = Command($"{HistorySelect} GROUP BY j.id ORDER BY COALESCE(j.started_at, j.created_at) DESC;");
            return ReadHistory(cmd);
        }
    }

    public JobReport? GetReport(Guid id)
    {
        MigrationHistoryEntry? summary;
        lock (_gate)
        {
            using var cmd = Command($"{HistorySelect} WHERE j.id = $id GROUP BY j.id;", ("$id", id.ToString()));
            summary = ReadHistory(cmd).FirstOrDefault();
        }
        if (summary is null) return null;

        var folders = new List<FolderReport>();
        var lines = new List<MessageReportLine>();
        foreach (var f in GetFolders(id))
        {
            var t = GetTotals(f.Id);
            folders.Add(new FolderReport(f.SourcePath, f.DestinationPath, f.Role, f.SourceCount, t.Copied, t.Skipped, t.Failed,
                t.BytesProcessed, f.DestinationCountAfter ?? t.Copied + t.Skipped));
            lines.AddRange(GetMessages(f.Id).Select(m => new MessageReportLine(f.DestinationPath, m.Date ?? m.InternalDate, m.Sender, m.Subject, m.Size, m.Status, m.Error)));
        }
        return new JobReport(summary, folders, lines.OrderByDescending(l => l.Date).ToList());
    }

    private const string HistorySelect = """
        SELECT j.id, j.color_index, j.source_email, j.destination_email, COALESCE(j.started_at, j.created_at), j.elapsed_ms, j.status,
               COUNT(m.id), COALESCE(SUM(m.status = 1), 0), COALESCE(SUM(m.status = 2), 0), COALESCE(SUM(m.status = 3), 0),
               COALESCE(SUM(CASE WHEN m.status = 1 THEN m.size ELSE 0 END), 0)
        FROM jobs j LEFT JOIN folders f ON f.job_id = j.id LEFT JOIN messages m ON m.folder_id = f.id
        """;

    private static List<MigrationHistoryEntry> ReadHistory(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var list = new List<MigrationHistoryEntry>();
        while (r.Read())
        {
            list.Add(new MigrationHistoryEntry
            {
                Id = Guid.Parse(r.GetString(0)),
                ColorIndex = r.GetInt32(1),
                SourceEmail = r.GetString(2),
                DestinationEmail = r.GetString(3),
                StartedAt = ParseDate(r.GetString(4)),
                Duration = TimeSpan.FromMilliseconds(r.GetInt64(5)),
                Status = Enum.Parse<JobStatus>(r.GetString(6)),
                SourceMessages = r.GetInt32(7),
                Copied = r.GetInt32(8),
                Skipped = r.GetInt32(9),
                Failed = r.GetInt32(10),
                Bytes = r.GetInt64(11),
            });
        }
        return list;
    }

    // ---------- Outils ----------

    private static List<StoredMessage> ReadMessages(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var list = new List<StoredMessage>();
        while (r.Read())
        {
            list.Add(new StoredMessage(r.GetInt64(0), r.GetInt64(1), (uint)r.GetInt64(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), ParseDate(r.GetString(5)), r.IsDBNull(6) ? null : ParseDate(r.GetString(6)),
                r.GetString(7), r.GetString(8), r.GetInt64(9), (MailFlags)r.GetInt32(10), (MessageStatus)r.GetInt32(11),
                r.IsDBNull(12) ? null : r.GetString(12)));
        }
        return list;
    }

    private static string Now() => DateTimeOffset.Now.ToString("O");

    private static DateTimeOffset ParseDate(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private SqliteTransaction? _transaction;

    /// <summary>Transaction courante, rattachée automatiquement aux commandes (appel sous verrou).</summary>
    private Transaction Begin()
    {
        _transaction = _db.BeginTransaction();
        return new Transaction(this);
    }

    private sealed class Transaction(SqliteMigrationStore store) : IDisposable
    {
        public void Commit() => store._transaction!.Commit();

        public void Dispose()
        {
            store._transaction?.Dispose();
            store._transaction = null;
        }
    }

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var cmd = _db.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        lock (_gate)
        {
            using var cmd = Command(sql, parameters);
            cmd.ExecuteNonQuery();
        }
    }

    private object? Scalar(string sql)
    {
        lock (_gate)
        {
            using var cmd = Command(sql);
            return cmd.ExecuteScalar();
        }
    }

    public void Dispose() => _db.Dispose();
}

using System.Globalization;
using System.Security.Cryptography;
using MailMigrator.Core.Abstractions;
using MailMigrator.Core.Imap;
using MailMigrator.Core.Models;
using Microsoft.Data.Sqlite;
using MimeKit;
using MimeKit.Utils;

namespace MailMigrator.Core.Archive;

/// <summary>Résumé d'une sauvegarde locale (écran « Sauvegardes locales »).</summary>
public sealed record LocalArchiveSummary(
    string Path,
    string Label,
    bool Encrypted,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Folders,
    int Messages,
    long Bytes);

/// <summary>
/// Sauvegarde d'une boîte sur ce PC, utilisable comme destination (sauvegarde) ou comme source
/// (restauration) par le même moteur que les migrations IMAP.
/// Disposition : <c>archive.db</c> (index SQLite : dossiers, rôles, drapeaux, dates) +
/// <c>Mail\&lt;dossier&gt;\0000001.eml</c> (lisible) ou <c>.eml.lwb</c> (chiffré AES-256).
/// </summary>
public sealed class LocalArchiveMailbox(ImapEndpoint endpoint, ISecretProtector protector) : IMailboxConnection, ICapacityAware
{
    public const string IndexFileName = "archive.db";
    private const string MailFolder = "Mail";
    private const long InMemoryLimit = 4 * 1024 * 1024;

    private readonly string _root = Path.GetFullPath(endpoint.LocalArchivePath ?? throw new ArgumentException("Chemin de sauvegarde absent."));
    private SqliteConnection? _db;
    private ArchiveCrypto? _crypto;
    private bool _connected;

    public bool IsConnected => _connected;
    public string NamespacePrefix => "";
    public char DefaultSeparator => '/';

    private string IndexPath => Path.Combine(_root, IndexFileName);

    public static bool Exists(string path) => File.Exists(Path.Combine(path, IndexFileName));

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        _connected = true;
        if (_db is null && Exists(_root)) Open(create: false);
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ Ouverture / création

    private void Open(bool create)
    {
        if (create) Directory.CreateDirectory(_root);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = IndexPath, Pooling = false }.ToString());
        db.Open();
        Execute(db, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        if (create)
        {
            Execute(db, """
                CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT);
                CREATE TABLE IF NOT EXISTS folders (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, path TEXT NOT NULL UNIQUE, role TEXT NOT NULL,
                    directory TEXT NOT NULL, uid_validity INTEGER NOT NULL, next_uid INTEGER NOT NULL DEFAULT 1);
                CREATE TABLE IF NOT EXISTS messages (
                    folder_id INTEGER NOT NULL, uid INTEGER NOT NULL, message_id TEXT, internal_date TEXT NOT NULL, date TEXT,
                    from_name TEXT, from_address TEXT, subject TEXT, size INTEGER NOT NULL, flags INTEGER NOT NULL, file TEXT NOT NULL,
                    PRIMARY KEY (folder_id, uid));
                CREATE INDEX IF NOT EXISTS ix_messages_mid ON messages (folder_id, message_id);
                """);
            var now = DateTimeOffset.Now.ToString("O");
            SetMeta(db, "version", "1");
            SetMeta(db, "label", endpoint.UserName);
            SetMeta(db, "created_at", now);
            SetMeta(db, "updated_at", now);
            if (endpoint.ArchiveEncrypted)
            {
                var key = ArchiveCrypto.NewKey();
                SetMeta(db, "encryption", "aes256-cbc-hmac-sha256");
                SetMeta(db, "key", Convert.ToBase64String(protector.Protect(Convert.ToBase64String(key))));
                _crypto = new ArchiveCrypto(key);
            }
        }
        else if (GetMeta(db, "key") is { } protectedKey)
        {
            try
            {
                _crypto = new ArchiveCrypto(Convert.FromBase64String(protector.Unprotect(Convert.FromBase64String(protectedKey))));
            }
            catch (CryptographicException ex)
            {
                db.Dispose();
                throw new ArchiveException("Cette sauvegarde est chiffrée pour une autre session Windows ou un autre PC : "
                                           + "elle ne peut être ouverte que depuis le compte Windows qui l'a créée.", ex);
            }
        }
        _db = db;
    }

    private SqliteConnection Db => _db ?? throw new InvalidOperationException("Sauvegarde non ouverte.");

    private SqliteConnection EnsureCreated()
    {
        if (_db is null) Open(create: !Exists(_root));
        return Db;
    }

    // ------------------------------------------------------------------ Lecture

    public Task<IReadOnlyList<RemoteFolder>> ListFoldersAsync(CancellationToken cancellationToken)
    {
        if (_db is null) return Task.FromResult<IReadOnlyList<RemoteFolder>>([]);
        var list = new List<RemoteFolder>();
        using var cmd = Command(Db, "SELECT path, role FROM folders ORDER BY id;");
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new RemoteFolder(r.GetString(0), '/', Enum.Parse<SpecialFolder>(r.GetString(1))));
        return Task.FromResult<IReadOnlyList<RemoteFolder>>(list);
    }

    public Task<FolderState> GetFolderStateAsync(string path, CancellationToken cancellationToken)
    {
        using var cmd = Command(Db, "SELECT f.uid_validity, (SELECT COUNT(*) FROM messages m WHERE m.folder_id = f.id) FROM folders f WHERE f.path = $p;", ("$p", path));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new ArchiveException($"Dossier « {path} » absent de la sauvegarde.");
        return Task.FromResult(new FolderState((uint)r.GetInt64(0), r.GetInt32(1)));
    }

    public Task<IReadOnlyList<RemoteMessage>> ListMessagesAsync(string path, CancellationToken cancellationToken)
    {
        var list = new List<RemoteMessage>();
        if (_db is null) return Task.FromResult<IReadOnlyList<RemoteMessage>>(list);
        using var cmd = Command(Db, """
            SELECT m.uid, m.message_id, m.internal_date, m.date, m.from_name, m.from_address, m.subject, m.size, m.flags
            FROM messages m JOIN folders f ON f.id = m.folder_id WHERE f.path = $p ORDER BY m.uid;
            """, ("$p", path));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new RemoteMessage((uint)r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1), ParseDate(r.GetString(2)),
                r.IsDBNull(3) ? null : ParseDate(r.GetString(3)), r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6), r.GetInt64(7), (MailFlags)r.GetInt32(8)));
        }
        return Task.FromResult<IReadOnlyList<RemoteMessage>>(list);
    }

    public async Task<Stream> DownloadAsync(string path, uint uid, long size, CancellationToken cancellationToken)
    {
        using var cmd = Command(Db, "SELECT m.file FROM messages m JOIN folders f ON f.id = m.folder_id WHERE f.path = $p AND m.uid = $u;", ("$p", path), ("$u", (long)uid));
        var relative = cmd.ExecuteScalar() as string ?? throw new ArchiveException($"Message {uid} absent de la sauvegarde.");
        var file = Path.Combine(_root, relative);
        if (!File.Exists(file)) throw new ArchiveException($"Fichier manquant dans la sauvegarde : {relative}");
        if (_crypto is null) return new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);

        Stream output = size <= InMemoryLimit
            ? new MemoryStream()
            : new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        try
        {
            await _crypto.DecryptFileAsync(file, output, cancellationToken);
            output.Position = 0;
            return output;
        }
        catch
        {
            await output.DisposeAsync();
            throw;
        }
    }

    public Task<bool> ContainsMessageIdAsync(string path, string messageId, CancellationToken cancellationToken)
    {
        if (_db is null) return Task.FromResult(false);
        using var cmd = Command(Db, """
            SELECT EXISTS (SELECT 1 FROM messages m JOIN folders f ON f.id = m.folder_id
                           WHERE f.path = $p AND lower(trim(m.message_id, '<> ')) = lower(trim($mid, '<> ')));
            """, ("$p", path), ("$mid", messageId));
        return Task.FromResult(Convert.ToInt64(cmd.ExecuteScalar()) == 1);
    }

    // ------------------------------------------------------------------ Écriture

    public Task CreateFolderAsync(string path, SpecialFolder role, CancellationToken cancellationToken)
    {
        var db = EnsureCreated();
        Execute(db, "INSERT OR IGNORE INTO folders (path, role, directory, uid_validity) VALUES ($p, $r, $d, $v);",
            ("$p", path), ("$r", role.ToString()), ("$d", Path.Combine(MailFolder, SafeDirectory(path))), ("$v", (long)RandomNumberGenerator.GetInt32(1, int.MaxValue)));
        // Rôle découvert après coup (dossier créé avant d'être reconnu) : on le complète.
        if (role != SpecialFolder.None) Execute(db, "UPDATE folders SET role = $r WHERE path = $p AND role = 'None';", ("$p", path), ("$r", role.ToString()));
        return Task.CompletedTask;
    }

    public async Task AppendAsync(string path, Stream message, MailFlags flags, DateTimeOffset internalDate, CancellationToken cancellationToken)
    {
        var db = EnsureCreated();
        await CreateFolderAsync(path, SpecialFolder.None, cancellationToken);

        // En-têtes seulement (Message-ID, expéditeur, sujet, date) pour l'index et la déduplication.
        var start = message.Position;
        var headers = await HeaderList.LoadAsync(message, cancellationToken);
        message.Position = start;
        var from = InternetAddressList.TryParse(headers[HeaderId.From] ?? "", out var list) ? list.Mailboxes.FirstOrDefault() : null;
        DateTimeOffset? date = DateUtils.TryParse(headers[HeaderId.Date] ?? "", out var parsed) ? parsed : null;

        long folderId, uid;
        string directory;
        using (var cmd = Command(db, "SELECT id, next_uid, directory FROM folders WHERE path = $p;", ("$p", path)))
        using (var r = cmd.ExecuteReader())
        {
            r.Read();
            (folderId, uid, directory) = (r.GetInt64(0), r.GetInt64(1), r.GetString(2));
        }

        var relative = Path.Combine(directory, $"{uid:D7}.eml{(_crypto is null ? "" : ".lwb")}");
        var target = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + ".tmp";
        if (File.Exists(temp)) File.Delete(temp);
        if (_crypto is null)
        {
            await using var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await message.CopyToAsync(file, cancellationToken);
        }
        else
        {
            await _crypto.EncryptToFileAsync(message, temp, cancellationToken);
        }
        File.Move(temp, target, overwrite: true);

        // Index mis à jour seulement une fois le fichier complet écrit : un arrêt brutal ne laisse pas d'entrée orpheline.
        using var tx = db.BeginTransaction();
        Execute(db, tx, """
            INSERT INTO messages (folder_id, uid, message_id, internal_date, date, from_name, from_address, subject, size, flags, file)
            VALUES ($f, $u, $mid, $idate, $date, $fn, $fa, $s, $size, $flags, $file);
            UPDATE folders SET next_uid = next_uid + 1 WHERE id = $f;
            """,
            ("$f", folderId), ("$u", uid), ("$mid", headers[HeaderId.MessageId]), ("$idate", internalDate.ToString("O")),
            ("$date", date?.ToString("O")), ("$fn", from?.Name), ("$fa", from?.Address), ("$s", headers[HeaderId.Subject]),
            ("$size", message.Length - start), ("$flags", (int)flags), ("$file", relative));
        tx.Commit();
        SetMeta(db, "updated_at", DateTimeOffset.Now.ToString("O"));
    }

    /// <summary>Refuse de démarrer s'il n'y a pas la place (taille + 10 % + 100 Mo de marge).</summary>
    public Task EnsureCapacityAsync(long bytes, CancellationToken cancellationToken)
    {
        var root = Path.GetPathRoot(_root);
        if (string.IsNullOrEmpty(root)) return Task.CompletedTask;
        var free = new DriveInfo(root).AvailableFreeSpace;
        var needed = (long)(bytes * 1.1) + 100L * 1024 * 1024;
        if (free < needed)
            throw new ArchiveException($"Espace disque insuffisant sur {root} : {Formatting.Format.Bytes(needed)} nécessaires, {Formatting.Format.Bytes(free)} libres. "
                                       + "Choisissez un autre disque ou libérez de la place.");
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ Résumé

    public static LocalArchiveSummary? ReadSummary(string path)
    {
        if (!Exists(path)) return null;
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(path, IndexFileName), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        using var cmd = Command(db, "SELECT (SELECT COUNT(*) FROM folders), COUNT(*), COALESCE(SUM(size), 0) FROM messages;");
        using var r = cmd.ExecuteReader();
        r.Read();
        return new LocalArchiveSummary(Path.GetFullPath(path), GetMeta(db, "label") ?? "", GetMeta(db, "key") is not null,
            ParseDate(GetMeta(db, "created_at") ?? DateTimeOffset.MinValue.ToString("O")), ParseDate(GetMeta(db, "updated_at") ?? DateTimeOffset.MinValue.ToString("O")),
            r.GetInt32(0), r.GetInt32(1), r.GetInt64(2));
    }

    // ------------------------------------------------------------------ Outils

    /// <summary>Nom de dossier Windows sûr pour chaque niveau (« Clients/Dupont » → « Clients\Dupont »).</summary>
    internal static string SafeDirectory(string path)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string[] reserved = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "LPT1", "LPT2", "LPT3"];
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(segment =>
        {
            var clean = new string(segment.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
            if (clean.Length == 0) clean = "_";
            if (reserved.Contains(clean.ToUpperInvariant())) clean = "_" + clean;
            return clean.Length > 80 ? clean[..80] : clean;
        });
        return Path.Combine(parts.DefaultIfEmpty("_").ToArray());
    }

    private static DateTimeOffset ParseDate(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string? GetMeta(SqliteConnection db, string key)
    {
        using var cmd = Command(db, "SELECT value FROM meta WHERE key = $k;", ("$k", key));
        return cmd.ExecuteScalar() as string;
    }

    private static void SetMeta(SqliteConnection db, string key, string? value) =>
        Execute(db, "INSERT INTO meta (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value;", ("$k", key), ("$v", value));

    private static SqliteCommand Command(SqliteConnection db, string sql, params (string, object?)[] parameters)
    {
        var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private static void Execute(SqliteConnection db, string sql, params (string, object?)[] parameters)
    {
        using var cmd = Command(db, sql, parameters);
        cmd.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection db, SqliteTransaction tx, string sql, params (string, object?)[] parameters)
    {
        using var cmd = Command(db, sql, parameters);
        cmd.Transaction = tx;
        cmd.ExecuteNonQuery();
    }

    public ValueTask DisposeAsync()
    {
        _db?.Dispose();
        _db = null;
        _connected = false;
        return ValueTask.CompletedTask;
    }
}

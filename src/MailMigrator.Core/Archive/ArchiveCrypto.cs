using System.Security.Cryptography;
using System.Text;

namespace MailMigrator.Core.Archive;

/// <summary>
/// Chiffrement des messages d'une sauvegarde locale, en flux (aucun message entier en mémoire) :
/// AES-256-CBC puis HMAC-SHA256 sur l'ensemble (« encrypt-then-MAC »).
/// Format d'un fichier : « LWB1 » + IV (16) + données chiffrées + étiquette HMAC (32).
/// </summary>
public sealed class ArchiveCrypto
{
    private static readonly byte[] Magic = "LWB1"u8.ToArray();
    private const int IvLength = 16;
    private const int TagLength = 32;

    private readonly byte[] _encryptionKey;
    private readonly byte[] _macKey;

    /// <param name="masterKey">Clé maîtresse de 32 octets propre à la sauvegarde.</param>
    public ArchiveCrypto(byte[] masterKey)
    {
        if (masterKey.Length != 32) throw new ArgumentException("Clé de 32 octets attendue.", nameof(masterKey));
        _encryptionKey = HMACSHA256.HashData(masterKey, Encoding.ASCII.GetBytes("LWB/encryption"));
        _macKey = HMACSHA256.HashData(masterKey, Encoding.ASCII.GetBytes("LWB/authentication"));
    }

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

    public async Task EncryptToFileAsync(Stream input, string path, CancellationToken ct)
    {
        var iv = RandomNumberGenerator.GetBytes(IvLength);
        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await file.WriteAsync(Magic, ct);
            await file.WriteAsync(iv, ct);
            using var aes = Aes.Create();
            aes.Key = _encryptionKey;
            aes.IV = iv;
            await using var crypto = new CryptoStream(file, aes.CreateEncryptor(), CryptoStreamMode.Write, leaveOpen: true);
            await input.CopyToAsync(crypto, ct);
            await crypto.FlushFinalBlockAsync(ct);
        }

        // Étiquette d'authentification calculée sur tout le contenu écrit.
        byte[] tag;
        await using (var read = File.OpenRead(path)) tag = await HMACSHA256.HashDataAsync(_macKey, read, ct);
        await using var append = new FileStream(path, FileMode.Append, FileAccess.Write);
        await append.WriteAsync(tag, ct);
    }

    /// <summary>Vérifie l'intégrité puis déchiffre vers <paramref name="output"/>.</summary>
    public async Task DecryptFileAsync(string path, Stream output, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var bodyLength = file.Length - TagLength;
        if (bodyLength < Magic.Length + IvLength) throw Corrupted(path);

        var header = new byte[Magic.Length + IvLength];
        await file.ReadExactlyAsync(header, ct);
        if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic)) throw Corrupted(path);

        // 1. Intégrité : aucune donnée n'est déchiffrée si le fichier a été modifié.
        file.Position = 0;
        byte[] expected;
        using (var hmac = new HMACSHA256(_macKey))
        {
            var buffer = new byte[81920];
            var remaining = bodyLength;
            while (remaining > 0)
            {
                var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
                if (read == 0) throw Corrupted(path);
                hmac.TransformBlock(buffer, 0, read, null, 0);
                remaining -= read;
            }
            hmac.TransformFinalBlock([], 0, 0);
            expected = hmac.Hash!;
        }
        var tag = new byte[TagLength];
        await file.ReadExactlyAsync(tag, ct);
        if (!CryptographicOperations.FixedTimeEquals(tag, expected)) throw Corrupted(path);

        // 2. Déchiffrement en flux.
        file.Position = header.Length;
        using var aes = Aes.Create();
        aes.Key = _encryptionKey;
        aes.IV = header[Magic.Length..];
        await using var crypto = new CryptoStream(new BoundedStream(file, bodyLength - header.Length), aes.CreateDecryptor(), CryptoStreamMode.Read);
        await crypto.CopyToAsync(output, ct);
    }

    private static ArchiveException Corrupted(string path) =>
        new($"Fichier de sauvegarde endommagé ou modifié : {Path.GetFileName(path)}");

    /// <summary>Lecture limitée à une portion d'un flux (le contenu chiffré, sans l'étiquette).</summary>
    private sealed class BoundedStream(Stream inner, long size) : Stream
    {
        private readonly long _size = size;
        private long _remaining = size;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _size;
        public override long Position { get => _size - _remaining; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0) return 0;
            var read = inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
            _remaining -= read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_remaining <= 0) return 0;
            var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], ct);
            _remaining -= read;
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>Erreur propre aux sauvegardes locales, avec un message français affichable tel quel.</summary>
public sealed class ArchiveException(string message, Exception? inner = null) : Exception(message, inner);

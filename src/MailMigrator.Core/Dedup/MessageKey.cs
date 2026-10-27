using System.Security.Cryptography;
using System.Text;
using MailMigrator.Core.Imap;

namespace MailMigrator.Core.Dedup;

/// <summary>
/// Clé de déduplication d'un message : le Message-ID normalisé, ou à défaut une empreinte
/// date + expéditeur + sujet + taille. Deux messages de même clé ne sont jamais copiés deux fois.
/// </summary>
public static class MessageKey
{
    public static string For(RemoteMessage message) =>
        Compute(message.MessageId, message.Date ?? message.InternalDate, message.FromAddress, message.Subject, message.Size);

    public static string Compute(string? messageId, DateTimeOffset? date, string? fromAddress, string? subject, long size)
    {
        var id = NormalizeMessageId(messageId);
        if (id.Length > 0) return "mid:" + id;

        var material = string.Join('|',
            date?.ToUniversalTime().ToString("yyyyMMddHHmmss") ?? "",
            (fromAddress ?? "").Trim().ToLowerInvariant(),
            NormalizeSubject(subject),
            size.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return "h:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..32].ToLowerInvariant();
    }

    /// <summary>« &lt;ABC@Host&gt; » → « abc@host ».</summary>
    public static string NormalizeMessageId(string? messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId)) return "";
        return messageId.Trim().Trim('<', '>').Trim().ToLowerInvariant();
    }

    /// <summary>Espaces multiples réduits, casse ignorée (les préfixes Re:/Fwd: sont conservés).</summary>
    public static string NormalizeSubject(string? subject) =>
        string.Join(' ', (subject ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
}

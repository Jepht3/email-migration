using MailMigrator.Core.Imap;
using MailMigrator.Core.Models;

namespace MailMigrator.Core.Folders;

/// <summary>Une boîte vue par le mappeur : ses dossiers et sa convention de nommage.</summary>
public sealed record MailboxLayout(IReadOnlyList<RemoteFolder> Folders, string NamespacePrefix, char Separator);

/// <summary>
/// Associe chaque dossier source à un dossier destination :
/// 1. choix manuel ; 2. SPECIAL-USE des deux côtés ; 3. nom reconnu (Sent / Envoyés / Sent Items…) ;
/// 4. même chemin ; 5. sinon le dossier sera créé, en convertissant le séparateur et l'espace de noms.
/// </summary>
public static class FolderMapper
{
    private static readonly Dictionary<SpecialFolder, string[]> KnownNames = new()
    {
        [SpecialFolder.Sent] = ["Sent", "Sent Items", "Sent Messages", "Sent Mail", "Envoyés", "Éléments envoyés", "Messages envoyés", "Envoyes"],
        [SpecialFolder.Drafts] = ["Drafts", "Draft", "Brouillons", "Brouillon"],
        [SpecialFolder.Trash] = ["Trash", "Deleted Items", "Deleted Messages", "Bin", "Corbeille", "Éléments supprimés", "Elements supprimes"],
        [SpecialFolder.Junk] = ["Junk", "Spam", "Junk E-mail", "Junk Email", "Bulk Mail", "Courrier indésirable", "Indésirables", "Pourriel"],
        [SpecialFolder.Archive] = ["Archive", "Archives"],
    };

    public static IReadOnlyList<FolderMapping> Map(MailboxLayout source, MailboxLayout destination, MigrationOptions options, IReadOnlyDictionary<string, int>? counts = null)
    {
        var result = new List<FolderMapping>();
        foreach (var folder in source.Folders.Where(f => f.CanSelect))
        {
            var role = RoleOf(folder);
            var relative = Relative(folder.Path, source);
            var count = counts is not null && counts.TryGetValue(folder.Path, out var c) ? c : 0;
            var (destination_, origin) = Resolve(folder, role, relative, destination, options);
            var included = !folder.IsVirtual
                           && !(role == SpecialFolder.Trash && !options.IncludeTrash)
                           && !(role == SpecialFolder.Junk && !options.IncludeJunk)
                           && !IsExcluded(relative, options.ExcludedFolders);
            result.Add(new FolderMapping(folder.Path, destination_, role, origin, count, included));
        }
        return result;
    }

    private static (string Path, MappingOrigin Origin) Resolve(RemoteFolder folder, SpecialFolder role, string relative, MailboxLayout dest, MigrationOptions options)
    {
        if (options.FolderOverrides.TryGetValue(folder.Path, out var manual))
            return (manual, MappingOrigin.Manual);

        if (role == SpecialFolder.Inbox)
            return (Inbox(dest), MappingOrigin.SpecialUse);

        if (options.AutoMapFolders && role != SpecialFolder.None)
        {
            if (folder.Role != SpecialFolder.None && dest.Folders.FirstOrDefault(d => d.Role == role) is { } byAttribute)
                return (byAttribute.Path, MappingOrigin.SpecialUse);
            if (dest.Folders.FirstOrDefault(d => d.CanSelect && RoleOf(d) == role) is { } byName)
                return (byName.Path, MappingOrigin.KnownName);
        }

        // Même chemin relatif, avec le séparateur et l'espace de noms de la destination.
        var target = Absolute(relative, dest);
        var exists = dest.Folders.Any(d => d.Path.Equals(target, StringComparison.Ordinal));
        return (target, exists ? MappingOrigin.SamePath : MappingOrigin.Created);
    }

    /// <summary>Rôle annoncé par le serveur, sinon déduit du dernier segment du nom.</summary>
    public static SpecialFolder RoleOf(RemoteFolder folder)
    {
        if (folder.Role != SpecialFolder.None) return folder.Role;
        if (folder.Path.Equals("INBOX", StringComparison.OrdinalIgnoreCase)) return SpecialFolder.Inbox;
        var name = folder.Path.Split(folder.Separator).Last();
        foreach (var (role, names) in KnownNames)
            if (names.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase))) return role;
        return SpecialFolder.None;
    }

    /// <summary>Chemin relatif normalisé avec « / » (sans l'espace de noms, ex. « INBOX. »).</summary>
    public static string Relative(string path, MailboxLayout layout)
    {
        if (layout.NamespacePrefix.Length > 0 && path.StartsWith(layout.NamespacePrefix, StringComparison.Ordinal))
            path = path[layout.NamespacePrefix.Length..];
        return layout.Separator == '/' ? path : path.Replace('/', '∕').Replace(layout.Separator, '/');
    }

    public static string Absolute(string relative, MailboxLayout layout)
    {
        // « ∕ » marque un « / » présent dans un nom source : il reste dans le nom si possible.
        var converted = layout.Separator == '/'
            ? relative.Replace('∕', '_')
            : relative.Replace(layout.Separator, '_').Replace('/', layout.Separator).Replace('∕', '/');
        return layout.NamespacePrefix + converted;
    }

    private static string Inbox(MailboxLayout dest) =>
        dest.Folders.FirstOrDefault(f => f.Path.Equals("INBOX", StringComparison.OrdinalIgnoreCase))?.Path ?? "INBOX";

    private static bool IsExcluded(string relative, IReadOnlyList<string> excluded) => excluded.Any(e =>
    {
        var x = e.Trim().Trim('/').Replace('\\', '/');
        return x.Length > 0 && (relative.Equals(x, StringComparison.OrdinalIgnoreCase)
                                || relative.StartsWith(x + "/", StringComparison.OrdinalIgnoreCase));
    });
}

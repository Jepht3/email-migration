using MailMigrator.Core.Models;

namespace MailMigrator.Core.Folders;

/// <summary>Comment une correspondance de dossier a été établie.</summary>
public enum MappingOrigin
{
    /// <summary>Attribut SPECIAL-USE annoncé par les deux serveurs.</summary>
    SpecialUse,
    /// <summary>Nom reconnu (Sent / Envoyés / Sent Items…).</summary>
    KnownName,
    /// <summary>Même chemin des deux côtés.</summary>
    SamePath,
    /// <summary>Dossier absent de la destination : il sera créé.</summary>
    Created,
    /// <summary>Choisi par l'utilisateur.</summary>
    Manual,
}

public sealed record FolderMapping(
    string SourcePath,
    string DestinationPath,
    SpecialFolder Role,
    MappingOrigin Origin,
    int MessageCount,
    bool Included = true);

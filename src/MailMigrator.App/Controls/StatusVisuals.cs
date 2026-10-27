using MailMigrator.Core.Models;
using MailMigrator.Core.Progress;
using Wpf.Ui.Controls;

namespace MailMigrator.App.Controls;

/// <summary>
/// Correspondance état → libellé + icône + couleur. L'information n'est jamais portée
/// par la couleur seule : chaque état a aussi une icône et un texte.
/// </summary>
public static class StatusVisuals
{
    public sealed record Visual(string Text, SymbolRegular Symbol, string BrushKey, string SoftBrushKey);

    public static Visual For(JobStatus status) => status switch
    {
        JobStatus.Queued => new("En file d'attente", SymbolRegular.Clock24, "Brush.Status.Paused", "Brush.Status.PausedSoft"),
        JobStatus.Connecting => new("Connexion", SymbolRegular.PlugConnected24, "Brush.Status.Info", "Brush.Status.InfoSoft"),
        JobStatus.Analyzing => new("Analyse", SymbolRegular.Search24, "Brush.Status.Info", "Brush.Status.InfoSoft"),
        JobStatus.Copying => new("Copie", SymbolRegular.ArrowSync24, "Brush.Accent", "Brush.AccentSoft"),
        JobStatus.Verifying => new("Vérification", SymbolRegular.ShieldCheckmark24, "Brush.Status.Info", "Brush.Status.InfoSoft"),
        JobStatus.Paused => new("En pause", SymbolRegular.Pause24, "Brush.Status.Paused", "Brush.Status.PausedSoft"),
        JobStatus.Completed => new("Terminé", SymbolRegular.CheckmarkCircle24, "Brush.Status.Success", "Brush.Status.SuccessSoft"),
        JobStatus.CompletedWithErrors => new("Terminé avec erreurs", SymbolRegular.Warning24, "Brush.Status.Warning", "Brush.Status.WarningSoft"),
        JobStatus.Failed => new("Erreur", SymbolRegular.ErrorCircle24, "Brush.Status.Error", "Brush.Status.ErrorSoft"),
        _ => new("Annulée", SymbolRegular.DismissCircle24, "Brush.Status.Paused", "Brush.Status.PausedSoft"),
    };

    public static Visual For(ActivityKind kind) => kind switch
    {
        ActivityKind.Copied => new("Copié", SymbolRegular.CheckmarkCircle24, "Brush.Status.Success", "Brush.Status.SuccessSoft"),
        ActivityKind.Skipped => new("Déjà présent", SymbolRegular.ArrowForward24, "Brush.Text.Secondary", "Brush.SurfaceSubtle"),
        ActivityKind.Failed => new("Échec", SymbolRegular.ErrorCircle24, "Brush.Status.Error", "Brush.Status.ErrorSoft"),
        ActivityKind.Warning => new("Attention", SymbolRegular.Warning24, "Brush.Status.Warning", "Brush.Status.WarningSoft"),
        _ => new("Info", SymbolRegular.Info24, "Brush.Status.Info", "Brush.Status.InfoSoft"),
    };

    public static Visual For(MessageStatus status) => status switch
    {
        MessageStatus.Copied => For(ActivityKind.Copied),
        MessageStatus.Skipped => For(ActivityKind.Skipped),
        MessageStatus.Failed => For(ActivityKind.Failed),
        _ => new("En attente", SymbolRegular.Clock24, "Brush.Status.Paused", "Brush.Status.PausedSoft"),
    };

    public static SymbolRegular ForFolder(SpecialFolder role) => role switch
    {
        SpecialFolder.Inbox => SymbolRegular.MailInbox24,
        SpecialFolder.Sent => SymbolRegular.Send24,
        SpecialFolder.Drafts => SymbolRegular.Drafts24,
        SpecialFolder.Trash => SymbolRegular.Delete24,
        SpecialFolder.Junk => SymbolRegular.ShieldError24,
        SpecialFolder.Archive => SymbolRegular.Archive24,
        _ => SymbolRegular.Folder24,
    };
}

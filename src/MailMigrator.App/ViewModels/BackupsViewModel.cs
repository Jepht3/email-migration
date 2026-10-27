using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailMigrator.App.Services;
using MailMigrator.App.Views.Pages;
using MailMigrator.Core.Archive;
using MailMigrator.Core.Models;
using MailMigrator.Core.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Controls;

namespace MailMigrator.App.ViewModels;

/// <summary>Une sauvegarde locale connue (créée par l'application ou ajoutée à la main).</summary>
public sealed record BackupItem(string Path, LocalArchiveSummary? Summary, string SourceEmail, ImapEndpoint? SourceServer)
{
    public bool IsAvailable => Summary is not null;
    public string Title => Summary?.Label is { Length: > 0 } label ? label : SourceEmail;
    public string FormatLabel => Summary?.Encrypted == false ? "Format lisible (.eml)" : "Chiffrée";
}

/// <summary>Écran « Sauvegardes locales » : mettre à jour depuis le serveur, restaurer vers une boîte.</summary>
public sealed partial class BackupsViewModel(IServiceProvider services, AppNavigation navigation, NewMigrationViewModel wizard, DialogService dialogs) : ObservableObject
{
    private static readonly string RememberedFile = Path.Combine(App.DataFolder, "sauvegardes.json");

    public ObservableCollection<BackupItem> Items { get; } = [];

    [ObservableProperty] private bool _isEmpty = true;

    public void Refresh()
    {
        var known = new Dictionary<string, BackupItem>(StringComparer.OrdinalIgnoreCase);

        // Sauvegardes créées par l'application : tâches dont la destination est ce PC.
        if (services.GetService<IMigrationStore>() is { } store)
        {
            foreach (var entry in store.GetAll().OrderBy(e => e.StartedAt))
            {
                var definition = store.GetJob(entry.Id)?.Definition;
                if (definition?.Destination is not { IsLocalArchive: true } archive) continue;
                known[archive.LocalArchivePath!] = new BackupItem(archive.LocalArchivePath!, null, definition.SourceEmail, definition.Source);
            }
        }
        foreach (var path in LoadRemembered())
            if (!known.ContainsKey(path)) known[path] = new BackupItem(path, null, "", null);

        Items.Clear();
        foreach (var item in known.Values)
        {
            LocalArchiveSummary? summary = null;
            try { summary = LocalArchiveMailbox.ReadSummary(item.Path); } catch (Exception) { /* dossier déplacé ou illisible */ }
            Items.Add(item with { Summary = summary });
        }
        foreach (var sorted in Items.OrderByDescending(i => i.Summary?.UpdatedAt ?? DateTimeOffset.MinValue).ToList())
        {
            Items.Remove(sorted);
            Items.Add(sorted);
        }
        IsEmpty = Items.Count == 0;
    }

    [RelayCommand]
    private void NewBackup()
    {
        wizard.PrepareNewBackup();
        navigation.Navigate(typeof(NewMigrationPage));
    }

    [RelayCommand]
    private void Update(BackupItem? item)
    {
        if (item is not { IsAvailable: true }) return;
        wizard.PrepareBackupUpdate(item.SourceEmail.Contains('@') ? item.SourceEmail : item.Title, item.SourceServer, item.Path);
        navigation.Navigate(typeof(NewMigrationPage));
    }

    [RelayCommand]
    private void Restore(BackupItem? item)
    {
        if (item is not { IsAvailable: true }) return;
        wizard.PrepareRestore(item.Path);
        navigation.Navigate(typeof(NewMigrationPage));
    }

    [RelayCommand]
    private void OpenFolder(BackupItem? item)
    {
        if (item is not null && Directory.Exists(item.Path)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{item.Path}\"") { UseShellExecute = true });
    }

    [RelayCommand]
    private async Task DeleteAsync(BackupItem? item)
    {
        if (item is null) return;
        var confirmed = await dialogs.ConfirmAsync("Supprimer cette sauvegarde ?",
            $"Tous les emails sauvegardés de {item.Title} ({item.Summary?.Messages ?? 0} messages) seront définitivement supprimés de ce PC :\n{item.Path}\n\nLes boîtes email ne sont pas touchées.",
            "Supprimer définitivement");
        if (!confirmed) return;
        try
        {
            if (Directory.Exists(item.Path) && LocalArchiveMailbox.Exists(item.Path)) Directory.Delete(item.Path, recursive: true);
            SaveRemembered(LoadRemembered().Where(p => !p.Equals(item.Path, StringComparison.OrdinalIgnoreCase)));
            Refresh();
            dialogs.Notify("Sauvegarde supprimée", item.Path, ControlAppearance.Success, SymbolRegular.Delete24);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            dialogs.Notify("Suppression impossible", $"Un fichier est peut-être ouvert ou en cours d'utilisation : {ex.Message}", ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
        }
    }

    [RelayCommand]
    private void AddExisting()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Dossier d'une sauvegarde existante" };
        if (dialog.ShowDialog() != true) return;
        if (!LocalArchiveMailbox.Exists(dialog.FolderName))
        {
            dialogs.Notify("Ce n'est pas une sauvegarde", "Le dossier choisi ne contient pas de fichier archive.db.", ControlAppearance.Caution, SymbolRegular.Warning24);
            return;
        }
        SaveRemembered(LoadRemembered().Append(dialog.FolderName).Distinct(StringComparer.OrdinalIgnoreCase));
        Refresh();
    }

    private static List<string> LoadRemembered()
    {
        try
        {
            return File.Exists(RememberedFile) ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(RememberedFile)) ?? [] : [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static void SaveRemembered(IEnumerable<string> paths)
    {
        Directory.CreateDirectory(App.DataFolder);
        File.WriteAllText(RememberedFile, JsonSerializer.Serialize(paths.ToList()));
    }
}

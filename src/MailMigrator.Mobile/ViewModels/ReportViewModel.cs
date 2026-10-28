using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailMigrator.Core.Abstractions;
using MailMigrator.Core.Formatting;
using MailMigrator.Core.Models;
using MailMigrator.Core.Persistence;
using MailMigrator.Core.Reporting;

namespace MailMigrator.Mobile.ViewModels;

public sealed record FolderLine(string Name, string Counts, string Check, Color CheckColor);

public sealed record MessageLine(string Subject, string Details, string Icon, Color IconColor, string? Reason)
{
    public bool HasReason => !string.IsNullOrEmpty(Reason);
}

/// <summary>Rapport d'une migration, partageable en HTML ou CSV (e-mail, WhatsApp, Drive…).</summary>
public sealed partial class ReportViewModel(IMigrationStore store, IMigrationRunner runner) : ObservableObject, IQueryAttributable
{
    private JobReport? _report;

    [ObservableProperty] private string _title = "Rapport";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _copied = "0";
    [ObservableProperty] private string _skipped = "0";
    [ObservableProperty] private string _failed = "0";
    [ObservableProperty] private string _volume = "";
    [ObservableProperty] private string _duration = "";
    [ObservableProperty] private bool _canRetry;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _onlyFailures;
    [ObservableProperty] private string _messagesCaption = "";

    /// <summary>Au-delà, la liste reste fluide : les exports contiennent tout.</summary>
    private const int MaxShown = 200;

    public ObservableCollection<FolderLine> Folders { get; } = [];
    public ObservableCollection<MessageLine> Messages { get; } = [];

    partial void OnSearchTextChanged(string value) => FilterMessages();
    partial void OnOnlyFailuresChanged(bool value) => FilterMessages();

    private void FilterMessages()
    {
        Messages.Clear();
        if (_report is null) return;
        var query = _report.Messages.Where(m =>
            (!OnlyFailures || m.Status == MessageStatus.Failed)
            && (string.IsNullOrWhiteSpace(SearchText)
                || m.Subject.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)
                || m.Sender.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)
                || m.Folder.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase))).ToList();
        foreach (var m in query.Take(MaxShown))
        {
            var (icon, color) = m.Status switch
            {
                MessageStatus.Copied => ("✔", Color.FromArgb("#16A34A")),
                MessageStatus.Failed => ("✘", Color.FromArgb("#DC2626")),
                MessageStatus.Skipped => ("↷", Color.FromArgb("#64748B")),
                _ => ("•", Color.FromArgb("#64748B")),
            };
            Messages.Add(new MessageLine(string.IsNullOrWhiteSpace(m.Subject) ? "(sans objet)" : m.Subject,
                $"{m.Folder} · {m.Date.LocalDateTime:dd/MM/yyyy} · {m.Sender} · {Format.Bytes(m.Size)}", icon, color, m.Error));
        }
        MessagesCaption = query.Count == 0 ? "Aucun message ne correspond."
            : query.Count > MaxShown ? $"{Format.Number(MaxShown)} premiers sur {Format.Number(query.Count)} — le rapport partagé contient tout."
            : $"{Format.Number(query.Count)} message(s)";
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("id", out var value) && Guid.TryParse(value?.ToString(), out var id)) Load(id);
    }

    private void Load(Guid id)
    {
        _report = store.GetReport(id);
        if (_report is null) return;
        var e = _report.Summary;
        Title = ReportExporter.StatusText(e.Status);
        Summary = $"{e.SourceEmail} → {e.DestinationEmail}";
        Copied = Format.Number(e.Copied);
        Skipped = Format.Number(e.Skipped);
        Failed = Format.Number(e.Failed);
        Volume = Format.Bytes(e.Bytes);
        Duration = Format.Duration(e.Duration);
        CanRetry = e.Status.IsFinished() && e.Failed > 0;

        Folders.Clear();
        foreach (var f in _report.Folders)
            Folders.Add(new FolderLine(f.DestinationPath, $"{Format.Number(f.Copied)} copiés · {Format.Number(f.Skipped)} ignorés · {Format.Number(f.Failed)} échecs",
                f.Gap == 0 ? "✔" : $"⚠ −{f.Gap}", f.Gap == 0 ? Color.FromArgb("#16A34A") : Color.FromArgb("#D97706")));
        // Une migration avec des échecs s'ouvre directement sur eux.
        OnlyFailures = e.Failed > 0;
        FilterMessages();
    }

    [RelayCommand]
    private Task ShareHtmlAsync() => ShareAsync(".html", "text/html", async (r, s) =>
    {
        await using var w = new StreamWriter(s, new System.Text.UTF8Encoding(false));
        await w.WriteAsync(ReportExporter.BuildHtml(r));
    });

    [RelayCommand]
    private Task ShareCsvAsync() => ShareAsync(".csv", "text/csv", (r, s) => ReportExporter.WriteCsvAsync(r, s));

    private async Task ShareAsync(string extension, string contentType, Func<JobReport, Stream, Task> write)
    {
        if (_report is null) return;
        var path = Path.Combine(FileSystem.CacheDirectory, ReportExporter.SuggestedFileName(_report.Summary, extension));
        await using (var file = File.Create(path)) await write(_report, file);
        await Share.Default.RequestAsync(new ShareFileRequest("Rapport de migration", new ShareFile(path, contentType)));
    }

    [RelayCommand]
    private async Task RetryAsync()
    {
        if (_report is null) return;
        if (runner.RetryFailed(_report.Summary.Id))
        {
            App.StartBackgroundService();
            await Shell.Current.GoToAsync("//migrations");
        }
        else await Shell.Current.DisplayAlertAsync("Nouvelle tentative impossible", "Aucun échec à relancer, ou la migration est déjà en cours.", "OK");
    }
}

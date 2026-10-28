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

public sealed record FailureLine(string Subject, string Reason);

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
    [ObservableProperty] private bool _hasFailures;

    public ObservableCollection<FolderLine> Folders { get; } = [];
    public ObservableCollection<FailureLine> Failures { get; } = [];

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
        Failures.Clear();
        foreach (var m in _report.Messages.Where(m => m.Status == MessageStatus.Failed).Take(100)) Failures.Add(new FailureLine(m.Subject, m.Error ?? ""));
        HasFailures = Failures.Count > 0;
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

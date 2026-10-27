using System.Collections.ObjectModel;
using System.IO;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailMigrator.App.Services;
using MailMigrator.App.Views.Pages;
using MailMigrator.Core.Abstractions;
using MailMigrator.Core.Formatting;
using MailMigrator.Core.Models;
using MailMigrator.Core.Reporting;
using Wpf.Ui.Controls;

namespace MailMigrator.App.ViewModels;

public sealed record FilterOption<T>(T Value, string Label);

/// <summary>Rapport final d'une tâche.</summary>
public sealed partial class ReportViewModel : ObservableObject
{
    private readonly IMigrationHistory _history;
    private readonly AppNavigation _navigation;
    private readonly DialogService _dialogs;
    private readonly IMigrationRunner _runner;
    private readonly SettingsViewModel _settings;

    public ReportViewModel(IMigrationHistory history, AppNavigation navigation, DialogService dialogs, IMigrationRunner runner, SettingsViewModel settings)
    {
        _history = history;
        _navigation = navigation;
        _dialogs = dialogs;
        _runner = runner;
        _settings = settings;
        MessagesView = CollectionViewSource.GetDefaultView(Messages);
        MessagesView.Filter = Matches;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Summary), nameof(HasErrors), nameof(IsSuccess), nameof(IsInProgress), nameof(AverageSpeed), nameof(TotalGap), nameof(CanRetry))]
    private MigrationHistoryEntry? _entry;

    private JobReport? _report;

    public ObservableCollection<FolderReport> Folders { get; } = [];
    public ObservableCollection<MessageReportLine> Messages { get; } = [];
    public ICollectionView MessagesView { get; }

    public IReadOnlyList<FilterOption<MessageStatus?>> StatusFilters { get; } =
    [
        new(null, "Tous les statuts"),
        new(MessageStatus.Copied, "Copiés"),
        new(MessageStatus.Skipped, "Déjà présents"),
        new(MessageStatus.Failed, "Échecs"),
    ];

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private FilterOption<MessageStatus?>? _statusFilter;
    [ObservableProperty] private int _visibleCount;

    /// <summary>Migration pas encore terminée (en cours, en pause, en file ou interrompue).</summary>
    public bool IsInProgress => Entry is { Status: var s } && !s.IsFinished();
    public bool HasErrors => !IsInProgress && Entry is { Failed: > 0 } or { Status: JobStatus.Failed or JobStatus.Cancelled };
    public bool IsSuccess => Entry is not null && !IsInProgress && !HasErrors;
    public bool CanRetry => !IsInProgress && Entry is { Failed: > 0 };
    public int TotalGap => Folders.Sum(f => f.Gap);

    public string Title => Entry?.Status switch
    {
        JobStatus.Completed => "Migration terminée",
        JobStatus.CompletedWithErrors => "Migration terminée avec des erreurs",
        JobStatus.Failed => "La migration a échoué",
        JobStatus.Cancelled => "Migration annulée",
        JobStatus.Paused => "Migration en pause",
        JobStatus.Queued => "Migration en attente de reprise",
        null => "Rapport de migration",
        _ => "Migration en cours",
    };

    public string Summary => Entry is not { } e ? "" : e.Status switch
    {
        JobStatus.Failed when e.Copied == 0 => $"La migration de {e.SourceEmail} n'a pas pu aboutir. Aucun message n'a été copié.",
        var s when !s.IsFinished() =>
            $"{Format.Number(e.Copied + e.Skipped + e.Failed)} messages traités sur {Format.Number(e.SourceMessages)} "
            + $"({Format.Number(e.Copied)} copiés, {Format.Number(e.Skipped)} déjà présents, {Format.Number(e.Failed)} en échec). "
            + "Ce rapport sera complet à la fin de la migration.",
        _ => $"{Format.Number(e.Copied)} emails copiés de {e.SourceEmail} vers {e.DestinationEmail} en {Format.Duration(e.Duration)}"
             + (e.Skipped > 0 ? $", {Format.Number(e.Skipped)} déjà présents ignorés" : "")
             + (e.Failed > 0 ? $", {Format.Number(e.Failed)} en échec." : "."),
    };

    /// <summary>Progression affichée dans l'en-tête tant que la migration n'est pas terminée.</summary>
    public double Progress => Entry is { SourceMessages: > 0 } e ? (double)(e.Copied + e.Skipped + e.Failed) / e.SourceMessages : 0;

    [RelayCommand]
    private void ShowRunning() => _navigation.Navigate(typeof(RunningPage));

    public string AverageSpeed => Entry is { Duration.TotalSeconds: > 0 } e
        ? Format.Number(e.Copied / e.Duration.TotalMinutes)
        : "—";

    public void Load(Guid id)
    {
        var report = _history.GetReport(id);
        Folders.Clear();
        Messages.Clear();
        if (report is null)
        {
            Entry = null;
            return;
        }
        foreach (var f in report.Folders) Folders.Add(f);
        foreach (var m in report.Messages) Messages.Add(m);
        SearchText = "";
        StatusFilter = StatusFilters[0];
        _report = report;
        Entry = report.Summary;
        OnPropertyChanged(nameof(TotalGap));
        OnPropertyChanged(nameof(Progress));
        RefreshFilter();
    }

    partial void OnSearchTextChanged(string value) => RefreshFilter();
    partial void OnStatusFilterChanged(FilterOption<MessageStatus?>? value) => RefreshFilter();

    private void RefreshFilter()
    {
        MessagesView.Refresh();
        VisibleCount = MessagesView.Cast<object>().Count();
    }

    private bool Matches(object o)
    {
        if (o is not MessageReportLine m) return false;
        if (StatusFilter?.Value is { } status && m.Status != status) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        return m.Subject.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)
            || m.Sender.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)
            || m.Folder.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase);
    }

    [RelayCommand]
    private Task ExportHtmlAsync() => ExportAsync(".html", "Page web (*.html)|*.html", async (report, stream) =>
    {
        await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
        await writer.WriteAsync(ReportExporter.BuildHtml(report));
    }, openAfter: true);

    [RelayCommand]
    private Task ExportCsvAsync() => ExportAsync(".csv", "Fichier CSV pour Excel (*.csv)|*.csv", (report, stream) => ReportExporter.WriteCsvAsync(report, stream), openAfter: false);

    /// <summary>Enregistre le rapport dans le dossier des rapports (Paramètres), avec un nom explicite.</summary>
    private async Task ExportAsync(string extension, string filter, Func<JobReport, Stream, Task> write, bool openAfter)
    {
        if (_report is null || Entry is null) return;
        Directory.CreateDirectory(_settings.ReportsFolder);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Exporter le rapport",
            InitialDirectory = _settings.ReportsFolder,
            FileName = ReportExporter.SuggestedFileName(Entry, extension),
            Filter = filter,
            DefaultExt = extension,
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await using (var file = File.Create(dialog.FileName)) await write(_report, file);
            _dialogs.Notify("Rapport exporté", dialog.FileName, ControlAppearance.Success, SymbolRegular.CheckmarkCircle24);
            if (openAfter) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dialog.FileName) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.Notify("Export impossible", ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
        }
    }

    [RelayCommand]
    private void RetryFailed()
    {
        if (Entry is null) return;
        if (_runner.RetryFailed(Entry.Id))
        {
            _dialogs.Notify("Nouvelle tentative", $"Les {Entry.Failed} message(s) en échec sont relancés.", ControlAppearance.Info, SymbolRegular.ArrowClockwise24);
            _navigation.Navigate(typeof(RunningPage));
        }
        else
        {
            _dialogs.Notify("Nouvelle tentative impossible", "Aucun échec à relancer, ou la migration est déjà en cours.", ControlAppearance.Caution, SymbolRegular.Warning24);
        }
    }

    [RelayCommand]
    private void Back() => _navigation.Navigate(typeof(HistoryPage));

    [RelayCommand]
    private void ShowOnlyFailures() => StatusFilter = StatusFilters[3];
}

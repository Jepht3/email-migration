using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailMigrator.App.Services;
using MailMigrator.App.Views.Pages;
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

    public ReportViewModel(IMigrationHistory history, AppNavigation navigation, DialogService dialogs)
    {
        _history = history;
        _navigation = navigation;
        _dialogs = dialogs;
        MessagesView = CollectionViewSource.GetDefaultView(Messages);
        MessagesView.Filter = Matches;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Summary), nameof(HasErrors), nameof(IsSuccess), nameof(AverageSpeed), nameof(TotalGap))]
    private MigrationHistoryEntry? _entry;

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

    public bool HasErrors => Entry is { Failed: > 0 } or { Status: JobStatus.Failed or JobStatus.Cancelled };
    public bool IsSuccess => !HasErrors;
    public int TotalGap => Folders.Sum(f => f.Gap);

    public string Title => Entry?.Status switch
    {
        JobStatus.Completed => "Migration terminée",
        JobStatus.CompletedWithErrors => "Migration terminée avec des erreurs",
        JobStatus.Failed => "La migration a échoué",
        JobStatus.Cancelled => "Migration annulée",
        _ => "Rapport de migration",
    };

    public string Summary => Entry is not { } e ? "" : e.Status == JobStatus.Failed
        ? $"Connexion impossible à la boîte source {e.SourceEmail}. Aucun message n'a été copié."
        : $"{Format.Number(e.Copied)} emails copiés de {e.SourceEmail} vers {e.DestinationEmail} en {Format.Duration(e.Duration)}"
          + (e.Skipped > 0 ? $", {Format.Number(e.Skipped)} déjà présents ignorés" : "")
          + (e.Failed > 0 ? $", {Format.Number(e.Failed)} en échec." : ".");

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
        Entry = report.Summary;
        OnPropertyChanged(nameof(TotalGap));
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
    private void ExportHtml() => NotYet("Export HTML");

    [RelayCommand]
    private void ExportCsv() => NotYet("Export CSV");

    [RelayCommand]
    private void RetryFailed() => NotYet("Nouvelle tentative des échecs");

    [RelayCommand]
    private void Back() => _navigation.Navigate(typeof(HistoryPage));

    [RelayCommand]
    private void ShowOnlyFailures() => StatusFilter = StatusFilters[3];

    private void NotYet(string feature) =>
        _dialogs.Notify(feature, "Maquette de validation du design : cette action sera branchée à l'étape (e).",
            ControlAppearance.Info, SymbolRegular.Info24);
}

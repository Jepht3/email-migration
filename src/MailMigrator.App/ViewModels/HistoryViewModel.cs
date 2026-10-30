using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailMigrator.App.Services;
using MailMigrator.App.Views.Pages;
using MailMigrator.Core.Models;
using MailMigrator.Core.Reporting;

namespace MailMigrator.App.ViewModels;

public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly IMigrationHistory _history;
    private readonly AppNavigation _navigation;

    public HistoryViewModel(IMigrationHistory history, AppNavigation navigation)
    {
        _history = history;
        _navigation = navigation;
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = Matches;
        _statusFilter = StatusFilters[0];
        _periodFilter = PeriodFilters[0];
        Refresh();
    }

    public ObservableCollection<MigrationHistoryEntry> Items { get; } = [];
    public ICollectionView ItemsView { get; }

    public IReadOnlyList<FilterOption<JobStatus?>> StatusFilters { get; } =
    [
        new(null, "Tous les statuts"),
        new(JobStatus.Completed, "Terminées"),
        new(JobStatus.CompletedWithErrors, "Avec erreurs"),
        new(JobStatus.Failed, "En échec"),
        new(JobStatus.Cancelled, "Annulées"),
    ];

    public IReadOnlyList<FilterOption<int?>> PeriodFilters { get; } =
    [
        new(null, "Toutes les dates"),
        new(7, "7 derniers jours"),
        new(30, "30 derniers jours"),
        new(365, "12 derniers mois"),
    ];

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private FilterOption<JobStatus?> _statusFilter;
    [ObservableProperty] private FilterOption<int?> _periodFilter;
    [ObservableProperty] private int _visibleCount;

    public bool IsEmpty => Items.Count == 0;

    public void Refresh()
    {
        Items.Clear();
        foreach (var e in _history.GetAll()) Items.Add(e);
        OnPropertyChanged(nameof(IsEmpty));
        RefreshFilter();
    }

    partial void OnSearchTextChanged(string value) => RefreshFilter();
    partial void OnStatusFilterChanged(FilterOption<JobStatus?> value) => RefreshFilter();
    partial void OnPeriodFilterChanged(FilterOption<int?> value) => RefreshFilter();

    private void RefreshFilter()
    {
        ItemsView.Refresh();
        VisibleCount = ItemsView.Cast<object>().Count();
    }

    private bool Matches(object o)
    {
        if (o is not MigrationHistoryEntry e) return false;
        if (StatusFilter?.Value is { } status && e.Status != status) return false;
        if (PeriodFilter?.Value is { } days && e.StartedAt < DateTimeOffset.Now.AddDays(-days)) return false;
        return string.IsNullOrWhiteSpace(SearchText)
            || e.SourceEmail.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)
            || e.DestinationEmail.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase);
    }

    [RelayCommand]
    private async Task OpenAsync(MigrationHistoryEntry? entry)
    {
        if (entry is not null) await _navigation.OpenReportAsync(entry.Id);
    }

    [RelayCommand]
    private void ClearFilters()
    {
        SearchText = "";
        StatusFilter = StatusFilters[0];
        PeriodFilter = PeriodFilters[0];
    }

    [RelayCommand]
    private void NewMigration() => _navigation.Navigate(typeof(NewMigrationPage));
}

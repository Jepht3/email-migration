using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailMigrator.App.Services;
using MailMigrator.App.Views.Pages;
using MailMigrator.Core.Models;
using MailMigrator.Core.Reporting;

namespace MailMigrator.App.ViewModels;

public sealed partial class HomeViewModel : ObservableObject
{
    private readonly IMigrationHistory _history;
    private readonly AppNavigation _navigation;

    public HomeViewModel(IMigrationHistory history, AppNavigation navigation, RunningViewModel running)
    {
        _history = history;
        _navigation = navigation;
        Running = running;
        Refresh();
    }

    public RunningViewModel Running { get; }

    public string Greeting => DateTime.Now.Hour switch
    {
        < 5 or >= 18 => "Bonsoir",
        _ => "Bonjour",
    };

    [ObservableProperty] private int _mailboxesMigrated;
    [ObservableProperty] private int _emailsCopied;
    [ObservableProperty] private long _totalBytes;
    [ObservableProperty] private double _successRate;

    public ObservableCollection<MigrationHistoryEntry> Recent { get; } = [];

    public void Refresh()
    {
        var all = _history.GetAll();
        var done = all.Where(e => e.Status is JobStatus.Completed or JobStatus.CompletedWithErrors).ToList();
        MailboxesMigrated = done.Count;
        EmailsCopied = all.Sum(e => e.Copied);
        TotalBytes = all.Sum(e => e.Bytes);
        var source = all.Sum(e => e.SourceMessages);
        SuccessRate = source == 0 ? 1 : all.Sum(e => e.Copied + e.Skipped) / (double)source;

        Recent.Clear();
        foreach (var entry in all.Take(5)) Recent.Add(entry);
    }

    [RelayCommand]
    private void NewMigration() => _navigation.Navigate(typeof(NewMigrationPage));

    [RelayCommand]
    private void ShowRunning() => _navigation.Navigate(typeof(RunningPage));

    [RelayCommand]
    private void ShowHistory() => _navigation.Navigate(typeof(HistoryPage));

    [RelayCommand]
    private void OpenReport(MigrationHistoryEntry? entry)
    {
        if (entry is not null) _navigation.OpenReport(entry.Id);
    }
}

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

    public ObservableCollection<JobCardViewModel> ActiveJobs { get; } = [];
    public ObservableCollection<MigrationHistoryEntry> Attention { get; } = [];

    public void RefreshActivity()
    {
        var active = Running.Jobs.Where(j => !j.IsFinished).ToList();
        foreach (var card in ActiveJobs.Except(active).ToList()) ActiveJobs.Remove(card);
        foreach (var card in active.Except(ActiveJobs)) ActiveJobs.Add(card);
    }

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
        RefreshActivity();
        ReplaceIfChanged(Attention, all.Where(e => e.Status is JobStatus.CompletedWithErrors or JobStatus.Failed).Take(5));
        var done = all.Where(e => e.Status is JobStatus.Completed or JobStatus.CompletedWithErrors).ToList();
        MailboxesMigrated = done.Count;
        EmailsCopied = all.Sum(e => e.Copied);
        TotalBytes = all.Sum(e => e.Bytes);
        var source = all.Sum(e => e.SourceMessages);
        SuccessRate = source == 0 ? 1 : all.Sum(e => e.Copied + e.Skipped) / (double)source;

        ReplaceIfChanged(Recent, all.Take(5));
    }

    private static void ReplaceIfChanged(ObservableCollection<MigrationHistoryEntry> target,
        IEnumerable<MigrationHistoryEntry> entries)
    {
        var next = entries.ToList();
        if (target.SequenceEqual(next)) return;
        target.Clear();
        foreach (var entry in next) target.Add(entry);
    }

    [RelayCommand]
    private void NewMigration() => _navigation.Navigate(typeof(NewMigrationPage));

    [RelayCommand]
    private void ShowRunning() => _navigation.Navigate(typeof(RunningPage));

    [RelayCommand]
    private void ShowHistory() => _navigation.Navigate(typeof(HistoryPage));

    [RelayCommand]
    private void ShowBackups() => _navigation.Navigate(typeof(BackupsPage));

    [RelayCommand]
    private void OpenJob(JobCardViewModel? job)
    {
        if (job is null) return;
        Running.SelectedJob = job;
        _navigation.Navigate(typeof(RunningPage));
    }

    [RelayCommand]
    private void OpenReport(MigrationHistoryEntry? entry)
    {
        if (entry is not null) _navigation.OpenReport(entry.Id);
    }
}

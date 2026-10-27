using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailMigrator.App.Services;
using MailMigrator.App.Views.Pages;
using MailMigrator.Core.Abstractions;
using MailMigrator.Core.Models;

namespace MailMigrator.App.ViewModels;

/// <summary>
/// Écran « Migrations en cours ». Lit les instantanés du moteur 10 fois par seconde
/// (jamais d'appel au Dispatcher par message copié) pour rester fluide avec 5 tâches.
/// </summary>
public sealed partial class RunningViewModel : ObservableObject
{
    private const int SparklineSeconds = 60;
    private readonly IMigrationRunner _runner;
    private readonly DialogService _dialogs;
    private readonly AppNavigation _navigation;
    private readonly DispatcherTimer _timer;
    private readonly List<double> _throughput = [];
    private DateTime _lastSample = DateTime.MinValue;

    public RunningViewModel(IMigrationRunner runner, DialogService dialogs, AppNavigation navigation)
    {
        _runner = runner;
        _dialogs = dialogs;
        _navigation = navigation;
        foreach (var job in runner.Jobs) Jobs.Add(new JobCardViewModel(job, dialogs, navigation));
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    public ObservableCollection<JobCardViewModel> Jobs { get; } = [];

    public IReadOnlyList<double> Throughput => _throughput;

    [ObservableProperty] private int _throughputRevision;
    [ObservableProperty] private double _overallProgress;
    [ObservableProperty] private long _bytesProcessed;
    [ObservableProperty] private long _totalBytes;
    [ObservableProperty] private int _messagesProcessed;
    [ObservableProperty] private int _messagesTotal;
    [ObservableProperty] private double _bytesPerSecond;
    [ObservableProperty] private double _messagesPerMinute;
    [ObservableProperty] private TimeSpan? _remaining;
    [ObservableProperty] private int _activeCount;
    [ObservableProperty] private int _queuedCount;
    [ObservableProperty] private int _finishedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetailOpen))]
    private JobCardViewModel? _selectedJob;

    public bool IsDetailOpen => SelectedJob is not null;
    public bool HasJobs => Jobs.Count > 0;

    private void Refresh()
    {
        // Nouvelles tâches ajoutées par l'assistant ou l'import CSV.
        foreach (var job in _runner.Jobs)
        {
            if (Jobs.All(j => j.Job != job))
            {
                Jobs.Add(new JobCardViewModel(job, _dialogs, _navigation));
                OnPropertyChanged(nameof(HasJobs));
            }
        }

        long bytes = 0, totalBytes = 0;
        int processed = 0, total = 0, active = 0, queued = 0, finished = 0;
        double bps = 0, mpm = 0;
        foreach (var card in Jobs)
        {
            var s = card.Update();
            bytes += s.BytesProcessed;
            totalBytes += s.TotalBytes;
            processed += s.Processed;
            total += s.TotalMessages;
            bps += s.BytesPerSecond;
            mpm += s.MessagesPerMinute;
            if (s.Status.IsActive()) active++;
            else if (s.Status == JobStatus.Queued) queued++;
            else finished++;
        }

        BytesProcessed = bytes;
        TotalBytes = totalBytes;
        MessagesProcessed = processed;
        MessagesTotal = total;
        OverallProgress = totalBytes == 0 ? 0 : (double)bytes / totalBytes;
        BytesPerSecond = bps;
        MessagesPerMinute = mpm;
        Remaining = bps > 1 ? TimeSpan.FromSeconds((totalBytes - bytes) / bps) : null;
        ActiveCount = active;
        QueuedCount = queued;
        FinishedCount = finished;

        // Un point par seconde pour la sparkline des 60 dernières secondes.
        if ((DateTime.UtcNow - _lastSample).TotalSeconds >= 1)
        {
            _lastSample = DateTime.UtcNow;
            _throughput.Add(bps);
            if (_throughput.Count > SparklineSeconds) _throughput.RemoveAt(0);
            ThroughputRevision++;
        }
    }

    [RelayCommand]
    private void OpenDetail(JobCardViewModel? job) => SelectedJob = job;

    [RelayCommand]
    private void CloseDetail() => SelectedJob = null;

    [RelayCommand]
    private void NewMigration() => _navigation.Navigate(typeof(NewMigrationPage));
}

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailMigrator.Core.Abstractions;
using MailMigrator.Core.Formatting;
using MailMigrator.Core.Models;
using MailMigrator.Core.Persistence;
using MailMigrator.Core.Reporting;

namespace MailMigrator.Mobile.ViewModels;

/// <summary>Écran principal : migrations en cours (rafraîchies 2 fois par seconde) et historique.</summary>
public sealed partial class MigrationsViewModel : ObservableObject
{
    private readonly IMigrationRunner _runner;
    private readonly IMigrationStore _store;

    public MigrationsViewModel(IMigrationRunner runner, IMigrationStore store)
    {
        _runner = runner;
        _store = store;
        runner.JobFinished += (_, _) => MainThread.BeginInvokeOnMainThread(RefreshHistory);
        RefreshHistory();
    }

    public ObservableCollection<JobItemViewModel> Running { get; } = [];
    public ObservableCollection<HistoryItem> History { get; } = [];

    [ObservableProperty] private bool _hasRunning;
    [ObservableProperty] private bool _isEmpty;

    /// <summary>Appelé par la page toutes les 500 ms tant qu'elle est visible.</summary>
    public void Tick()
    {
        foreach (var job in _runner.Jobs)
            if (Running.All(r => r.Job != job)) Running.Insert(0, new JobItemViewModel(job));
        foreach (var item in Running) item.Update();
        HasRunning = Running.Count > 0;
        IsEmpty = Running.Count == 0 && History.Count == 0;
    }

    public void RefreshHistory()
    {
        History.Clear();
        var running = _runner.Jobs.Select(j => j.Definition.Id).ToHashSet();
        foreach (var entry in _store.GetAll().Where(e => !running.Contains(e.Id)).Take(50)) History.Add(new HistoryItem(entry));
        IsEmpty = Running.Count == 0 && History.Count == 0;
    }

    [RelayCommand]
    private Task OpenReportAsync(Guid id) => Shell.Current.GoToAsync($"report?id={id}");

    [RelayCommand]
    private Task NewMigrationAsync() => Shell.Current.GoToAsync("//nouvelle");
}

public sealed record HistoryItem(MigrationHistoryEntry Entry)
{
    public Guid Id => Entry.Id;
    public string Title => Entry.SourceEmail;
    public string Subtitle => $"→ {Entry.DestinationEmail}";
    public string Status => ReportExporter.StatusText(Entry.Status);
    public string Details => $"{Format.Number(Entry.Copied)} copiés · {Format.Bytes(Entry.Bytes)} · {Entry.StartedAt.LocalDateTime:dd/MM/yyyy HH:mm}";
    public Color StatusColor => Entry.Status switch
    {
        JobStatus.Completed => Color.FromArgb("#16A34A"),
        JobStatus.CompletedWithErrors => Color.FromArgb("#D97706"),
        JobStatus.Failed => Color.FromArgb("#DC2626"),
        _ => Color.FromArgb("#64748B"),
    };

    public IAsyncRelayCommand OpenReportCommand { get; } = new AsyncRelayCommand(() => Shell.Current.GoToAsync($"report?id={Entry.Id}"));
}

/// <summary>Carte d'une migration en cours.</summary>
public sealed partial class JobItemViewModel(IMigrationJob job) : ObservableObject
{
    private static readonly string[] Palette = ["#2563EB", "#7C3AED", "#0D9488", "#EA580C", "#DB2777"];

    public IMigrationJob Job { get; } = job;
    public Guid Id => Job.Definition.Id;
    public string Source => Job.Definition.SourceEmail;
    public string Destination => $"→ {Job.Definition.DestinationEmail}";
    public string Initials => Source.Length >= 2 ? Source[..2].ToUpperInvariant() : "?";
    public Color Color => Color.FromArgb(Palette[Job.Definition.ColorIndex % Palette.Length]);

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _counts = "";
    [ObservableProperty] private string _speed = "";
    [ObservableProperty] private string _folder = "";
    [ObservableProperty] private string _copied = "0";
    [ObservableProperty] private string _skipped = "0";
    [ObservableProperty] private string _failed = "0";
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    private bool _isFinished;

    public bool CanCancel => !IsFinished;
    [ObservableProperty] private string? _lastActivity;

    private readonly List<Core.Progress.ActivityEvent> _buffer = [];

    public void Update()
    {
        var s = Job.GetSnapshot();
        Status = s.Status switch
        {
            JobStatus.Queued => "En file d'attente",
            JobStatus.Connecting => "Connexion",
            JobStatus.Analyzing => "Analyse des dossiers",
            JobStatus.Copying => "Copie",
            JobStatus.Verifying => "Vérification",
            JobStatus.Paused => "En pause",
            _ => ReportExporter.StatusText(s.Status),
        };
        Progress = s.Fraction;
        Counts = s.TotalMessages == 0 ? "Préparation…" : $"{Format.Number(s.Processed)} / {Format.Number(s.TotalMessages)} messages · {Format.Percent(s.Fraction)}";
        Speed = s.Status == JobStatus.Copying ? $"{Format.Number(s.MessagesPerMinute)} msg/min · {Format.Speed(s.BytesPerSecond)} · reste {Format.Duration(s.Remaining)}" : "";
        Folder = s.CurrentFolder is { } f ? $"Dossier : {f}" : "";
        Copied = Format.Number(s.Copied);
        Skipped = Format.Number(s.Skipped);
        Failed = Format.Number(s.Failed);
        IsPaused = s.Status == JobStatus.Paused;
        IsActive = s.Status.IsRunning();
        IsFinished = s.Status.IsFinished();

        _buffer.Clear();
        Job.DrainActivity(_buffer, 200);
        if (_buffer.LastOrDefault() is { } last)
            LastActivity = last.Subject is null ? last.Detail : $"{(last.Kind == Core.Progress.ActivityKind.Failed ? "✘" : "✔")} [{last.Folder}] {last.Subject}";
    }

    [RelayCommand]
    private Task OpenReportAsync() => Shell.Current.GoToAsync($"report?id={Id}");

    [RelayCommand]
    private void Pause() => Job.Pause();

    [RelayCommand]
    private void Resume() => Job.Resume();

    [RelayCommand]
    private async Task CancelAsync()
    {
        if (await Shell.Current.DisplayAlertAsync("Annuler cette migration ?",
                "Les messages déjà copiés restent dans la destination ; une relance ne copiera que les manquants.",
                "Annuler la migration", "Continuer"))
            Job.Cancel();
    }
}

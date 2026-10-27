using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailMigrator.App.Behaviors;
using MailMigrator.App.Services;
using MailMigrator.Core.Abstractions;
using MailMigrator.Core.Models;
using MailMigrator.Core.Progress;

namespace MailMigrator.App.ViewModels;

/// <summary>Carte d'une tâche sur l'écran « Migrations en cours » + son panneau de détail.</summary>
public sealed partial class JobCardViewModel : ObservableObject
{
    private const int MaxActivityLines = 2000;
    private readonly List<ActivityEvent> _buffer = new(64);
    private readonly DialogService _dialogs;
    private readonly AppNavigation _navigation;

    public JobCardViewModel(IMigrationJob job, DialogService dialogs, AppNavigation navigation)
    {
        Job = job;
        _dialogs = dialogs;
        _navigation = navigation;
        ActivityView = CollectionViewSource.GetDefaultView(Activity);
        ActivityView.Filter = o => !OnlyErrors || o is ActivityItemViewModel { Kind: ActivityKind.Failed or ActivityKind.Warning };
    }

    public IMigrationJob Job { get; }
    public int ColorIndex => Job.Definition.ColorIndex;
    public string SourceEmail => Job.Definition.SourceEmail;
    public string DestinationEmail => Job.Definition.DestinationEmail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPaused), nameof(IsRunning), nameof(IsFinished), nameof(IsQueued), nameof(IsPreparing), nameof(ShowCounters))]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand), nameof(ResumeCommand), nameof(CancelCommand))]
    private JobStatus _status;

    [ObservableProperty] private double _progress;
    [ObservableProperty] private double _folderProgress;
    [ObservableProperty] private string? _currentFolder;
    [ObservableProperty] private int _folderProcessed;
    [ObservableProperty] private int _folderTotal;
    [ObservableProperty] private int _copied;
    [ObservableProperty] private int _skipped;
    [ObservableProperty] private int _failed;
    [ObservableProperty] private int _processed;
    [ObservableProperty] private int _total;
    [ObservableProperty] private long _bytesProcessed;
    [ObservableProperty] private long _totalBytes;
    [ObservableProperty] private double _messagesPerMinute;
    [ObservableProperty] private double _bytesPerSecond;
    [ObservableProperty] private TimeSpan _elapsed;
    [ObservableProperty] private TimeSpan? _remaining;

    [ObservableProperty] private bool _isScrollFrozen;

    [ObservableProperty] private bool _onlyErrors;

    public bool IsPaused => Status == JobStatus.Paused;
    public bool IsRunning => Status.IsRunning();
    public bool IsFinished => Status.IsFinished();
    public bool IsQueued => Status == JobStatus.Queued;
    public bool IsPreparing => Status is JobStatus.Connecting or JobStatus.Analyzing;
    public bool ShowCounters => !IsQueued;

    public ObservableCollection<FolderRowViewModel> Folders { get; } = [];
    public ObservableCollection<ActivityItemViewModel> Activity { get; } = [];
    public ICollectionView ActivityView { get; }

    partial void OnOnlyErrorsChanged(bool value) => ActivityView.Refresh();

    /// <summary>Appelé ~10 fois par seconde sur le thread UI.</summary>
    public JobSnapshot Update()
    {
        var s = Job.GetSnapshot();
        Status = s.Status;
        Progress = s.Fraction;
        Copied = s.Copied;
        Skipped = s.Skipped;
        Failed = s.Failed;
        Processed = s.Processed;
        Total = s.TotalMessages;
        BytesProcessed = s.BytesProcessed;
        TotalBytes = s.TotalBytes;
        MessagesPerMinute = s.MessagesPerMinute;
        BytesPerSecond = s.BytesPerSecond;
        Elapsed = s.Elapsed;
        Remaining = s.Remaining;
        CurrentFolder = s.CurrentFolder;

        var current = s.Folders.FirstOrDefault(f => f.DestinationPath == s.CurrentFolder);
        FolderProgress = current?.Fraction ?? 0;
        FolderProcessed = current?.Processed ?? 0;
        FolderTotal = current?.Total ?? 0;

        for (var i = 0; i < s.Folders.Count; i++)
        {
            if (i < Folders.Count) Folders[i].Update(s.Folders[i], s.Folders[i].DestinationPath == s.CurrentFolder);
            else Folders.Add(new FolderRowViewModel(s.Folders[i], s.Folders[i].DestinationPath == s.CurrentFolder));
        }

        _buffer.Clear();
        Job.DrainActivity(_buffer, 60);
        if (!IsScrollFrozen || Activity.Count < MaxActivityLines)
        {
            foreach (var e in _buffer) Activity.Add(new ActivityItemViewModel(e));
            while (Activity.Count > MaxActivityLines) Activity.RemoveAt(0);
        }
        return s;
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Pause() => Job.Pause();

    [RelayCommand(CanExecute = nameof(IsPaused))]
    private void Resume() => Job.Resume();

    private bool CanCancel() => !IsFinished;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private async Task CancelAsync()
    {
        var confirmed = await _dialogs.ConfirmAsync(
            "Annuler cette migration ?",
            $"La copie de {SourceEmail} vers {DestinationEmail} sera interrompue. Les messages déjà copiés restent dans la destination ; vous pourrez relancer la migration plus tard, seuls les messages manquants seront copiés.",
            "Annuler la migration", "Continuer");
        if (confirmed) Job.Cancel();
    }

    [RelayCommand]
    private void OpenReport() => _navigation.OpenReport(Job.Definition.Id);
}

public sealed partial class FolderRowViewModel : ObservableObject
{
    public FolderRowViewModel(FolderProgress f, bool isCurrent)
    {
        Name = f.DestinationPath;
        SourceName = f.SourcePath;
        Role = f.Role;
        Update(f, isCurrent);
    }

    public string Name { get; }
    public string SourceName { get; }
    public SpecialFolder Role { get; }

    /// <summary>Nom lisible : « INBOX.Sent » → « Sent », « INBOX/Clients/Dupont » → « Clients / Dupont ».</summary>
    public string DisplayName => Pretty(Name);

    /// <summary>« depuis Junk » quand le dossier source porte un autre nom (fusion, correspondance).</summary>
    public string? Origin => Pretty(SourceName) == DisplayName ? null : $"depuis {Pretty(SourceName)}";

    public string Tooltip => $"{SourceName} → {Name}";

    private static string Pretty(string path)
    {
        if (path.Length <= 6 || !path.StartsWith("INBOX", StringComparison.OrdinalIgnoreCase) || path[5] is not ('.' or '/'))
            return path.Replace("/", " / ");
        // Serveur à espace de noms « INBOX. » (Courier, cPanel) : le séparateur est le point.
        var separator = path[5];
        return path[6..].Replace(separator.ToString(), " / ");
    }

    [ObservableProperty] private double _fraction;
    [ObservableProperty] private int _processed;
    [ObservableProperty] private int _total;
    [ObservableProperty] private int _failed;
    [ObservableProperty] private bool _isCompleted;
    [ObservableProperty] private bool _isCurrent;

    public void Update(FolderProgress f, bool isCurrent)
    {
        Fraction = f.Fraction;
        Processed = f.Processed;
        Total = f.Total;
        Failed = f.Failed;
        IsCompleted = f.IsCompleted;
        IsCurrent = isCurrent;
    }
}

/// <summary>Ligne du flux d'activité (immuable, apparaît en fondu une seule fois).</summary>
public sealed class ActivityItemViewModel(ActivityEvent e) : IFreshItem
{
    private bool _fresh = true;

    public ActivityKind Kind => e.Kind;
    public string? Folder => e.Folder;
    public DateTimeOffset Timestamp => e.Timestamp;
    public DateTimeOffset? MessageDate => e.MessageDate;
    public string? Sender => e.Sender;
    public string Subject => e.Subject ?? e.Detail ?? "";
    public long? Size => e.Size;
    public bool IsMessage => e.Subject is not null;
    public bool IsError => e.Kind == ActivityKind.Failed;
    public string? Reason => e.Kind == ActivityKind.Failed ? e.Detail : null;

    public bool ConsumeFresh()
    {
        var fresh = _fresh;
        _fresh = false;
        return fresh;
    }
}

namespace MailMigrator.Core.Engine;

/// <summary>Pause / reprise coopérative : le moteur attend entre deux messages.</summary>
public sealed class PauseController
{
    private readonly object _gate = new();
    private TaskCompletionSource? _resume;

    public bool IsPaused
    {
        get { lock (_gate) return _resume is not null; }
    }

    public void Pause()
    {
        lock (_gate) _resume ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Resume()
    {
        TaskCompletionSource? resume;
        lock (_gate)
        {
            resume = _resume;
            _resume = null;
        }
        resume?.TrySetResult();
    }

    /// <summary>Renvoie immédiatement si la tâche n'est pas en pause.</summary>
    public Task WaitIfPausedAsync(CancellationToken cancellationToken)
    {
        Task? wait;
        lock (_gate) wait = _resume?.Task;
        return wait is null ? Task.CompletedTask : wait.WaitAsync(cancellationToken);
    }
}

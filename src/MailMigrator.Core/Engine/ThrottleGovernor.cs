namespace MailMigrator.Core.Engine;

/// <summary>
/// Régulateur de débit d'une tâche. Quand le serveur signale une limite, la taille des lots est
/// divisée par deux et une pause est ajoutée entre les lots ; après une série de lots acceptés,
/// le débit remonte progressivement jusqu'au maximum.
/// </summary>
public sealed class ThrottleGovernor
{
    public const int MaxBatchSize = 50;
    private const int SuccessesBeforeSpeedUp = 10;
    private static readonly TimeSpan MaxPause = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private int _successes;

    public int BatchSize { get; private set; } = MaxBatchSize;
    public TimeSpan Pause { get; private set; } = TimeSpan.Zero;
    public int ThrottleCount { get; private set; }

    public bool IsSlowedDown
    {
        get { lock (_gate) return BatchSize < MaxBatchSize || Pause > TimeSpan.Zero; }
    }

    public void OnThrottled()
    {
        lock (_gate)
        {
            ThrottleCount++;
            _successes = 0;
            BatchSize = Math.Max(1, BatchSize / 2);
            Pause = Pause == TimeSpan.Zero ? TimeSpan.FromSeconds(2) : TimeSpan.FromTicks(Math.Min(MaxPause.Ticks, Pause.Ticks * 2));
        }
    }

    /// <returns>Vrai si le débit vient d'être relevé.</returns>
    public bool OnSuccess()
    {
        lock (_gate)
        {
            if (BatchSize >= MaxBatchSize && Pause == TimeSpan.Zero) return false;
            if (++_successes < SuccessesBeforeSpeedUp) return false;
            _successes = 0;
            BatchSize = Math.Min(MaxBatchSize, BatchSize * 2);
            Pause = Pause < TimeSpan.FromSeconds(1) ? TimeSpan.Zero : Pause / 2;
            return true;
        }
    }

    /// <summary>Attente avant de réessayer après un refus : 30 s, 1 min, 2 min, 4 min, puis 5 min.</summary>
    public static TimeSpan BackoffFor(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(300, 30 * Math.Pow(2, Math.Max(0, attempt - 1))));
}

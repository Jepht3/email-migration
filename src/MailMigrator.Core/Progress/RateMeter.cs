namespace MailMigrator.Core.Progress;

/// <summary>
/// Mesure de débit sur une fenêtre glissante (messages/min et octets/s).
/// Thread-safe ; alimenté par le moteur, lu par les instantanés.
/// </summary>
public sealed class RateMeter(TimeSpan window)
{
    private readonly Queue<(DateTimeOffset At, long Messages, long Bytes)> _samples = new();
    private readonly object _gate = new();

    public RateMeter() : this(TimeSpan.FromSeconds(20)) { }

    /// <summary>Enregistre les compteurs cumulés à l'instant <paramref name="now"/>.</summary>
    public void Record(DateTimeOffset now, long totalMessages, long totalBytes)
    {
        lock (_gate)
        {
            _samples.Enqueue((now, totalMessages, totalBytes));
            while (_samples.Count > 2 && now - _samples.Peek().At > window)
                _samples.Dequeue();
        }
    }

    public (double MessagesPerMinute, double BytesPerSecond) Current
    {
        get
        {
            lock (_gate)
            {
                if (_samples.Count < 2) return (0, 0);
                var first = _samples.Peek();
                var last = _samples.Last();
                var seconds = (last.At - first.At).TotalSeconds;
                if (seconds <= 0.001) return (0, 0);
                return ((last.Messages - first.Messages) / seconds * 60, (last.Bytes - first.Bytes) / seconds);
            }
        }
    }

    public void Reset()
    {
        lock (_gate) _samples.Clear();
    }
}

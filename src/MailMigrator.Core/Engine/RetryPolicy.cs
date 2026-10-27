using System.Net.Sockets;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;

namespace MailMigrator.Core.Engine;

/// <summary>
/// Reconnexion automatique avec backoff exponentiel (1 s, 2 s, 4 s… plafonné à 60 s, avec gigue)
/// pour les coupures réseau. Les refus du serveur (NO / BAD) ne sont pas réessayés ici.
/// </summary>
public sealed class RetryPolicy
{
    public int MaxAttempts { get; init; } = 8;
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Attente injectable (les tests n'attendent pas réellement).</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    public static RetryPolicy Default { get; } = new();

    /// <summary>Coupure réseau ou connexion perdue : on peut se reconnecter et réessayer.</summary>
    public static bool IsTransient(Exception ex) => ex switch
    {
        ImapCommandException => false,
        AuthenticationException => false,
        SslHandshakeException => false,
        OperationCanceledException => false,
        SocketException or IOException or TimeoutException or HttpRequestException => true,
        ImapProtocolException or ServiceNotConnectedException or ServiceNotAuthenticatedException => true,
        _ => ex.InnerException is not null && IsTransient(ex.InnerException),
    };

    public TimeSpan DelayFor(int attempt)
    {
        var seconds = Math.Min(MaxDelay.TotalSeconds, InitialDelay.TotalSeconds * Math.Pow(2, attempt - 1));
        return TimeSpan.FromSeconds(seconds * (0.85 + Random.Shared.NextDouble() * 0.3));
    }

    /// <param name="onRetry">Appelé avant chaque nouvelle tentative (reconnexion).</param>
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, Func<int, Exception, CancellationToken, Task> onRetry, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < MaxAttempts && !cancellationToken.IsCancellationRequested)
            {
                await Delay(DelayFor(attempt), cancellationToken).ConfigureAwait(false);
                try
                {
                    await onRetry(attempt, ex, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception reconnect) when (IsTransient(reconnect))
                {
                    // Serveur toujours injoignable : la prochaine itération réessaiera.
                }
            }
        }
    }

    public Task ExecuteAsync(Func<CancellationToken, Task> operation, Func<int, Exception, CancellationToken, Task> onRetry, CancellationToken cancellationToken) =>
        ExecuteAsync(async ct =>
        {
            await operation(ct).ConfigureAwait(false);
            return true;
        }, onRetry, cancellationToken);
}

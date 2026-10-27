using MailKit;
using MailKit.Net.Imap;
using MailMigrator.Core.Connectivity;
using MailMigrator.Core.Dedup;
using MailMigrator.Core.Folders;
using MailMigrator.Core.Imap;
using MailMigrator.Core.Models;
using MailMigrator.Core.Persistence;
using MailMigrator.Core.Progress;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SpecialFolder = MailMigrator.Core.Models.SpecialFolder;

namespace MailMigrator.Core.Engine;

/// <summary>
/// Moteur de copie d'UNE tâche : analyse, copie message par message avec reprise,
/// déduplication et reconnexion automatique, puis vérification finale.
/// Chaque tâche a ses propres connexions source et destination.
/// </summary>
public sealed class MigrationEngine(IMigrationStore store, IMailboxConnectionFactory connections, RetryPolicy? retry = null, ILogger? logger = null)
{
    private readonly RetryPolicy _retry = retry ?? RetryPolicy.Default;
    private readonly ILogger _log = logger ?? NullLogger.Instance;

    private sealed record PlannedFolder(StoredFolder Folder, int ProgressIndex);

    /// <summary>
    /// Exécute (ou reprend) la tâche. Lève <see cref="OperationCanceledException"/> si elle est
    /// interrompue : l'état est déjà enregistré, une nouvelle exécution reprendra au même point.
    /// </summary>
    public async Task<JobStatus> RunAsync(Guid jobId, IAuthProvider sourceAuth, IAuthProvider destinationAuth,
        JobProgress progress, PauseController pause, CancellationToken cancellationToken)
    {
        var job = store.GetJob(jobId) ?? throw new InvalidOperationException($"Tâche {jobId} introuvable.");
        var definition = job.Definition;
        if (definition.Source is null || definition.Destination is null)
            throw new InvalidOperationException("Les paramètres des serveurs IMAP sont incomplets.");

        progress.RestoreElapsed(job.Elapsed);
        await using var source = connections.Create(definition.Source, sourceAuth);
        await using var destination = connections.Create(definition.Destination, destinationAuth);

        try
        {
            SetStatus(jobId, progress, JobStatus.Connecting);
            progress.Info($"Connexion à {definition.Source.Host} et {definition.Destination.Host}…");
            await _retry.ExecuteAsync(source.ConnectAsync, Reconnect(progress, source), cancellationToken);
            await _retry.ExecuteAsync(destination.ConnectAsync, Reconnect(progress, destination), cancellationToken);

            SetStatus(jobId, progress, JobStatus.Analyzing);
            var plan = await AnalyzeAsync(jobId, definition.Options, source, destination, progress, cancellationToken);

            SetStatus(jobId, progress, JobStatus.Copying);
            foreach (var folder in plan)
                await CopyFolderAsync(jobId, folder, source, destination, progress, pause, cancellationToken);

            if (definition.Options.VerifyAfterCopy)
            {
                SetStatus(jobId, progress, JobStatus.Verifying);
                await VerifyAsync(plan, destination, progress, cancellationToken);
            }

            var failed = plan.Sum(p => store.GetTotals(p.Folder.Id).Failed);
            var final = failed > 0 ? JobStatus.CompletedWithErrors : JobStatus.Completed;
            store.SaveElapsed(jobId, progress.Elapsed);
            SetStatus(jobId, progress, final);
            progress.Info(failed > 0 ? $"Migration terminée avec {failed} échec(s)." : "Migration terminée.");

            // Les mots de passe sont gardés en cas d'échecs, pour « Réessayer les échecs ».
            if (definition.Options.ErasePasswordsWhenDone && final == JobStatus.Completed) store.ClearSecrets(jobId);
            return final;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            store.SaveElapsed(jobId, progress.Elapsed);
            throw;
        }
        catch (Exception ex)
        {
            // Erreur de sauvegarde locale (espace disque, clé, fichier abîmé) : son message est déjà explicite.
            var info = ex is Archive.ArchiveException archive
                ? new ConnectionErrorInfo(ConnectionError.Unknown, "Sauvegarde locale", archive.Message, "")
                : ConnectionErrorInfo.For(ImapErrorTranslator.Translate(ex, definition.Source.Host));
            _log.LogError(ex, "Échec de la tâche {JobId}", jobId);
            store.SaveElapsed(jobId, progress.Elapsed);
            store.UpdateJobStatus(jobId, JobStatus.Failed, $"{info.Title} — {ImapErrorTranslator.Flatten(ex)}");
            progress.SetStatus(JobStatus.Failed, info.Title);
            progress.Warning($"{info.Title} : {info.Explanation}");
            return JobStatus.Failed;
        }
    }

    private void SetStatus(Guid jobId, JobProgress progress, JobStatus status)
    {
        store.UpdateJobStatus(jobId, status);
        progress.SetStatus(status);
    }

    /// <summary>Régulateur de débit de la tâche (une instance de moteur par exécution).</summary>
    public ThrottleGovernor Governor { get; } = new();

    private Func<int, Exception, CancellationToken, Task> Reconnect(JobProgress progress, params IMailboxConnection[] mailboxes) =>
        async (attempt, error, ct) =>
        {
            var lost = mailboxes.Where(m => !m.IsConnected).ToArray();
            if (Connectivity.ImapErrorTranslator.IsThrottling(error))
            {
                // Limite de débit : on ralentit (lots plus petits, pause entre les lots) et on attend.
                Governor.OnThrottled();
                progress.Warning($"Le serveur limite le débit — pause de {Formatting.Format.Duration(ThrottleGovernor.BackoffFor(attempt))}, "
                                 + $"puis reprise au ralenti (lots de {Governor.BatchSize} messages).");
                _log.LogWarning("Limite de débit ({Count}) : {Reason}", Governor.ThrottleCount, error.Message);
                foreach (var mailbox in lost) await mailbox.ConnectAsync(ct);
                return;
            }
            progress.Warning($"Connexion interrompue ({error.GetType().Name}) — reconnexion, tentative {attempt}…");
            // Une seule connexion tombée : on ne reconnecte que celle-là ; sinon (délai dépassé) les deux.
            foreach (var mailbox in lost.Length > 0 ? lost : mailboxes) await mailbox.ConnectAsync(ct);
        };

    // ------------------------------------------------------------------ Analyse

    private async Task<List<PlannedFolder>> AnalyzeAsync(Guid jobId, MigrationOptions options, IMailboxConnection source,
        IMailboxConnection destination, JobProgress progress, CancellationToken ct)
    {
        var sourceFolders = await _retry.ExecuteAsync(source.ListFoldersAsync, Reconnect(progress, source), ct);
        var destinationFolders = (await _retry.ExecuteAsync(destination.ListFoldersAsync, Reconnect(progress, destination), ct)).ToList();

        var mappings = FolderMapper.Map(
            new MailboxLayout(sourceFolders, source.NamespacePrefix, source.DefaultSeparator),
            new MailboxLayout(destinationFolders, destination.NamespacePrefix, destination.DefaultSeparator),
            options);

        progress.ClearFolders();
        var plan = new List<PlannedFolder>();
        foreach (var mapping in mappings.Where(m => m.Included))
        {
            ct.ThrowIfCancellationRequested();
            var state = await _retry.ExecuteAsync(c => source.GetFolderStateAsync(mapping.SourcePath, c), Reconnect(progress, source), ct);
            var folder = store.UpsertFolder(jobId, mapping.SourcePath, mapping.DestinationPath, mapping.Role, state.UidValidity);

            var messages = await _retry.ExecuteAsync(c => source.ListMessagesAsync(mapping.SourcePath, c), Reconnect(progress, source), ct);
            messages = messages.Where(m => InRange(m, options)).ToList();
            var added = store.AddMessages(folder.Id, messages.Select(m => (m, MessageKey.For(m))));

            // Messages supprimés de la source depuis le passage précédent.
            var present = messages.Select(m => m.Uid).ToHashSet();
            foreach (var gone in store.GetMessages(folder.Id, MessageStatus.Pending).Where(m => !present.Contains(m.Uid)))
                store.SetMessageStatus(gone.Id, MessageStatus.Skipped, "Supprimé de la source depuis la dernière analyse");

            // Dossier destination : création si besoin, puis inventaire pour la déduplication.
            if (!destinationFolders.Any(d => d.Path == mapping.DestinationPath))
            {
                await _retry.ExecuteAsync(c => destination.CreateFolderAsync(mapping.DestinationPath, mapping.Role, c), Reconnect(progress, destination), ct);
                destinationFolders.Add(new RemoteFolder(mapping.DestinationPath, destination.DefaultSeparator, SpecialFolder.None));
                progress.Info($"Dossier créé dans la destination : {mapping.DestinationPath}");
            }
            var existing = await _retry.ExecuteAsync(c => destination.ListMessagesAsync(mapping.DestinationPath, c), Reconnect(progress, destination), ct);
            store.ReplaceDestinationKeys(jobId, mapping.DestinationPath, existing.Select(MessageKey.For));

            folder = store.GetFolders(jobId).First(f => f.Id == folder.Id);
            var totals = store.GetTotals(folder.Id);
            var index = progress.AddFolder(folder.SourcePath, folder.DestinationPath, folder.Role, folder.SourceCount, folder.SourceBytes,
                totals.Copied, totals.Skipped, totals.Failed, totals.BytesProcessed);
            plan.Add(new PlannedFolder(folder, index));
            _log.LogInformation("Dossier {Source} → {Destination} : {Count} messages, {Added} nouveaux", folder.SourcePath, folder.DestinationPath, folder.SourceCount, added);
        }

        var pending = plan.Sum(p => store.GetTotals(p.Folder.Id).Pending);

        // Sauvegarde sur ce PC : on vérifie la place disponible avant de copier quoi que ce soit.
        if (destination is ICapacityAware capacity)
        {
            var bytes = plan.Sum(p => store.GetMessages(p.Folder.Id, MessageStatus.Pending).Sum(m => m.Size));
            await capacity.EnsureCapacityAsync(bytes, ct);
        }
        progress.Info($"Analyse terminée : {plan.Sum(p => p.Folder.SourceCount):N0} messages dans {plan.Count} dossiers, {pending:N0} à traiter.");
        return plan;
    }

    private static bool InRange(RemoteMessage m, MigrationOptions options)
    {
        var day = DateOnly.FromDateTime(m.InternalDate.LocalDateTime);
        return (options.From is not { } from || day >= from) && (options.To is not { } to || day <= to);
    }

    // ------------------------------------------------------------------ Copie

    /// <summary>Lot : au plus 50 messages et 8 Mo ; un message de plus de 4 Mo voyage seul (par morceaux).</summary>
    private const int BatchMaxMessages = 50;
    private const long BatchMaxBytes = 8 * 1024 * 1024;
    private const long LargeMessage = 4 * 1024 * 1024;

    private sealed record Downloaded(IReadOnlyList<StoredMessage> Messages, IReadOnlyDictionary<uint, Stream> Streams, Exception? Error) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            foreach (var stream in Streams.Values) await stream.DisposeAsync();
        }
    }

    private static RemoteMessageInfo Info(StoredMessage m) => new(m.Date ?? m.InternalDate, m.Sender, m.Subject, m.Size);

    /// <summary>
    /// Copie d'un dossier par lots, en flux tendu : pendant que le lot N est déposé dans la destination,
    /// le lot N+1 est déjà en cours de téléchargement depuis la source (connexions distinctes).
    /// Une commande réseau par lot au lieu de deux ou trois par message.
    /// </summary>
    private async Task CopyFolderAsync(Guid jobId, PlannedFolder planned, IMailboxConnection source, IMailboxConnection destination,
        JobProgress progress, PauseController pause, CancellationToken ct)
    {
        var folder = planned.Folder;
        var keys = store.GetDestinationKeys(jobId, folder.DestinationPath);
        progress.SetCurrentFolder(planned.ProgressIndex);

        // 1. Déjà présents dans la destination (ou en double dans la source) : rien à transférer.
        var toCopy = new List<StoredMessage>();
        var planned_ = new HashSet<string>(StringComparer.Ordinal);
        var skipped = new List<long>();
        foreach (var message in store.GetMessages(folder.Id, MessageStatus.Pending))
        {
            if (keys.Contains(message.Key) || !planned_.Add(message.Key))
            {
                skipped.Add(message.Id);
                progress.Record(planned.ProgressIndex, Progress.ActivityKind.Skipped, Info(message), "Déjà présent dans la destination");
            }
            else toCopy.Add(message);
        }
        if (skipped.Count > 0) store.SetMessagesStatus(skipped, MessageStatus.Skipped, "Déjà présent dans la destination");

        // 2. Lots formés au fil de l'eau : leur taille suit le régulateur de débit.
        var queue = new Queue<StoredMessage>(toCopy);
        List<StoredMessage>? NextBatch()
        {
            if (queue.Count == 0) return null;
            var batch = new List<StoredMessage> { queue.Dequeue() };
            var bytes = batch[0].Size;
            if (bytes > LargeMessage) return batch;
            var max = Math.Min(BatchMaxMessages, Governor.BatchSize);
            while (batch.Count < max && queue.TryPeek(out var m) && m.Size <= LargeMessage && bytes + m.Size <= BatchMaxBytes)
            {
                batch.Add(queue.Dequeue());
                bytes += m.Size;
            }
            return batch;
        }

        // 3. Téléchargement du lot suivant pendant le dépôt du lot courant.
        var first = NextBatch();
        Task<Downloaded>? next = first is null ? null : DownloadAsync(first);
        try
        {
            while (next is not null)
            {
                await using var current = await next;
                next = null;
                if (pause.IsPaused)
                {
                    SetStatus(jobId, progress, JobStatus.Paused);
                    store.SaveElapsed(jobId, progress.Elapsed);
                    await pause.WaitIfPausedAsync(ct);
                    SetStatus(jobId, progress, JobStatus.Copying);
                }
                ct.ThrowIfCancellationRequested();
                if (Governor.Pause > TimeSpan.Zero) await _retry.Delay(Governor.Pause, ct);
                if (NextBatch() is { } following) next = DownloadAsync(following);

                await AppendAsync(current);
                if (Governor.OnSuccess())
                    progress.Info(Governor.IsSlowedDown
                        ? $"Le serveur accepte de nouveau : accélération (lots de {Governor.BatchSize} messages)."
                        : "Débit normal rétabli.");
                store.SaveElapsed(jobId, progress.Elapsed);
            }
        }
        finally
        {
            // Interruption : le lot déjà lancé est attendu puis libéré (pas de tâche orpheline).
            if (next is not null)
            {
                try { await using var _ = await next; } catch { /* sans importance à l'arrêt */ }
            }
        }

        async Task<Downloaded> DownloadAsync(List<StoredMessage> batch)
        {
            try
            {
                var streams = batch is [{ Size: > LargeMessage } single]
                    ? new Dictionary<uint, Stream>
                    {
                        [single.Uid] = await _retry.ExecuteAsync(c => source.DownloadAsync(folder.SourcePath, single.Uid, single.Size, c), Reconnect(progress, source), ct),
                    }
                    : await _retry.ExecuteAsync(c => source.DownloadBatchAsync(folder.SourcePath, batch.Select(m => (m.Uid, m.Size)).ToList(), c), Reconnect(progress, source), ct);
                return new Downloaded(batch, streams, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !RetryPolicy.IsTransient(ex))
            {
                return new Downloaded(batch, new Dictionary<uint, Stream>(), ex);
            }
        }

        async Task AppendAsync(Downloaded downloaded)
        {
            var available = downloaded.Messages.Where(m => downloaded.Streams.ContainsKey(m.Uid)).ToList();
            if (downloaded.Error is not null)
            {
                // Le lot n'a pas pu être téléchargé d'un bloc : on isole le message en cause.
                foreach (var message in downloaded.Messages) await CopyOneAsync(message, null, retried: false);
                return;
            }
            foreach (var missing in downloaded.Messages.Except(available))
                Fail(missing, "Message introuvable sur la source (supprimé pendant la migration ?)");
            if (available.Count == 0) return;

            var attempt = 0;
            try
            {
                await _retry.ExecuteAsync(async c =>
                {
                    var items = available;
                    if (attempt++ > 0)
                    {
                        // Coupure pendant le dépôt : on ne redépose pas ce qui est déjà arrivé.
                        items = [];
                        foreach (var m in available)
                            if (m.MessageId is not { } mid || !await destination.ContainsMessageIdAsync(folder.DestinationPath, mid, c)) items.Add(m);
                    }
                    foreach (var m in items) downloaded.Streams[m.Uid].Position = 0;
                    await destination.AppendBatchAsync(folder.DestinationPath,
                        items.Select(m => new AppendItem(downloaded.Streams[m.Uid], m.Flags, m.InternalDate)).ToList(), c);
                }, Reconnect(progress, destination), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !RetryPolicy.IsTransient(ex))
            {
                // Lot refusé (message trop gros, MIME invalide…) : un par un pour isoler le fautif.
                _log.LogInformation("Lot refusé dans {Folder} ({Reason}) : dépôt message par message", folder.DestinationPath, ex.Message);
                foreach (var message in available) await CopyOneAsync(message, downloaded.Streams[message.Uid], retried: true);
                return;
            }

            store.SetMessagesStatus(available.Select(m => m.Id), MessageStatus.Copied);
            store.AddDestinationKeys(jobId, folder.DestinationPath, available.Select(m => m.Key));
            foreach (var m in available)
            {
                keys.Add(m.Key);
                progress.Record(planned.ProgressIndex, Progress.ActivityKind.Copied, Info(m));
            }
        }

        // Copie d'un seul message (repli après un lot refusé ou illisible).
        async Task CopyOneAsync(StoredMessage message, Stream? content, bool retried)
        {
            try
            {
                var appendAttempted = retried;
                await _retry.ExecuteAsync(async c =>
                {
                    // L'APPEND a peut-être abouti juste avant la coupure : on vérifie avant de recopier.
                    if (appendAttempted && message.MessageId is { } mid && await destination.ContainsMessageIdAsync(folder.DestinationPath, mid, c))
                        return;
                    var stream = content ?? await source.DownloadAsync(folder.SourcePath, message.Uid, message.Size, c);
                    try
                    {
                        stream.Position = 0;
                        appendAttempted = true;
                        await destination.AppendAsync(folder.DestinationPath, stream, message.Flags, message.InternalDate, c);
                    }
                    finally
                    {
                        if (content is null) await stream.DisposeAsync();
                    }
                }, Reconnect(progress, source, destination), ct);

                store.SetMessageStatus(message.Id, MessageStatus.Copied);
                store.AddDestinationKey(jobId, folder.DestinationPath, message.Key);
                keys.Add(message.Key);
                progress.Record(planned.ProgressIndex, Progress.ActivityKind.Copied, Info(message));
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !RetryPolicy.IsTransient(ex))
            {
                Fail(message, Describe(ex));
            }
        }

        void Fail(StoredMessage message, string reason)
        {
            _log.LogWarning("Message {Uid} de {Folder} en échec : {Reason}", message.Uid, folder.SourcePath, reason);
            store.SetMessageStatus(message.Id, MessageStatus.Failed, reason);
            progress.Record(planned.ProgressIndex, Progress.ActivityKind.Failed, Info(message), reason);
        }
    }

    /// <summary>Raison d'échec d'un message, en français.</summary>
    public static string Describe(Exception ex)
    {
        var text = ex.Message;
        if (ex is ImapCommandException command)
        {
            var response = command.ResponseText ?? text;
            if (response.Contains("TOOBIG", StringComparison.OrdinalIgnoreCase) || response.Contains("too large", StringComparison.OrdinalIgnoreCase)
                || response.Contains("size", StringComparison.OrdinalIgnoreCase))
                return $"Message trop volumineux pour le serveur de destination ({response})";
            if (response.Contains("OVERQUOTA", StringComparison.OrdinalIgnoreCase) || response.Contains("quota", StringComparison.OrdinalIgnoreCase))
                return $"Quota de la boîte de destination dépassé ({response})";
            return $"Refusé par le serveur : {response}";
        }
        return ex switch
        {
            MessageNotFoundException => "Message introuvable sur la source (supprimé pendant la migration ?)",
            MimeKit.ParseException or FormatException => "Message illisible : format MIME invalide",
            _ => text,
        };
    }

    // ------------------------------------------------------------------ Vérification

    private async Task VerifyAsync(List<PlannedFolder> plan, IMailboxConnection destination, JobProgress progress, CancellationToken ct)
    {
        foreach (var planned in plan)
        {
            var state = await _retry.ExecuteAsync(c => destination.GetFolderStateAsync(planned.Folder.DestinationPath, c), Reconnect(progress, destination), ct);
            store.SetDestinationCount(planned.Folder.Id, state.Count);
            var expected = planned.Folder.SourceCount - store.GetTotals(planned.Folder.Id).Failed;
            if (state.Count < expected)
                progress.Warning($"Écart dans {planned.Folder.DestinationPath} : {expected} attendus, {state.Count} présents.");
        }
        progress.Info("Vérification terminée : recomptage effectué des deux côtés.");
    }
}

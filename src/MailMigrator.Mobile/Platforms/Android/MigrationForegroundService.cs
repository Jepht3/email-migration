using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using MailMigrator.Core.Abstractions;
using MailMigrator.Core.Formatting;
using MailMigrator.Core.Models;

namespace MailMigrator.Mobile.Droid;

/// <summary>
/// Service au premier plan : empêche Android de suspendre l'application pendant une migration
/// et affiche sa progression dans une notification permanente. S'arrête quand tout est terminé.
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeDataSync)]
public sealed class MigrationForegroundService : Service
{
    public const string ChannelId = "migrations";
    public const string DoneChannelId = "migrations-terminees";
    private const int NotificationId = 4201;

    private CancellationTokenSource? _loop;
    private PowerManager.WakeLock? _wakeLock;

    public static void Start()
    {
        var context = global::Android.App.Application.Context;
        var intent = new Intent(context, typeof(MigrationForegroundService));
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O) context.StartForegroundService(intent);
        else context.StartService(intent);
    }

    public static void EnsureChannels(Context context)
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
        var manager = (NotificationManager)context.GetSystemService(NotificationService)!;
        manager.CreateNotificationChannel(new NotificationChannel(ChannelId, "Migrations en cours", NotificationImportance.Low)
        {
            Description = "Progression des migrations d'emails",
        });
        manager.CreateNotificationChannel(new NotificationChannel(DoneChannelId, "Migrations terminées", NotificationImportance.Default)
        {
            Description = "Fin ou échec d'une migration",
        });
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        EnsureChannels(this);
        var notification = CreateNotification("Préparation de la migration…", 0, true);
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
            StartForeground(NotificationId, notification, ForegroundService.TypeDataSync);
        else
            StartForeground(NotificationId, notification);

        // Le processeur reste éveillé écran éteint, le temps de la migration.
        _wakeLock ??= ((PowerManager)GetSystemService(PowerService)!).NewWakeLock(WakeLockFlags.Partial, "lewebmax:migration");
        if (!_wakeLock!.IsHeld) _wakeLock.Acquire();

        if (_loop is null)
        {
            _loop = new CancellationTokenSource();
            _ = UpdateLoopAsync(_loop.Token);
        }
        return StartCommandResult.Sticky;
    }

    private async Task UpdateLoopAsync(CancellationToken ct)
    {
        var runner = IPlatformApplication.Current!.Services.GetRequiredService<IMigrationRunner>();
        var manager = NotificationManagerCompat.From(this)!;
        while (!ct.IsCancellationRequested)
        {
            var snapshots = runner.Jobs.Select(j => j.GetSnapshot()).ToList();
            var active = snapshots.Where(s => s.Status.IsActive() || s.Status == JobStatus.Queued).ToList();
            if (active.Count == 0)
            {
                StopSelf();
                return;
            }
            long done = active.Sum(s => s.BytesProcessed), total = active.Sum(s => s.TotalBytes);
            var processed = active.Sum(s => s.Processed);
            var messages = active.Sum(s => s.TotalMessages);
            var text = messages == 0
                ? $"{active.Count} migration(s) : connexion et analyse…"
                : $"{Format.Number(processed)} / {Format.Number(messages)} messages · {Format.Speed(active.Sum(s => s.BytesPerSecond))}";
            var percent = total == 0 ? 0 : (int)(done * 100 / total);
            try { manager.Notify(NotificationId, CreateNotification(text, percent, messages == 0)); } catch (Java.Lang.SecurityException) { /* notifications refusées */ }
            await Task.Delay(2000, ct).ContinueWith(_ => { });
        }
    }

    private Notification CreateNotification(string text, int percent, bool indeterminate)
    {
        var open = PendingIntent.GetActivity(this, 0, new Intent(this, typeof(MainActivity)).SetFlags(ActivityFlags.SingleTop),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        return new NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle("Migration d'emails en cours")!
            .SetContentText(text)!
            .SetSmallIcon(Resource.Drawable.notification_icon)!
            .SetOngoing(true)!
            .SetOnlyAlertOnce(true)!
            .SetProgress(100, percent, indeterminate)!
            .SetContentIntent(open)!
            .Build()!;
    }

    public override void OnDestroy()
    {
        _loop?.Cancel();
        if (_wakeLock?.IsHeld == true) _wakeLock.Release();
        base.OnDestroy();
    }
}

using MailMigrator.Core.Abstractions;
using MailMigrator.Core.Engine;
using MailMigrator.Core.Models;
using MailMigrator.Mobile.Services;

namespace MailMigrator.Mobile;

public partial class App : Application
{
    private readonly IServiceProvider _services;

    public App(IServiceProvider services)
    {
        _services = services;
        InitializeComponent();
        // Thème choisi dans les paramètres (par défaut : celui du téléphone).
        UserAppTheme = MobileSettings.ToAppTheme(MobileSettings.Theme);

        var scheduler = services.GetRequiredService<MigrationScheduler>();
        scheduler.MaxConcurrency = MobileSettings.MaxConcurrency;
        scheduler.JobFinished += (_, job) => MainThread.BeginInvokeOnMainThread(() => Notify(job));
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new AppShell());
        window.Created += async (_, _) =>
        {
#if ANDROID
            await Permissions.RequestAsync<Permissions.PostNotifications>();
#endif
            // Migrations interrompues (application fermée, téléphone redémarré) : reprise automatique.
            if (_services.GetRequiredService<MigrationScheduler>().ResumePending() > 0) StartBackgroundService();
        };
        return window;
    }

    public static void StartBackgroundService()
    {
#if ANDROID
        Droid.MigrationForegroundService.Start();
#endif
    }

    private static void Notify(IMigrationJob job)
    {
#if ANDROID
        var s = job.GetSnapshot();
        var (title, text) = s.Status switch
        {
            JobStatus.Completed => ("Migration terminée", $"{job.Definition.SourceEmail} : {s.Copied:N0} emails copiés, {s.Skipped:N0} déjà présents."),
            JobStatus.CompletedWithErrors => ("Migration terminée avec des erreurs", $"{job.Definition.SourceEmail} : {s.Failed:N0} en échec. Ouvrez le rapport."),
            JobStatus.Failed => ("La migration a échoué", $"{job.Definition.SourceEmail} : {s.LastError}"),
            _ => ("Migration annulée", job.Definition.SourceEmail),
        };
        var context = Android.App.Application.Context;
        var notification = new AndroidX.Core.App.NotificationCompat.Builder(context, Droid.MigrationForegroundService.DoneChannelId)
            .SetContentTitle(title)!.SetContentText(text)!.SetSmallIcon(Resource.Drawable.notification_icon)!.SetAutoCancel(true)!.Build()!;
        try { AndroidX.Core.App.NotificationManagerCompat.From(context)!.Notify(job.Definition.Id.GetHashCode(), notification); }
        catch (Java.Lang.SecurityException) { /* notifications refusées */ }
#endif
    }
}

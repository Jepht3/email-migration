using System.Windows;
using Microsoft.Toolkit.Uwp.Notifications;
using Serilog;

namespace MailMigrator.App.Services;

/// <summary>
/// Notifications Windows (toasts) : visibles même quand l'application est réduite.
/// Un clic ramène la fenêtre au premier plan sur le rapport de la migration.
/// </summary>
public sealed class ToastService
{
    private readonly AppNavigation _navigation;

    public ToastService(AppNavigation navigation)
    {
        _navigation = navigation;
        try
        {
            ToastNotificationManagerCompat.OnActivated += args =>
            {
                var parsed = ToastArguments.Parse(args.Argument);
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    var window = Application.Current.MainWindow;
                    if (window is null) return;
                    if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                    window.Activate();
                    if (parsed.TryGetValue("job", out var id) && Guid.TryParse(id, out var jobId)) _navigation.OpenReport(jobId);
                });
            };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Notifications Windows indisponibles");
        }
    }

    public void Show(string title, string message, Guid? jobId = null)
    {
        try
        {
            var builder = new ToastContentBuilder().AddText(title).AddText(message);
            if (jobId is { } id) builder.AddArgument("job", id.ToString());
            builder.Show();
        }
        catch (Exception ex)
        {
            // Notifications désactivées ou indisponibles : sans conséquence sur la migration.
            Log.Warning(ex, "Notification Windows non affichée");
        }
    }
}

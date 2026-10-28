using System.IO;
using System.Windows;
using System.Windows.Threading;
using MailMigrator.App.Services;
using MailMigrator.App.Simulation;
using MailMigrator.App.ViewModels;
using MailMigrator.App.Views;
using MailMigrator.App.Views.Pages;
using System.Net.Http;
using MailMigrator.Core.Abstractions;
using MailMigrator.Core.Connectivity;
using MailMigrator.Core.Discovery;
using MailMigrator.Core.Engine;
using MailMigrator.Core.Imap;
using MailMigrator.Core.Models;
using MailMigrator.Core.Persistence;
using Microsoft.Extensions.Logging;
using MailMigrator.Core.Reporting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Wpf.Ui;
using ThemeService = MailMigrator.App.Services.ThemeService;

namespace MailMigrator.App;

public partial class App : Application
{
    private IHost? _host;

    public static string DataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LEWEBMAX", "MailMigrator");

    private static System.Threading.Mutex? _singleInstance;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    /// <summary>
    /// Une seule instance (hors démo) : deux fenêtres reprendraient les mêmes migrations en double.
    /// Un second lancement ramène simplement la fenêtre existante au premier plan.
    /// </summary>
    private static bool IsAlreadyRunning()
    {
        if (IsDemo) return false;
        _singleInstance = new System.Threading.Mutex(true, @"Local\LEWEBMAX.MailMigrator", out var createdNew);
        if (createdNew) return false;
        var current = System.Diagnostics.Process.GetCurrentProcess();
        var other = System.Diagnostics.Process.GetProcessesByName(current.ProcessName).FirstOrDefault(p => p.Id != current.Id && p.MainWindowHandle != IntPtr.Zero);
        if (other is not null)
        {
            ShowWindow(other.MainWindowHandle, 9); // SW_RESTORE
            SetForegroundWindow(other.MainWindowHandle);
        }
        return true;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (IsAlreadyRunning())
        {
            Shutdown();
            return;
        }

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(DataFolder, "logs", "mailmigrator-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
            .CreateLogger();
        DispatcherUnhandledException += OnUnhandledException;

        _host = Host.CreateDefaultBuilder(e.Args)
            .UseSerilog()
            .ConfigureServices(services =>
            {
                services.AddSingleton(_ => AppSettings.Load());
                services.AddSingleton<ThemeService>();
                services.AddSingleton<AppNavigation>();
                services.AddSingleton<IPageService, PageService>();
                services.AddSingleton<ISnackbarService, SnackbarService>();
                services.AddSingleton<DialogService>();

                // Détection des serveurs et tests de connexion (réels).
                services.AddSingleton(_ => new HttpClient { DefaultRequestHeaders = { { "User-Agent", "LEWEBMAX-MailMigrator/1.0" } } });
                services.AddSingleton(sp => ServerDiscoveryService.CreateDefault(sp.GetRequiredService<HttpClient>()));
                services.AddSingleton<ConnectionTester>();
                services.AddSingleton<AccountServices>();

                // Boîtes IMAP ou sauvegardes sur ce PC (mots de passe et clés de sauvegarde protégés par DPAPI).
                services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
                services.AddSingleton<IMailboxConnectionFactory, MailboxConnectionFactory>();
                services.AddSingleton<ToastService>();
                if (IsDemo)
                {
                    // Démonstration (--demo, --screenshots) : 5 migrations simulées, historique fictif.
                    services.AddSingleton<DemoHistory>();
                    services.AddSingleton<IMigrationHistory>(sp => sp.GetRequiredService<DemoHistory>());
                    services.AddSingleton<DemoMigrationRunner>();
                    services.AddSingleton<IMigrationRunner>(sp => sp.GetRequiredService<DemoMigrationRunner>());
                }
                else
                {
                    // Moteur réel : état et historique dans SQLite, mots de passe chiffrés DPAPI.
                    services.AddSingleton(_ => new SqliteMigrationStore(Path.Combine(DataFolder, "migrations.db")));
                    services.AddSingleton<IMigrationStore>(sp => sp.GetRequiredService<SqliteMigrationStore>());
                    services.AddSingleton<IMigrationHistory>(sp => sp.GetRequiredService<SqliteMigrationStore>());
                    services.AddSingleton(sp => new MigrationService(
                        sp.GetRequiredService<IMigrationStore>(), sp.GetRequiredService<IMailboxConnectionFactory>(),
                        sp.GetRequiredService<ISecretProtector>(), logger: sp.GetRequiredService<ILogger<MigrationEngine>>()));
                    services.AddSingleton<MigrationScheduler>();
                    services.AddSingleton<IMigrationRunner>(sp => sp.GetRequiredService<MigrationScheduler>());
                }

                services.AddSingleton<HomeViewModel>();
                services.AddSingleton<NewMigrationViewModel>();
                services.AddSingleton<RunningViewModel>();
                services.AddSingleton<ReportViewModel>();
                services.AddSingleton<HistoryViewModel>();
                services.AddSingleton<SettingsViewModel>();
                services.AddSingleton<BackupsViewModel>();

                services.AddSingleton<HomePage>();
                services.AddSingleton<NewMigrationPage>();
                services.AddSingleton<RunningPage>();
                services.AddSingleton<ReportPage>();
                services.AddSingleton<HistoryPage>();
                services.AddSingleton<SettingsPage>();
                services.AddSingleton<BackupsPage>();
                services.AddSingleton<ShellWindow>();
            })
            .Build();
        await _host.StartAsync();

        var services = _host.Services;
        services.GetRequiredService<ThemeService>().Initialize();
        var runner = services.GetRequiredService<IMigrationRunner>();
        // Applique les réglages enregistrés (thème, accent, tâches simultanées) avant d'afficher la fenêtre.
        services.GetRequiredService<SettingsViewModel>();
        runner.JobFinished += (_, job) => Dispatcher.BeginInvoke(() => OnJobFinished(job));
        if (IsDemo) services.GetRequiredService<DemoMigrationRunner>().SeedDemo();
        else
        {
            var resumed = services.GetRequiredService<MigrationScheduler>().ResumePending();
            if (resumed > 0) Log.Information("{Count} migration(s) interrompue(s) remise(s) en file", resumed);
        }

        var shell = services.GetRequiredService<ShellWindow>();
        MainWindow = shell;
        shell.Show();

        var screenshotIndex = Array.IndexOf(e.Args, "--screenshots");
        if (screenshotIndex >= 0)
        {
            var folder = screenshotIndex + 1 < e.Args.Length ? e.Args[screenshotIndex + 1] : Path.Combine(DataFolder, "captures");
            await new ScreenshotTour(services, shell).RunAsync(folder);
            Shutdown();
        }
    }

    /// <summary>Vrai en mode démonstration : données fictives, aucun accès aux boîtes.</summary>
    public static bool IsDemo { get; } = Environment.GetCommandLineArgs().Any(a => a is "--demo" or "--screenshots");

    /// <summary>Fin d'une tâche : notification Windows (même app réduite) et rafraîchissement des écrans.</summary>
    private void OnJobFinished(IMigrationJob job)
    {
        var services = _host!.Services;
        services.GetRequiredService<HomeViewModel>().Refresh();
        services.GetRequiredService<HistoryViewModel>().Refresh();
        var s = job.GetSnapshot();
        var (title, text) = s.Status switch
        {
            JobStatus.Completed => ("Migration terminée", $"{job.Definition.SourceEmail} → {job.Definition.DestinationEmail} : {s.Copied:N0} emails copiés, {s.Skipped:N0} déjà présents."),
            JobStatus.CompletedWithErrors => ("Migration terminée avec des erreurs", $"{job.Definition.SourceEmail} : {s.Copied:N0} copiés, {s.Failed:N0} en échec. Ouvrez le rapport pour réessayer."),
            JobStatus.Failed => ("La migration a échoué", $"{job.Definition.SourceEmail} : {s.LastError ?? "voir le détail dans l'application."}"),
            _ => ("Migration annulée", $"{job.Definition.SourceEmail} → {job.Definition.DestinationEmail}"),
        };
        services.GetRequiredService<ToastService>().Show(title, text, job.Definition.Id);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Erreur non gérée");
        e.Handled = true;
        _host?.Services.GetService<DialogService>()?.Notify("Une erreur inattendue est survenue",
            "L'opération n'a pas pu aboutir. Le détail technique a été enregistré dans le journal.",
            Wpf.Ui.Controls.ControlAppearance.Danger, Wpf.Ui.Controls.SymbolRegular.ErrorCircle24);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            // Interrompt proprement les migrations : elles reprendront au prochain lancement.
            if (_host.Services.GetService<MigrationScheduler>() is { } scheduler) await scheduler.DisposeAsync();
            await _host.StopAsync();
            _host.Dispose();
        }
        await Log.CloseAndFlushAsync();
        base.OnExit(e);
    }
}

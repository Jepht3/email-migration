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

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(DataFolder, "logs", "mailmigrator-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
            .CreateLogger();
        DispatcherUnhandledException += OnUnhandledException;

        _host = Host.CreateDefaultBuilder(e.Args)
            .UseSerilog()
            .ConfigureServices(services =>
            {
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

                // Étape 2 : moteur et historique simulés (remplacés aux étapes 3b à 3d).
                services.AddSingleton<DemoHistory>();
                services.AddSingleton<IMigrationHistory>(sp => sp.GetRequiredService<DemoHistory>());
                services.AddSingleton<DemoMigrationRunner>();
                services.AddSingleton<IMigrationRunner>(sp => sp.GetRequiredService<DemoMigrationRunner>());

                services.AddSingleton<HomeViewModel>();
                services.AddSingleton<NewMigrationViewModel>();
                services.AddSingleton<RunningViewModel>();
                services.AddSingleton<ReportViewModel>();
                services.AddSingleton<HistoryViewModel>();
                services.AddSingleton<SettingsViewModel>();

                services.AddSingleton<HomePage>();
                services.AddSingleton<NewMigrationPage>();
                services.AddSingleton<RunningPage>();
                services.AddSingleton<ReportPage>();
                services.AddSingleton<HistoryPage>();
                services.AddSingleton<SettingsPage>();
                services.AddSingleton<ShellWindow>();
            })
            .Build();
        await _host.StartAsync();

        var services = _host.Services;
        services.GetRequiredService<ThemeService>().Initialize();
        services.GetRequiredService<DemoMigrationRunner>().SeedDemo();

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
            await _host.StopAsync();
            _host.Dispose();
        }
        await Log.CloseAndFlushAsync();
        base.OnExit(e);
    }
}

using MailMigrator.Core.Abstractions;
using MailMigrator.Core.Connectivity;
using MailMigrator.Core.Discovery;
using MailMigrator.Core.Engine;
using MailMigrator.Core.Imap;
using MailMigrator.Core.Persistence;
using MailMigrator.Mobile.Pages;
using MailMigrator.Mobile.ViewModels;

namespace MailMigrator.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        var services = builder.Services;
        // Cœur commun avec la version Windows : détection, moteur, SQLite, rapports.
        services.AddSingleton(_ => new HttpClient { DefaultRequestHeaders = { { "User-Agent", "LEWEBMAX-MailMigrator-Android/1.0" } } });
        services.AddSingleton(sp => ServerDiscoveryService.CreateDefault(sp.GetRequiredService<HttpClient>()));
        services.AddSingleton<ConnectionTester>();
#if ANDROID
        services.AddSingleton<ISecretProtector, Droid.KeystoreSecretProtector>();
#endif
        services.AddSingleton(_ => new SqliteMigrationStore(Path.Combine(FileSystem.AppDataDirectory, "migrations.db")));
        services.AddSingleton<IMigrationStore>(sp => sp.GetRequiredService<SqliteMigrationStore>());
        services.AddSingleton<IMailboxConnectionFactory, MailboxConnectionFactory>();
        services.AddSingleton(sp => new MigrationService(sp.GetRequiredService<IMigrationStore>(), sp.GetRequiredService<IMailboxConnectionFactory>(),
            sp.GetRequiredService<ISecretProtector>()));
        services.AddSingleton<MigrationScheduler>();
        services.AddSingleton<IMigrationRunner>(sp => sp.GetRequiredService<MigrationScheduler>());

        services.AddSingleton<MigrationsViewModel>();
        services.AddSingleton<NewMigrationViewModel>();
        services.AddTransient<ReportViewModel>();
        services.AddSingleton<MigrationsPage>();
        services.AddSingleton<NewMigrationPage>();
        services.AddTransient<ReportPage>();

        return builder.Build();
    }
}

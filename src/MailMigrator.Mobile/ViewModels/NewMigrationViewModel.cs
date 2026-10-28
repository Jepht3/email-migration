using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailMigrator.Core.Abstractions;
using MailMigrator.Core.Connectivity;
using MailMigrator.Core.Discovery;
using MailMigrator.Core.Models;
using MailMigrator.Mobile.Services;

namespace MailMigrator.Mobile.ViewModels;

/// <summary>Une migration à la fois sur le téléphone ; plusieurs peuvent tourner en parallèle (jusqu'à 5).</summary>
public sealed partial class NewMigrationViewModel : ObservableObject
{
    private readonly IMigrationRunner _runner;
    private int _color;

    public NewMigrationViewModel(IMigrationRunner runner, ServerDiscoveryService discovery, ConnectionTester tester)
    {
        _runner = runner;
        Source = new AccountSideViewModel("Source", discovery, tester);
        Destination = new AccountSideViewModel("Destination", discovery, tester);
    }

    public AccountSideViewModel Source { get; }
    public AccountSideViewModel Destination { get; }

    [ObservableProperty] private bool _includeTrash;
    [ObservableProperty] private bool _includeJunk;
    [ObservableProperty] private bool _verifyAfterCopy = true;
    [ObservableProperty] private bool _erasePasswords = MobileSettings.ErasePasswords;
    [ObservableProperty] private bool _useDateRange;
    [ObservableProperty] private DateTime _dateFrom = DateTime.Today.AddYears(-1);
    [ObservableProperty] private DateTime _dateTo = DateTime.Today;
    [ObservableProperty] private string _excludedFolders = "";

    [RelayCommand]
    private async Task StartAsync()
    {
        foreach (var side in new[] { Source, Destination })
        {
            var problem = !side.Email.Contains('@') ? "adresse email invalide"
                : side.Password.Length == 0 ? "mot de passe manquant"
                : side.IsDetecting ? "détection du serveur en cours, patientez une seconde"
                : side.Endpoint is null ? "serveur IMAP non renseigné"
                : null;
            if (problem is null) continue;
            await Shell.Current.DisplayAlertAsync($"{side.Role} incomplète", $"{(side.Email.Length > 0 ? side.Email : "Boîte")} : {problem}.", "OK");
            return;
        }
        if (UseDateRange && DateFrom.Date > DateTo.Date)
        {
            await Shell.Current.DisplayAlertAsync("Période invalide", "La date de début est après la date de fin.", "OK");
            return;
        }

        _runner.Enqueue(new MigrationJobDefinition
        {
            ColorIndex = _color++ % 5,
            SourceEmail = Source.Email.Trim(),
            Source = Source.Endpoint,
            DestinationEmail = Destination.Email.Trim(),
            Destination = Destination.Endpoint,
            Options = new MigrationOptions
            {
                IncludeTrash = IncludeTrash,
                IncludeJunk = IncludeJunk,
                VerifyAfterCopy = VerifyAfterCopy,
                ErasePasswordsWhenDone = ErasePasswords,
                From = UseDateRange ? DateOnly.FromDateTime(DateFrom) : null,
                To = UseDateRange ? DateOnly.FromDateTime(DateTo) : null,
                ExcludedFolders = ExcludedFolders.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            },
        }, Source.Password, Destination.Password);
        App.StartBackgroundService();

        // Les mots de passe ne restent pas dans le formulaire : seule la copie chiffrée est conservée.
        Source.Clear();
        Destination.Clear();
        ErasePasswords = MobileSettings.ErasePasswords;
        await Shell.Current.GoToAsync("//migrations");
    }
}

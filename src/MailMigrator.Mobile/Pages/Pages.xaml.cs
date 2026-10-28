using MailMigrator.Mobile.ViewModels;

namespace MailMigrator.Mobile.Pages;

public partial class MigrationsPage : ContentPage
{
    private readonly MigrationsViewModel _viewModel;
    private IDispatcherTimer? _timer;

    public MigrationsPage(MigrationsViewModel viewModel)
    {
        _viewModel = viewModel;
        BindingContext = viewModel;
        InitializeComponent();
    }

    // Rafraîchissement 2 fois par seconde, seulement quand l'écran est visible.
    protected override void OnAppearing()
    {
        base.OnAppearing();
        _viewModel.RefreshHistory();
        _viewModel.Tick();
        _timer ??= Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(500);
        _timer.Tick += OnTick;
        _timer.Start();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (_timer is null) return;
        _timer.Stop();
        _timer.Tick -= OnTick;
    }

    private void OnTick(object? sender, EventArgs e) => _viewModel.Tick();
}

public partial class NewMigrationPage : ContentPage
{
    public NewMigrationPage(NewMigrationViewModel viewModel)
    {
        BindingContext = viewModel;
        InitializeComponent();
    }
}

public partial class SettingsPage : ContentPage
{
    public SettingsPage(SettingsViewModel viewModel)
    {
        BindingContext = viewModel;
        InitializeComponent();
    }
}

public partial class ReportPage : ContentPage
{
    public ReportPage(ReportViewModel viewModel)
    {
        BindingContext = viewModel;
        InitializeComponent();
    }
}

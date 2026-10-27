using System.Windows.Controls;
using MailMigrator.App.ViewModels;

namespace MailMigrator.App.Views.Pages;

public partial class HomePage : Page
{
    public HomePage(HomeViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        Loaded += (_, _) => viewModel.Refresh();
    }
}

public partial class NewMigrationPage : Page
{
    public NewMigrationPage(NewMigrationViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}

public partial class ReportPage : Page
{
    public ReportPage(ReportViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}

public partial class HistoryPage : Page
{
    public HistoryPage(HistoryViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        Loaded += (_, _) => viewModel.Refresh();
    }
}

public partial class SettingsPage : Page
{
    public SettingsPage(SettingsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}

using MailMigrator.Mobile.Pages;

namespace MailMigrator.Mobile;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
        Routing.RegisterRoute("report", typeof(ReportPage));
    }
}

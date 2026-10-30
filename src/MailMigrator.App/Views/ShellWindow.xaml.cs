using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using MailMigrator.App.Services;
using MailMigrator.App.Views.Pages;
using MailMigrator.Core.Abstractions;
using MailMigrator.Core.Models;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace MailMigrator.App.Views;

public partial class ShellWindow : FluentWindow
{
    private readonly IMigrationRunner _runner;
    private readonly DialogService _dialogs;
    private bool _closeConfirmed;

    public ShellWindow(IPageService pages, AppNavigation navigation, ISnackbarService snackbar, IMigrationRunner runner, DialogService dialogs)
    {
        _runner = runner;
        _dialogs = dialogs;
        InitializeComponent();

        VersionText.Text = "Version " + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "");
        RootNavigation.SetPageService(pages);
        snackbar.SetSnackbarPresenter(SnackbarPresenter);
        navigation.Attach(RootNavigation);

        // Icône « Filled » pour l'entrée active, « Regular » pour les autres.
        RootNavigation.Navigated += (_, _) =>
        {

            foreach (var item in RootNavigation.MenuItems.OfType<NavigationViewItem>()
                         .Concat(RootNavigation.FooterMenuItems.OfType<NavigationViewItem>()))
            {
                // WPF UI assigns its top-navigation template locally, which takes
                // precedence over the style. Apply our pill template after navigation.
                var style = (Style)FindResource("Navigation.Pill");
                item.Template = (System.Windows.Controls.ControlTemplate)style.Setters
                    .OfType<Setter>().Single(s => s.Property == System.Windows.Controls.Control.TemplateProperty).Value;
                if (item.Icon is SymbolIcon icon) icon.Filled = true;
            }
        };
        Loaded += (_, _) => RootNavigation.Navigate(typeof(HomePage));
    }

    public NavigationView Navigation => RootNavigation;

    static ShellWindow()
    {
        // Chaque page gère son propre défilement (panneaux fixes, listes virtualisées) :
        // on neutralise le ScrollViewer que la NavigationView place autour des pages,
        // pour que la page reçoive la hauteur réelle de la fenêtre.
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.Page), LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            for (var parent = VisualTreeHelper.GetParent((DependencyObject)sender); parent is not null and not NavigationView; parent = VisualTreeHelper.GetParent(parent))
            {
                if (parent is System.Windows.Controls.ScrollViewer viewer)
                {
                    viewer.VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled;
                    viewer.ScrollToTop();
                    break;
                }
            }
        }));
    }

    public void CloseWithoutPrompt()
    {
        _closeConfirmed = true;
        Close();
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_closeConfirmed || !_runner.Jobs.Any(j => j.GetSnapshot().Status.IsActive()))
        {
            base.OnClosing(e);
            return;
        }
        e.Cancel = true;
        var confirmed = await _dialogs.ConfirmAsync(
            "Des migrations sont en cours",
            "Si vous fermez l'application, les migrations s'arrêtent. Elles reprendront là où elles en étaient au prochain lancement, sans copier deux fois les mêmes messages.",
            "Fermer quand même", "Continuer les migrations");
        if (!confirmed) return;
        _closeConfirmed = true;
        Close();
    }
}

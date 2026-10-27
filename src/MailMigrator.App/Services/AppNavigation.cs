using System.Windows;
using MailMigrator.App.ViewModels;
using MailMigrator.App.Views.Pages;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace MailMigrator.App.Services;

/// <summary>Navigation entre pages, utilisable depuis les ViewModels.</summary>
public sealed class AppNavigation(IServiceProvider services)
{
    private INavigationView? _view;

    public void Attach(INavigationView view) => _view = view;

    public void Navigate(Type pageType) => _view?.Navigate(pageType);

    public void OpenReport(Guid jobId)
    {
        services.GetRequiredService<ReportViewModel>().Load(jobId);
        Navigate(typeof(ReportPage));
    }
}

/// <summary>Fournit les pages à la NavigationView depuis le conteneur d'injection.</summary>
public sealed class PageService(IServiceProvider services) : IPageService
{
    public T? GetPage<T>() where T : class => services.GetService(typeof(T)) as T;

    public FrameworkElement? GetPage(Type pageType) => services.GetService(pageType) as FrameworkElement;
}

/// <summary>Boîtes de dialogue et notifications in-app (snackbar), en français.</summary>
public sealed class DialogService(ISnackbarService snackbar)
{
    public async Task<bool> ConfirmAsync(string title, string message, string confirm, string cancel = "Annuler")
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = title,
            Content = new System.Windows.Controls.TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
            PrimaryButtonText = confirm,
            CloseButtonText = cancel,
            PrimaryButtonAppearance = ControlAppearance.Danger,
        };
        return await box.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary;
    }

    public async Task ShowDetailsAsync(string title, string text)
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = title,
            Content = new System.Windows.Controls.TextBox
            {
                Text = text,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 560,
                MaxHeight = 360,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"),
                FontSize = 12,
            },
            CloseButtonText = "Fermer",
            IsPrimaryButtonEnabled = false,
        };
        await box.ShowDialogAsync();
    }

    public void Notify(string title, string message, ControlAppearance appearance = ControlAppearance.Secondary, SymbolRegular icon = SymbolRegular.Info24) =>
        snackbar.Show(title, message, appearance, new SymbolIcon(icon), TimeSpan.FromSeconds(4));
}

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MailMigrator.App.Services;
using MailMigrator.App.ViewModels;

namespace MailMigrator.App.Views.Pages;

public partial class RunningPage : Page
{
    private readonly RunningViewModel _viewModel;

    public RunningPage(RunningViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.PropertyChanged += OnViewModelChanged;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && viewModel.IsDetailOpen)
            {
                viewModel.CloseDetailCommand.Execute(null);
                e.Handled = true;
            }
        };
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RunningViewModel.IsDetailOpen)) AnimateDetail(_viewModel.IsDetailOpen);
    }

    /// <summary>Le panneau glisse depuis la droite (220 ms), le voile apparaît en fondu.</summary>
    private void AnimateDetail(bool open)
    {
        if (open)
        {
            Scrim.Visibility = Visibility.Visible;
            DetailPanel.Visibility = Visibility.Visible;
            Motion.Animate(Scrim, OpacityProperty, 1, 200);
            Motion.Animate(DetailTranslate, TranslateTransform.XProperty, DetailPanel.Width + 40, 0, 240);
            DetailPanel.Focus();
            return;
        }

        Motion.Animate(Scrim, OpacityProperty, 0, 160);
        Motion.Animate(DetailTranslate, TranslateTransform.XProperty, DetailPanel.Width + 40, 180);
        var hide = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Motion.IsEnabled ? 200 : 0) };
        hide.Tick += (_, _) =>
        {
            hide.Stop();
            if (_viewModel.IsDetailOpen) return;
            Scrim.Visibility = Visibility.Collapsed;
            DetailPanel.Visibility = Visibility.Collapsed;
        };
        hide.Start();
    }

    private void OnScrimClick(object sender, MouseButtonEventArgs e) => _viewModel.CloseDetailCommand.Execute(null);
}

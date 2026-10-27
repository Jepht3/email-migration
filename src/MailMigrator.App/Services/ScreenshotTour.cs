using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using MailMigrator.App.Simulation;
using MailMigrator.App.ViewModels;
using MailMigrator.App.Views;
using MailMigrator.App.Views.Pages;
using MailMigrator.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace MailMigrator.App.Services;

/// <summary>
/// Mode <c>--screenshots &lt;dossier&gt;</c> : parcourt tous les écrans en thème clair puis sombre
/// et capture la fenêtre réelle (effet Mica compris) pour la validation du design.
/// </summary>
public sealed class ScreenshotTour(IServiceProvider services, ShellWindow shell)
{
    public async Task RunAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        var theme = services.GetRequiredService<ThemeService>();
        var navigation = services.GetRequiredService<AppNavigation>();
        var wizard = services.GetRequiredService<NewMigrationViewModel>();
        var running = services.GetRequiredService<RunningViewModel>();
        var history = services.GetRequiredService<DemoHistory>().GetAll();
        var success = history.First(e => e.Status == JobStatus.Completed);
        var withErrors = history.First(e => e.Status == JobStatus.CompletedWithErrors);

        shell.Topmost = true;
        shell.Activate();
        wizard.SeedDemo();
        await Task.Delay(4000); // laisse les migrations simulées avancer

        foreach (var (choice, suffix) in new[] { (ThemeChoice.Light, "clair"), (ThemeChoice.Dark, "sombre") })
        {
            theme.Apply(choice);
            await Task.Delay(600);

            navigation.Navigate(typeof(HomePage));
            await Shot(folder, $"01-accueil-{suffix}");

            navigation.Navigate(typeof(NewMigrationPage));
            for (var step = 0; step < 3; step++)
            {
                wizard.ShowStep(step);
                await Shot(folder, $"0{2 + step}-nouvelle-migration-etape{step + 1}-{suffix}");
            }

            navigation.Navigate(typeof(RunningPage));
            await Shot(folder, $"05-migrations-en-cours-{suffix}", 1800);
            running.OpenDetailCommand.Execute(running.Jobs[0]);
            await Shot(folder, $"06-detail-tache-{suffix}", 1500);
            running.CloseDetailCommand.Execute(null);
            await Task.Delay(400);

            navigation.OpenReport(success.Id);
            await Shot(folder, $"07-rapport-{suffix}", 1400);
            navigation.OpenReport(withErrors.Id);
            await Shot(folder, $"08-rapport-avec-erreurs-{suffix}", 1000);

            navigation.Navigate(typeof(HistoryPage));
            await Shot(folder, $"09-historique-{suffix}");

            navigation.Navigate(typeof(SettingsPage));
            await Shot(folder, $"10-parametres-{suffix}");
        }
        shell.CloseWithoutPrompt();
    }

    private async Task Shot(string folder, string name, int delay = 900)
    {
        await Task.Delay(delay);
        var hwnd = new WindowInteropHelper(shell).Handle;
        DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out var r, Marshal.SizeOf<Rect>());
        int width = r.Right - r.Left, height = r.Bottom - r.Top;

        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, width, height);
        var old = SelectObject(memory, bitmap);
        BitBlt(memory, 0, 0, width, height, screen, r.Left, r.Top, 0x00CC0020 /* SRCCOPY */);
        SelectObject(memory, old);
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            await using var file = File.Create(Path.Combine(folder, name + ".png"));
            encoder.Save(file);
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private const int DwmwaExtendedFrameBounds = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect value, int size);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
}

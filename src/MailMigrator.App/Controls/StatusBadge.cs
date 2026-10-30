using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MailMigrator.Core.Models;
using MailMigrator.Core.Progress;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace MailMigrator.App.Controls;

/// <summary>Badge coloré icône + texte pour l'état d'une tâche, d'un message ou d'une ligne d'activité.</summary>
public sealed class StatusBadge : Border
{
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(object), typeof(StatusBadge),
        new PropertyMetadata(null, (d, _) => ((StatusBadge)d).Update()));

    public static readonly DependencyProperty TextOverrideProperty = DependencyProperty.Register(
        nameof(TextOverride), typeof(string), typeof(StatusBadge),
        new PropertyMetadata(null, (d, _) => ((StatusBadge)d).Update()));

    private readonly SymbolIcon _icon = new() { FontSize = 20, Filled = true, Margin = new Thickness(0, 0, 6, 0) };
    private readonly TextBlock _text = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };

    /// <summary><see cref="JobStatus"/>, <see cref="MessageStatus"/> ou <see cref="ActivityKind"/>.</summary>
    public object? Status { get => GetValue(StatusProperty); set => SetValue(StatusProperty, value); }

    public string? TextOverride { get => (string?)GetValue(TextOverrideProperty); set => SetValue(TextOverrideProperty, value); }

    public StatusBadge()
    {
        CornerRadius = new CornerRadius(999);
        Padding = new Thickness(8, 3, 10, 3);
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(_icon);
        panel.Children.Add(_text);
        Child = panel;
        Update();
    }

    private void Update()
    {
        var visual = Status switch
        {
            JobStatus s => StatusVisuals.For(s),
            MessageStatus m => StatusVisuals.For(m),
            ActivityKind a => StatusVisuals.For(a),
            _ => StatusVisuals.For(JobStatus.Queued),
        };
        _icon.Symbol = visual.Symbol;
        _text.Text = TextOverride ?? visual.Text;
        _icon.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, visual.BrushKey);
        _text.SetResourceReference(TextBlock.ForegroundProperty, visual.BrushKey);
        SetResourceReference(BackgroundProperty, visual.SoftBrushKey);
        AutomationProperties.SetName(this, _text.Text);
    }
}

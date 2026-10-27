using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;

namespace MailMigrator.App.Behaviors;

/// <summary>Fait défiler une liste jusqu'au dernier élément tant que <c>Follow</c> est vrai.</summary>
public static class AutoScroll
{
    public static readonly DependencyProperty FollowProperty = DependencyProperty.RegisterAttached(
        "Follow", typeof(bool), typeof(AutoScroll), new PropertyMetadata(false, OnChanged));

    private static readonly DependencyProperty HookedProperty = DependencyProperty.RegisterAttached(
        "Hooked", typeof(bool), typeof(AutoScroll), new PropertyMetadata(false));

    public static bool GetFollow(DependencyObject d) => (bool)d.GetValue(FollowProperty);
    public static void SetFollow(DependencyObject d, bool value) => d.SetValue(FollowProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox list) return;
        if (!(bool)list.GetValue(HookedProperty))
        {
            list.SetValue(HookedProperty, true);
            ((INotifyCollectionChanged)list.Items).CollectionChanged += (_, args) =>
            {
                if (args.Action == NotifyCollectionChangedAction.Add && GetFollow(list)) ScrollToEnd(list);
            };
        }
        if (e.NewValue is true) ScrollToEnd(list);
    }

    private static void ScrollToEnd(ListBox list)
    {
        if (list.Items.Count > 0) list.ScrollIntoView(list.Items[^1]);
    }
}

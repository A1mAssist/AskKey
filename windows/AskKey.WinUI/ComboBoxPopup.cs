using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace AskKeyApp;

public static class ComboBoxPopup
{
    public static readonly DependencyProperty ConstrainToRootProperty = DependencyProperty.RegisterAttached(
        "ConstrainToRoot", typeof(bool), typeof(ComboBoxPopup), new PropertyMetadata(false, OnConstrainToRootChanged));

    public static bool GetConstrainToRoot(DependencyObject element) => (bool)element.GetValue(ConstrainToRootProperty);

    public static void SetConstrainToRoot(DependencyObject element, bool value) => element.SetValue(ConstrainToRootProperty, value);

    private static void OnConstrainToRootChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not ComboBox combo) return;
        combo.Loaded -= OnLoaded;
        combo.DropDownOpened -= OnDropDownOpened;
        if (args.NewValue is true)
        {
            combo.Loaded += OnLoaded;
            combo.DropDownOpened += OnDropDownOpened;
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ComboBox combo) return;
        combo.ApplyTemplate();
        var popup = FindPopup(combo);
        if (popup is null) return;

        // ComboBox uses a square top-level HWND by default; render in-root so the template border can round.
        popup.ShouldConstrainToRootBounds = true;
        RoundPopupBorder(popup);
    }

    private static void OnDropDownOpened(object? sender, object e)
    {
        if (sender is ComboBox combo && FindPopup(combo) is { } popup) RoundPopupBorder(popup);
    }

    private static Popup? FindPopup(ComboBox combo)
        => combo.FindName("Popup") as Popup ?? FindDescendant<Popup>(combo);

    private static void RoundPopupBorder(Popup popup)
    {
        if (popup.Child is DependencyObject root && FindPopupBorder(root) is { } border)
            border.CornerRadius = new CornerRadius(8);
    }

    private static Border? FindPopupBorder(DependencyObject root)
    {
        if (root is Border { Name: "PopupBorder" } border) return border;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            if (FindPopupBorder(VisualTreeHelper.GetChild(root, index)) is { } nested) return nested;
        }
        return null;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }
}

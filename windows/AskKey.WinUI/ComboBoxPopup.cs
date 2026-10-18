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
        if (args.NewValue is true) combo.Loaded += OnLoaded;
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ComboBox combo) return;
        combo.ApplyTemplate();
        var popup = combo.FindName("Popup") as Popup ?? FindDescendant<Popup>(combo);
        if (popup is null) return;

        // ComboBox uses a square top-level HWND by default; render in-root so PopupBorder's 8px radius is visible.
        popup.ShouldConstrainToRootBounds = true;
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

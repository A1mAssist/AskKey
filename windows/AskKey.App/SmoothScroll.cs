using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AskKeyApp;

public static class SmoothScroll
{
    private const double WheelStep = 56;
    private const double SettleSeconds = 0.055;
    private const double SettleDistance = 0.1;
    private static readonly ConditionalWeakTable<ScrollViewer, Scroller> States = new();

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    internal static double GetTargetOffset(double current, int wheelDelta, double scrollableHeight)
    {
        var target = current - wheelDelta / 120d * WheelStep;
        return Math.Clamp(target, 0, Math.Max(0, scrollableHeight));
    }

    internal static double Advance(double current, double target, double elapsedSeconds)
    {
        var elapsed = Math.Clamp(elapsedSeconds, 0, 0.1);
        var next = current + (target - current) * (1 - Math.Exp(-elapsed / SettleSeconds));
        return Math.Abs(target - next) < SettleDistance ? target : next;
    }

    private static void OnIsEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not ScrollViewer viewer) return;
        viewer.PreviewMouseWheel -= OnPreviewMouseWheel;
        viewer.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
        if (args.NewValue is true)
        {
            viewer.PreviewMouseWheel += OnPreviewMouseWheel;
            viewer.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        }
        else if (States.TryGetValue(viewer, out var scroller))
        {
            scroller.Stop();
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs args)
    {
        if (args.Handled || sender is not ScrollViewer viewer) return;
        if (viewer.ScrollableHeight <= 0 || HasNestedScrollable(args.OriginalSource as DependencyObject, viewer)) return;

        States.GetValue(viewer, item => new Scroller(item)).ScrollBy(args.Delta);
        args.Handled = true;
    }

    private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs args)
    {
        if (sender is ScrollViewer viewer && States.TryGetValue(viewer, out var scroller))
            scroller.Stop();
    }

    private static bool HasNestedScrollable(DependencyObject? source, ScrollViewer viewer)
    {
        for (var node = source; node is not null && node != viewer; node = ParentOf(node))
        {
            if (node is ScrollViewer { ScrollableHeight: > 0 }) return true;
        }
        return false;
    }

    private static DependencyObject? ParentOf(DependencyObject node) => node switch
    {
        Visual => VisualTreeHelper.GetParent(node),
        FrameworkContentElement content => content.Parent,
        _ => LogicalTreeHelper.GetParent(node),
    };

    private sealed class Scroller
    {
        private readonly ScrollViewer _viewer;
        private readonly EventHandler _rendering;
        private long _lastTick;
        private double _target;
        private bool _running;

        public Scroller(ScrollViewer viewer)
        {
            _viewer = viewer;
            _rendering = OnRendering;
            viewer.Unloaded += (_, _) => Stop();
        }

        public void ScrollBy(int wheelDelta)
        {
            if (!_running)
            {
                _target = _viewer.VerticalOffset;
                _lastTick = Stopwatch.GetTimestamp();
                CompositionTarget.Rendering += _rendering;
                _running = true;
            }

            _target = GetTargetOffset(_target, wheelDelta, _viewer.ScrollableHeight);
            if (Math.Abs(_target - _viewer.VerticalOffset) < SettleDistance) Stop();
        }

        public void Stop()
        {
            if (!_running) return;
            CompositionTarget.Rendering -= _rendering;
            _running = false;
        }

        private void OnRendering(object? sender, EventArgs args)
        {
            var now = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(_lastTick, now).TotalSeconds;
            _lastTick = now;

            _target = Math.Clamp(_target, 0, Math.Max(0, _viewer.ScrollableHeight));
            var next = Advance(_viewer.VerticalOffset, _target, elapsed);
            _viewer.ScrollToVerticalOffset(next);
            if (next == _target) Stop();
        }
    }
}
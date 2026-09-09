using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace RhiLinux.Gui;

internal static class SmoothScrolling
{
    private const double WheelStep = 86;
    private const double ResponseRate = 22;
    private const double MinimumTargetLead = 260;
    private const double ViewportTargetLead = 1.25;
    private const double CompletionTolerance = 0.25;
    private static readonly ConditionalWeakTable<ScrollViewer, ScrollAnimation> Animations = new();
    private static bool initialized;

    public static void Initialize()
    {
        if (initialized) return;
        initialized = true;
        InputElement.PointerWheelChangedEvent.AddClassHandler<TopLevel>(
            OnPointerWheelChanged,
            RoutingStrategies.Tunnel);
        InputElement.PointerPressedEvent.AddClassHandler<TopLevel>(
            OnPointerPressed,
            RoutingStrategies.Tunnel);
    }

    private static void OnPointerWheelChanged(TopLevel _, PointerWheelEventArgs eventArgs)
    {
        if (eventArgs.Handled || !MotionEnabled() || eventArgs.Source is not Visual source) return;

        var delta = eventArgs.Delta;
        if ((eventArgs.KeyModifiers & KeyModifiers.Shift) != 0 && Math.Abs(delta.Y) > Math.Abs(delta.X))
            delta = new Vector(delta.Y, 0);

        foreach (var viewer in ScrollViewersFrom(source))
        {
            var animation = Animations.GetValue(viewer, static item => new ScrollAnimation(item));
            if (!animation.TryScroll(delta)) continue;
            eventArgs.Handled = true;
            return;
        }
    }

    private static void OnPointerPressed(TopLevel _, PointerPressedEventArgs eventArgs)
    {
        if (eventArgs.Source is not Visual source) return;
        foreach (var viewer in ScrollViewersFrom(source))
            if (Animations.TryGetValue(viewer, out var animation))
                animation.Stop();
    }

    private static IEnumerable<ScrollViewer> ScrollViewersFrom(Visual source)
    {
        if (source is ScrollViewer viewer) yield return viewer;
        foreach (var ancestor in source.GetVisualAncestors().OfType<ScrollViewer>())
            yield return ancestor;
    }

    private static bool MotionEnabled() =>
        Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime
        {
            MainWindow.DataContext: MainViewModel { UseMotion: false }
        };

    private sealed class ScrollAnimation
    {
        private readonly ScrollViewer viewer;
        private Vector target;
        private Vector lastAppliedOffset;
        private long previousFrameTimestamp;
        private bool isAnimating;
        private bool frameRequested;

        public ScrollAnimation(ScrollViewer viewer)
        {
            this.viewer = viewer;
            target = viewer.Offset;
            lastAppliedOffset = target;
        }

        public bool TryScroll(Vector wheelDelta)
        {
            var topLevel = TopLevel.GetTopLevel(viewer);
            if (topLevel is null) return false;

            var maximum = MaximumOffset();
            var current = Clamp(viewer.Offset, maximum);
            if (!isAnimating || !Close(current, lastAppliedOffset, 1))
            {
                target = current;
                lastAppliedOffset = current;
            }
            else
                target = Clamp(target, maximum);

            var horizontalDelta = wheelDelta.X;
            var verticalDelta = wheelDelta.Y;
            if (Math.Abs(verticalDelta) > double.Epsilon && maximum.Y <= 0 && maximum.X > 0 && Math.Abs(horizontalDelta) <= double.Epsilon)
            {
                horizontalDelta = verticalDelta;
                verticalDelta = 0;
            }

            var horizontalMovement = -horizontalDelta * WheelStep;
            var verticalMovement = -verticalDelta * WheelStep;
            var horizontalLead = Math.Max(MinimumTargetLead, viewer.Viewport.Width * ViewportTargetLead);
            var verticalLead = Math.Max(MinimumTargetLead, viewer.Viewport.Height * ViewportTargetLead);
            var next = new Vector(
                NextTarget(current.X, target.X, horizontalMovement, maximum.X, horizontalLead),
                NextTarget(current.Y, target.Y, verticalMovement, maximum.Y, verticalLead));
            if (Close(next, target))
                return isAnimating && !Close(current, target, CompletionTolerance);

            target = next;
            if (!isAnimating)
            {
                isAnimating = true;
                previousFrameTimestamp = Stopwatch.GetTimestamp();
            }
            RequestFrame(topLevel);
            return true;
        }

        public void Stop()
        {
            isAnimating = false;
            target = viewer.Offset;
            lastAppliedOffset = target;
        }

        private void RequestFrame(TopLevel? topLevel = null)
        {
            if (!isAnimating || frameRequested) return;
            topLevel ??= TopLevel.GetTopLevel(viewer);
            if (topLevel is null)
            {
                Stop();
                return;
            }

            frameRequested = true;
            topLevel.RequestAnimationFrame(OnAnimationFrame);
        }

        private void OnAnimationFrame(TimeSpan _)
        {
            frameRequested = false;
            if (!isAnimating) return;
            if (!MotionEnabled())
            {
                Stop();
                return;
            }

            var maximum = MaximumOffset();
            target = Clamp(target, maximum);
            var current = Clamp(viewer.Offset, maximum);
            if (!Close(current, lastAppliedOffset, 1))
            {
                Stop();
                return;
            }

            var now = Stopwatch.GetTimestamp();
            var seconds = Math.Clamp(Stopwatch.GetElapsedTime(previousFrameTimestamp, now).TotalSeconds, 0.001, 1d / 30d);
            previousFrameTimestamp = now;
            var blend = 1 - Math.Exp(-ResponseRate * seconds);
            var next = new Vector(
                current.X + (target.X - current.X) * blend,
                current.Y + (target.Y - current.Y) * blend);

            if (Close(next, target, CompletionTolerance))
            {
                viewer.Offset = target;
                Stop();
                return;
            }

            lastAppliedOffset = Clamp(next, maximum);
            viewer.Offset = lastAppliedOffset;
            RequestFrame();
        }

        private Vector MaximumOffset() => new(
            Math.Max(0, viewer.Extent.Width - viewer.Viewport.Width),
            Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height));

        private static Vector Clamp(Vector value, Vector maximum) => new(
            Math.Clamp(value.X, 0, maximum.X),
            Math.Clamp(value.Y, 0, maximum.Y));

        private static double NextTarget(
            double current,
            double previousTarget,
            double movement,
            double maximum,
            double maximumLead)
        {
            if (Math.Abs(movement) <= double.Epsilon) return previousTarget;
            var remaining = previousTarget - current;
            if (Math.Abs(remaining) > CompletionTolerance && Math.Sign(remaining) != Math.Sign(movement))
                previousTarget = current;
            return Math.Clamp(
                previousTarget + movement,
                Math.Max(0, current - maximumLead),
                Math.Min(maximum, current + maximumLead));
        }

        private static bool Close(Vector left, Vector right, double tolerance = 0.01) =>
            Math.Abs(left.X - right.X) <= tolerance && Math.Abs(left.Y - right.Y) <= tolerance;
    }
}

using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Garou.Views.Controls;

public sealed class AnimatedScrollViewer : ScrollViewer
{
    public static readonly StyledProperty<TimeSpan> DurationProperty =
        AvaloniaProperty.Register<AnimatedScrollViewer, TimeSpan>(nameof(Duration), TimeSpan.FromMilliseconds(250));

    public static readonly StyledProperty<double> ScrollMultiplierProperty =
        AvaloniaProperty.Register<AnimatedScrollViewer, double>(nameof(ScrollMultiplier), 1.0);

    private double _targetOffset;
    private bool _isScrolling;
    //private CancellationTokenSource? _scrollAnimationCancellation;
    private CancellationTokenSource? _scrollCancellation;

    protected override Type StyleKeyOverride => typeof(ScrollViewer);

    public AnimatedScrollViewer()
    {
        AddHandler(
            InputElement.PointerWheelChangedEvent,
            OnPointerWheelChanged,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
    }

    public TimeSpan Duration
    {
        get => GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    public double ScrollMultiplier
    {
        get => GetValue(ScrollMultiplierProperty);
        set => SetValue(ScrollMultiplierProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _targetOffset = Offset.Y;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        /*
        var cancellation = _scrollAnimationCancellation;
        _scrollAnimationCancellation = null;
        cancellation?.Cancel();
        base.OnDetachedFromVisualTree(e);
        */
        _scrollCancellation?.Cancel();
        _scrollCancellation = null;
        base.OnDetachedFromVisualTree(e);
    }

    /*
    private async void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.Y == 0)
        {
            return;
        }

        e.Handled = true;

        var currentOffset = Offset;
        var maxOffset = Math.Max(0, Extent.Height - Viewport.Height);
        _targetOffset = Math.Clamp(
            _targetOffset - e.Delta.Y * 50 * ScrollMultiplier,
            0,
            maxOffset);

        var animation = new Animation
        {
            Duration = Duration > TimeSpan.Zero ? Duration : TimeSpan.FromMilliseconds(400),
            Easing = new CubicEaseOut(),
            Children =
            {
                new KeyFrame
                {
                    Setters = { new Setter(OffsetProperty, currentOffset) },
                    Cue = new Cue(0)
                },
                new KeyFrame
                {
                    Setters = { new Setter(OffsetProperty, new Vector(currentOffset.X, _targetOffset)) },
                    Cue = new Cue(1)
                }
            }
        };

        var cancellation = new CancellationTokenSource();
        var previousCancellation = _scrollAnimationCancellation;
        _scrollAnimationCancellation = cancellation;
        previousCancellation?.Cancel();

        try
        {
            await animation.RunAsync(this, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_scrollAnimationCancellation, cancellation))
            {
                _scrollAnimationCancellation = null;
                _targetOffset = Offset.Y;
            }

            cancellation.Dispose();
        }
    }
    */

    private async void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.Y == 0)
        {
            return;
        }

        e.Handled = true;

        var maxOffset = Math.Max(0, Extent.Height - Viewport.Height);

        // Follow the actual offset when idle (e.g. after a scrollbar drag).
        if (!_isScrolling)
        {
            _targetOffset = Offset.Y;
        }

        // Only the target changes here; the running loop picks it up.
        _targetOffset = Math.Clamp(
            _targetOffset - e.Delta.Y * 50 * ScrollMultiplier,
            0,
            maxOffset);

        if (_isScrolling)
        {
            return;
        }

        _isScrolling = true;
        _scrollCancellation = new CancellationTokenSource();
        _ = RunScrollLoopAsync(_scrollCancellation.Token);
    }

    private async Task RunScrollLoopAsync(CancellationToken token)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null)
            {
                return;
            }

            // Time constant derived from Duration: the view covers most of the distance within ~Duration.
            var rate = 5.0 / Math.Max(0.05, Duration.TotalSeconds);

            // Pretend the previous frame was one 60 Hz frame ago so the first step moves immediately.
            var last = Stopwatch.GetTimestamp() - Stopwatch.Frequency / 60;

            while (!token.IsCancellationRequested)
            {
                var now = Stopwatch.GetTimestamp();
                var dt = Math.Min(Stopwatch.GetElapsedTime(last, now).TotalSeconds, 0.05);
                last = now;

                var current = Offset.Y;
                var diff = _targetOffset - current;

                if (Math.Abs(diff) < 0.5)
                {
                    Offset = new Vector(Offset.X, _targetOffset);
                    break;
                }

                Offset = new Vector(Offset.X, current + diff * (1.0 - Math.Exp(-rate * dt)));

                await NextFrameAsync(topLevel, token);
            }
        }
        finally
        {
            _isScrolling = false;
        }
    }

    // Completes normally when the frame fires or the token is cancelled; never throws.
    private static Task NextFrameAsync(TopLevel topLevel, CancellationToken token)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var registration = token.Register(() => tcs.TrySetResult());
        topLevel.RequestAnimationFrame(_ => tcs.TrySetResult());

        return tcs.Task;
    }
}

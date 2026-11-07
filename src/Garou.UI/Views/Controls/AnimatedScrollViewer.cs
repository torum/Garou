using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Garou.Views.Controls;

public sealed class AnimatedScrollViewer : ScrollViewer
{
    public static readonly StyledProperty<TimeSpan> DurationProperty =
        AvaloniaProperty.Register<AnimatedScrollViewer, TimeSpan>(nameof(Duration), TimeSpan.FromMilliseconds(400));

    public static readonly StyledProperty<double> ScrollMultiplierProperty =
        AvaloniaProperty.Register<AnimatedScrollViewer, double>(nameof(ScrollMultiplier), 1.0);

    private double _targetOffset;
    private CancellationTokenSource? _scrollAnimationCancellation;

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
        var cancellation = _scrollAnimationCancellation;
        _scrollAnimationCancellation = null;
        cancellation?.Cancel();
        base.OnDetachedFromVisualTree(e);
    }

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
}

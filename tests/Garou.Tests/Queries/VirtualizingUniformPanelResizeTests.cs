using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Garou.Views.Controls;

namespace Garou.Tests.Queries;

[Collection("Avalonia UI")]
public sealed class VirtualizingUniformPanelResizeTests
{
    [AvaloniaTheory]
    [InlineData(600, 300)]
    [InlineData(300, 600)]
    public async Task ResizingWidthKeepsCenterItemInPlace(double initialWidth, double newWidth)
    {
        var (window, list, items) = CreateWindow(initialWidth, 300);
        try
        {
            window.Show();
            await SettleAsync(window);
            var panel = list.GetVisualDescendants().OfType<VirtualizingUniformPanel>().Single();
            var viewer = list.GetVisualDescendants().OfType<ScrollViewer>().First();
            viewer.Offset = new Vector(0, 2500);
            await SettleAsync(window);

            var columns = (int)(panel.Bounds.Width / panel.ItemWidth);
            var row = (int)((viewer.Offset.Y + viewer.Viewport.Height / 2) / panel.ItemHeight);
            var index = row * columns + columns / 2;
            var centerWithinRow = (viewer.Offset.Y + viewer.Viewport.Height / 2) / panel.ItemHeight - row;
            var item = items[index];

            window.Width = newWidth;
            await SettleAsync(window);

            columns = (int)(panel.Bounds.Width / panel.ItemWidth);
            var itemTop = (index / columns) * panel.ItemHeight;
            Assert.Same(item, items[index]);
            Assert.InRange(itemTop + centerWithinRow * panel.ItemHeight - viewer.Offset.Y,
                viewer.Viewport.Height / 2 - 2, viewer.Viewport.Height / 2 + 2);
            Assert.True(itemTop < viewer.Offset.Y + viewer.Viewport.Height && itemTop + panel.ItemHeight > viewer.Offset.Y);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ResizeAtTopDoesNotScrollDown()
    {
        var (window, list, _) = CreateWindow(600, 300);
        try
        {
            window.Show();
            await SettleAsync(window);
            window.Width = 300;
            await SettleAsync(window);
            var viewer = list.GetVisualDescendants().OfType<ScrollViewer>().First();
            Assert.Equal(0, viewer.Offset.Y);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ResizingHeightKeepsCenterItemInPlace()
    {
        var (window, list, _) = CreateWindow(500, 400);
        try
        {
            window.Show();
            await SettleAsync(window);
            var viewer = list.GetVisualDescendants().OfType<ScrollViewer>().First();
            viewer.Offset = new Vector(0, 2500);
            await SettleAsync(window);
            var center = viewer.Offset.Y + viewer.Viewport.Height / 2;

            window.Height = 200;
            await SettleAsync(window);

            Assert.InRange(viewer.Offset.Y + viewer.Viewport.Height / 2, center - 2, center + 2);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ConsecutiveResizesKeepTheOriginalCenterItemVisible()
    {
        var (window, list, items) = CreateWindow(300, 300);
        try
        {
            window.Show();
            await SettleAsync(window);
            var panel = list.GetVisualDescendants().OfType<VirtualizingUniformPanel>().Single();
            var viewer = list.GetVisualDescendants().OfType<ScrollViewer>().First();
            viewer.Offset = new Vector(0, 5000);
            await SettleAsync(window);
            var columns = (int)(panel.Bounds.Width / panel.ItemWidth);
            var index = Math.Min(items.Count - 1,
                (int)((viewer.Offset.Y + viewer.Viewport.Height / 2) / panel.ItemHeight) * columns + columns / 2);

            window.Width = 800;
            window.UpdateLayout();
            window.Width = 400;
            await SettleAsync(window);

            columns = (int)(panel.Bounds.Width / panel.ItemWidth);
            var itemTop = index / columns * panel.ItemHeight;
            Assert.True(itemTop < viewer.Offset.Y + viewer.Viewport.Height && itemTop + panel.ItemHeight > viewer.Offset.Y);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ChangingTileSizeKeepsCenterItemVisible()
    {
        var (window, list, items) = CreateWindow(600, 300);
        try
        {
            window.Show();
            await SettleAsync(window);
            var panel = list.GetVisualDescendants().OfType<VirtualizingUniformPanel>().Single();
            var viewer = list.GetVisualDescendants().OfType<ScrollViewer>().First();
            viewer.Offset = new Vector(0, 2500);
            await SettleAsync(window);
            var columns = (int)(panel.Bounds.Width / panel.ItemWidth);
            var index = Math.Min(items.Count - 1,
                (int)((viewer.Offset.Y + viewer.Viewport.Height / 2) / panel.ItemHeight) * columns + columns / 2);

            panel.ItemWidth = 150;
            panel.ItemHeight = 150;
            await SettleAsync(window);

            columns = (int)(panel.Bounds.Width / panel.ItemWidth);
            var itemTop = index / columns * panel.ItemHeight;
            Assert.True(itemTop < viewer.Offset.Y + viewer.Viewport.Height && itemTop + panel.ItemHeight > viewer.Offset.Y);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ResizingNearEndClampsOffsetAndKeepsAnchorVisible()
    {
        var (window, list, items) = CreateWindow(300, 300);
        try
        {
            window.Show();
            await SettleAsync(window);
            var panel = list.GetVisualDescendants().OfType<VirtualizingUniformPanel>().Single();
            var viewer = list.GetVisualDescendants().OfType<ScrollViewer>().First();
            viewer.Offset = new Vector(0, viewer.Extent.Height - viewer.Viewport.Height);
            await SettleAsync(window);
            var columns = (int)(panel.Bounds.Width / panel.ItemWidth);
            var index = Math.Min(items.Count - 1,
                (int)((viewer.Offset.Y + viewer.Viewport.Height / 2) / panel.ItemHeight) * columns + columns / 2);

            window.Width = 800;
            await SettleAsync(window);

            columns = (int)(panel.Bounds.Width / panel.ItemWidth);
            var itemTop = index / columns * panel.ItemHeight;
            Assert.InRange(viewer.Offset.Y, 0, Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height));
            Assert.True(itemTop < viewer.Offset.Y + viewer.Viewport.Height && itemTop + panel.ItemHeight > viewer.Offset.Y);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ExplicitScrollWinsOverPendingResizeRestore()
    {
        var (window, list, _) = CreateWindow(600, 300);
        try
        {
            window.Show();
            await SettleAsync(window);
            var viewer = list.GetVisualDescendants().OfType<ScrollViewer>().First();
            viewer.Offset = new Vector(0, 2500);
            await SettleAsync(window);

            window.Width = 300;
            window.UpdateLayout();
            viewer.Offset = new Vector(0, 0);
            await SettleAsync(window);

            Assert.Equal(0, viewer.Offset.Y);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task CollectionResetCancelsPendingResizeRestore()
    {
        var (window, list, items) = CreateWindow(600, 300);
        try
        {
            window.Show();
            await SettleAsync(window);
            var viewer = list.GetVisualDescendants().OfType<ScrollViewer>().First();
            viewer.Offset = new Vector(0, 2500);
            await SettleAsync(window);

            window.Width = 300;
            window.UpdateLayout();
            items.Clear();
            for (var i = 0; i < 200; i++) items.Add(new object());
            viewer.Offset = new Vector(0, 0);
            await SettleAsync(window);

            Assert.Equal(0, viewer.Offset.Y);
        }
        finally
        {
            window.Close();
        }
    }

    private static (Window Window, ListBox List, ObservableCollection<object> Items) CreateWindow(double width, double height)
    {
        var items = new ObservableCollection<object>(Enumerable.Range(0, 200).Select(_ => new object()));
        var list = new ListBox
        {
            ItemsSource = items,
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingUniformPanel { ItemWidth = 100, ItemHeight = 100 })
        };
        return (new Window { Content = list, Width = width, Height = height }, list, items);
    }

    private static async Task SettleAsync(Window window)
    {
        window.UpdateLayout();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        window.UpdateLayout();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        window.UpdateLayout();
    }
}

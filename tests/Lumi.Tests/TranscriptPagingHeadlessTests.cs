using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.ViewModels;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class TranscriptPagingHeadlessTests
{
    private sealed class VisualTranscriptItem : TranscriptItem
    {
        public VisualTranscriptItem(string stableId, double desiredHeight, string text)
            : base(stableId)
        {
            DesiredHeight = desiredHeight;
            Text = text;
        }

        public double DesiredHeight { get; }
        public string Text { get; }
    }

    private readonly record struct Anchor(string StableId, double ViewportY);

    [Fact]
    public async Task OpeningLongTranscript_RealizesOnlyMountedTurns()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp), AvaloniaTestIsolationLevel.PerTest);

        await session.Dispatch(async () =>
        {
            TranscriptTurnControl.ResetDiagnostics();
            var controller = new TranscriptWindowController(new TranscriptPagingOptions
            {
                MaxPageWeight = 4,
                MaxTurnsPerPage = 2,
                MinInitialPages = 2,
                MaxMountedPages = 4,
            });
            var source = CreateVisualTurns(80);
            controller.BindTranscript(source, "ui-open");
            controller.ResetToLatest(260, "ui-open");

            var (window, shell, _) = await CreateHostAsync(controller);
            try
            {
                shell.ResetAutoScroll();
                shell.ScrollToEnd();
                await PumpAsync();
                _ = window.CaptureRenderedFrame();

                var realizedTurnCount = window.GetVisualDescendants().OfType<TranscriptTurnControl>().Count();
                var diagnostics = TranscriptTurnControl.CaptureDiagnostics();

                Assert.True(realizedTurnCount > 0);
                Assert.True(realizedTurnCount < source.Count);
                Assert.Equal(controller.MountedTurns.Count, realizedTurnCount);
                Assert.True(diagnostics.ControlCreateCount < source.Count);
                Assert.True(shell.VerticalOffset > 0);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task PrependingOlderPage_RestoresVisibleAnchor()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp), AvaloniaTestIsolationLevel.PerTest);

        await session.Dispatch(async () =>
        {
            var controller = new TranscriptWindowController(new TranscriptPagingOptions
            {
                MaxPageWeight = 4,
                MaxTurnsPerPage = 2,
                MinInitialPages = 2,
                MaxMountedPages = 5,
                PrependTriggerPixels = 120,
            });
            var source = CreateVisualTurns(24);
            controller.BindTranscript(source, "ui-prepend");
            controller.ResetToLatest(220, "ui-prepend");

            var (window, shell, scrollViewer) = await CreateHostAsync(controller);
            try
            {
                shell.ResetAutoScroll();
                shell.ScrollToEnd();
                await PumpAsync();

                shell.ScrollToVerticalOffset(0);
                await PumpAsync();

                controller.UpdatePinnedState(false, shell.CurrentDistanceFromBottom, "ui-prepend");
                var anchor = CaptureAnchor(window, scrollViewer);
                Assert.NotNull(anchor);

                var mutation = controller.UpdateViewport(
                    new TranscriptViewportState(
                        shell.VerticalOffset,
                        shell.ViewportHeight,
                        shell.ExtentHeight,
                        shell.IsPinnedToBottom,
                        shell.CurrentDistanceFromBottom),
                    "ui-prepend");

                Assert.Equal(TranscriptWindowMutationKind.Prepend, mutation.Kind);
                await PumpAsync();
                RestoreAnchor(window, shell, scrollViewer, anchor!.Value);
                await PumpAsync();

                var restored = CaptureAnchor(window, scrollViewer);
                Assert.NotNull(restored);
                Assert.Equal(anchor.Value.StableId, restored!.Value.StableId);
                Assert.InRange(Math.Abs(restored.Value.ViewportY - anchor.Value.ViewportY), 0, 1.5);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task DetachedTurnControl_DoesNotProcessChangesUntilReattached()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp), AvaloniaTestIsolationLevel.PerTest);

        await session.Dispatch(async () =>
        {
            var turn = new TranscriptTurn("turn:0000");
            turn.Items.Add(new VisualTranscriptItem("item:0000", 72, "One"));

            var control = new TranscriptTurnControl { Turn = turn };
            var host = new StackPanel();
            host.Children.Add(control);

            var window = new Window
            {
                Width = 480,
                Height = 320,
                Content = host,
            };
            window.DataTemplates.Add(new FuncDataTemplate<VisualTranscriptItem>((item, _) => new Border
            {
                Height = item.DesiredHeight,
                Child = new TextBlock { Text = item.Text },
            }));

            window.Show();
            await PumpAsync();
            Assert.Equal(1, GetHostedItemCount(control));

            host.Children.Clear();
            await PumpAsync();

            turn.Items.Add(new VisualTranscriptItem("item:0001", 72, "Two"));
            Assert.Equal(1, GetHostedItemCount(control));

            host.Children.Add(control);
            await PumpAsync();
            Assert.Equal(2, GetHostedItemCount(control));

            turn.Items.Add(new VisualTranscriptItem("item:0002", 72, "Three"));
            Assert.Equal(3, GetHostedItemCount(control));

            window.Close();
        }, CancellationToken.None);
    }

    private static async Task<(Window Window, StrataChatShell Shell, ScrollViewer ScrollViewer)> CreateHostAsync(TranscriptWindowController controller)
    {
        var transcript = new ItemsControl
        {
            ItemsSource = controller.MountedTurns,
            ItemTemplate = new FuncDataTemplate<TranscriptTurn>((turn, _) => new TranscriptTurnControl { Turn = turn }),
        };
        transcript.ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Spacing = 8 });

        var shell = new StrataChatShell
        {
            Header = new TextBlock { Text = "Transcript Test" },
            Transcript = transcript,
            Composer = new Border { Height = 48 },
        };

        var window = new Window
        {
            Width = 900,
            Height = 700,
            Content = shell,
        };
        window.DataTemplates.Add(new FuncDataTemplate<VisualTranscriptItem>((item, _) => new Border
        {
            Height = item.DesiredHeight,
            Padding = new Thickness(12, 8),
            Child = new TextBlock { Text = item.Text },
        }));

        window.Show();
        await PumpAsync();
        await PumpAsync();

        var scrollViewer = shell.TranscriptScrollViewer;
        Assert.NotNull(scrollViewer);
        return (window, shell, scrollViewer!);
    }

    private static ObservableCollection<TranscriptTurn> CreateVisualTurns(int count)
    {
        return new ObservableCollection<TranscriptTurn>(
            Enumerable.Range(0, count)
                .Select(index =>
                {
                    var turn = new TranscriptTurn($"turn:{index:D4}");
                    var height = 72 + (index % 5) * 28;
                    turn.Items.Add(new VisualTranscriptItem($"item:{index:D4}", height, $"Turn {index}"));
                    return turn;
                })
                .ToArray());
    }

    private static Anchor? CaptureAnchor(Visual root, ScrollViewer scrollViewer)
    {
        foreach (var control in root.GetVisualDescendants().OfType<TranscriptTurnControl>())
        {
            if (control.Turn is null)
                continue;

            var point = control.TranslatePoint(default, scrollViewer);
            if (point is null)
                continue;

            if (point.Value.Y + control.Bounds.Height < 0)
                continue;

            return new Anchor(control.Turn.StableId, point.Value.Y);
        }

        return null;
    }

    private static void RestoreAnchor(Visual root, StrataChatShell shell, ScrollViewer scrollViewer, Anchor anchor)
    {
        var control = root.GetVisualDescendants()
            .OfType<TranscriptTurnControl>()
            .FirstOrDefault(candidate => candidate.Turn?.StableId == anchor.StableId);
        Assert.NotNull(control);

        var point = control!.TranslatePoint(default, scrollViewer);
        Assert.NotNull(point);

        var delta = point!.Value.Y - anchor.ViewportY;
        shell.ScrollToVerticalOffset(shell.VerticalOffset + delta);
    }

    private static async Task PumpAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
    }

    private static int GetHostedItemCount(TranscriptTurnControl control)
    {
        return Assert.IsType<StackPanel>(control.Content).Children.Count;
    }
}

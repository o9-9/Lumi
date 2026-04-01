using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class StrataChatShellScrollTests
{
    [Fact]
    public async Task SmallUserScrollAway_DisablesFollowModeUntilJumpToLatest()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp), AvaloniaTestIsolationLevel.PerTest);

        await session.Dispatch(async () =>
        {
            var transcript = new StackPanel { Spacing = 8 };
            for (var i = 0; i < 48; i++)
            {
                transcript.Children.Add(new Border
                {
                    Height = 56,
                    Child = new TextBlock { Text = $"Turn {i}" }
                });
            }

            var shell = new StrataChatShell
            {
                Header = new TextBlock { Text = "Scroll Test" },
                Transcript = transcript,
                Composer = new Border { Height = 48 }
            };

            var window = new Window
            {
                Width = 720,
                Height = 520,
                Content = shell,
            };

            window.Show();
            await PumpAsync();
            await PumpAsync();

            var scrollViewer = Assert.IsType<ScrollViewer>(shell.TranscriptScrollViewer);

            shell.JumpToLatest();
            await PumpAsync();

            var bottomOffset = scrollViewer.Offset.Y;
            Assert.True(bottomOffset > 0);
            Assert.True(shell.IsFollowingTail);
            Assert.True(shell.IsPinnedToBottom);

            scrollViewer.Offset = scrollViewer.Offset.WithY(Math.Max(0, bottomOffset - 24));
            await PumpAsync();

            Assert.False(shell.IsFollowingTail);
            Assert.False(shell.IsPinnedToBottom);
            var readerOffset = scrollViewer.Offset.Y;

            transcript.Children.Add(new Border
            {
                Height = 56,
                Child = new TextBlock { Text = "Newest turn" }
            });
            await PumpAsync();

            Assert.True(shell.HasNewContent);

            shell.ScrollToEnd();
            await PumpAsync();

            Assert.InRange(Math.Abs(scrollViewer.Offset.Y - readerOffset), 0, 1.5);

            shell.JumpToLatest();
            await PumpAsync();

            Assert.True(shell.IsFollowingTail);
            Assert.True(shell.IsPinnedToBottom);
            Assert.False(shell.HasNewContent);
            Assert.True(scrollViewer.Offset.Y > readerOffset + 10);

            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ProgrammaticBottomLanding_DoesNotReenterFollowMode()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp), AvaloniaTestIsolationLevel.PerTest);

        await session.Dispatch(async () =>
        {
            var transcript = new StackPanel { Spacing = 8 };
            for (var i = 0; i < 48; i++)
            {
                transcript.Children.Add(new Border
                {
                    Height = 56,
                    Child = new TextBlock { Text = $"Turn {i}" }
                });
            }

            var shell = new StrataChatShell
            {
                Header = new TextBlock { Text = "Scroll Test" },
                Transcript = transcript,
                Composer = new Border { Height = 48 }
            };

            var window = new Window
            {
                Width = 720,
                Height = 520,
                Content = shell,
            };

            window.Show();
            await PumpAsync();
            await PumpAsync();

            var scrollViewer = Assert.IsType<ScrollViewer>(shell.TranscriptScrollViewer);

            shell.JumpToLatest();
            await PumpAsync();

            var bottomOffset = scrollViewer.Offset.Y;
            scrollViewer.Offset = scrollViewer.Offset.WithY(Math.Max(0, bottomOffset - 24));
            await PumpAsync();

            Assert.False(shell.IsFollowingTail);
            Assert.False(shell.IsPinnedToBottom);

            transcript.Children.Add(new Border
            {
                Height = 56,
                Child = new TextBlock { Text = "Newest turn" }
            });
            await PumpAsync();

            Assert.True(shell.HasNewContent);

            var maxOffset = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
            shell.ScrollToVerticalOffset(maxOffset);
            await PumpAsync();

            Assert.False(shell.IsFollowingTail);
            Assert.False(shell.IsPinnedToBottom);
            Assert.True(shell.HasNewContent);

            shell.JumpToLatest();
            await PumpAsync();

            Assert.True(shell.IsFollowingTail);
            Assert.True(shell.IsPinnedToBottom);
            Assert.False(shell.HasNewContent);

            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ManualReturnToBottom_ReentersFollowMode()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp), AvaloniaTestIsolationLevel.PerTest);

        await session.Dispatch(async () =>
        {
            var transcript = new StackPanel { Spacing = 8 };
            for (var i = 0; i < 64; i++)
            {
                transcript.Children.Add(new Border
                {
                    Height = 56,
                    Child = new TextBlock { Text = $"Turn {i}" }
                });
            }

            var shell = new StrataChatShell
            {
                Header = new TextBlock { Text = "Scroll Test" },
                Transcript = transcript,
                Composer = new Border { Height = 48 }
            };

            var window = new Window
            {
                Width = 720,
                Height = 520,
                Content = shell,
            };

            window.Show();
            await PumpAsync();
            await PumpAsync();

            var scrollViewer = Assert.IsType<ScrollViewer>(shell.TranscriptScrollViewer);

            shell.JumpToLatest();
            await PumpAsync();

            var bottomOffset = scrollViewer.Offset.Y;
            scrollViewer.Offset = scrollViewer.Offset.WithY(Math.Max(0, bottomOffset - 320));
            await PumpAsync();

            Assert.False(shell.IsFollowingTail);
            Assert.False(shell.IsPinnedToBottom);

            transcript.Children.Add(new Border
            {
                Height = 56,
                Child = new TextBlock { Text = "Newest turn" }
            });
            await PumpAsync();

            Assert.True(shell.HasNewContent);

            var maxOffset = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
            scrollViewer.Offset = scrollViewer.Offset.WithY(maxOffset);
            await PumpAsync();

            Assert.True(shell.IsFollowingTail);
            Assert.True(shell.IsPinnedToBottom);
            Assert.False(shell.HasNewContent);

            window.Close();
        }, CancellationToken.None);
    }

    private static async Task PumpAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
}

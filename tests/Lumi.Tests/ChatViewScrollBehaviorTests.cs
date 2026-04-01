using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class ChatViewScrollBehaviorTests
{
    [Fact]
    public async Task CurrentChatMetadataRefresh_DoesNotReenterFollowMode()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp), AvaloniaTestIsolationLevel.PerTest);

        await session.Dispatch(async () =>
        {
            var chat = CreateLongChat();
            var data = new AppData
            {
                Settings = new UserSettings
                {
                    AutoSaveChats = false,
                    EnableMemoryAutoSave = false
                }
            };
            data.Chats.Add(chat);

            var viewModel = new ChatViewModel(new DataStore(data), new CopilotService());
            var view = new ChatView { DataContext = viewModel };
            var window = new Window
            {
                Width = 1100,
                Height = 820,
                Content = view,
            };

            window.Show();
            try
            {
                await PumpAsync();
                await PumpAsync();

                await viewModel.LoadChatAsync(chat);
                await WaitUntilAsync(() => view.FindControl<StrataChatShell>("ChatShell")?.TranscriptScrollViewer is not null);

                var shell = Assert.IsType<StrataChatShell>(view.FindControl<StrataChatShell>("ChatShell"));
                var scrollViewer = Assert.IsType<ScrollViewer>(shell.TranscriptScrollViewer);

                shell.JumpToLatest();
                await PumpAsync();

                Assert.True(scrollViewer.Extent.Height > scrollViewer.Viewport.Height);

                var bottomOffset = scrollViewer.Offset.Y;
                scrollViewer.Offset = scrollViewer.Offset.WithY(Math.Max(0, bottomOffset - 220));
                await PumpAsync();

                Assert.False(shell.IsFollowingTail);
                var readerOffset = scrollViewer.Offset.Y;

                viewModel.SetProjectId(Guid.NewGuid());
                await PumpAsync();
                await PumpAsync();

                Assert.False(shell.IsFollowingTail);
                Assert.InRange(Math.Abs(scrollViewer.Offset.Y - readerOffset), 0, 1.5);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    private static Chat CreateLongChat()
    {
        var chat = new Chat { Title = "Scroll regression" };
        for (var i = 0; i < 18; i++)
        {
            chat.Messages.Add(new ChatMessage
            {
                Role = "user",
                Content = $"Question {i}: " + new string('q', 160)
            });
            chat.Messages.Add(new ChatMessage
            {
                Role = "assistant",
                Content = $"Answer {i}: " + new string('a', 280)
            });
        }

        return chat;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;

            await PumpAsync();
            await Task.Delay(20);
        }

        Assert.True(condition(), "Timed out waiting for the chat view to finish loading.");
    }

    private static async Task PumpAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
}

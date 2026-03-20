using System.Reflection;
using System.Collections.Generic;
using Lumi.Models;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public class AppDataSnapshotFactoryTests
{
    [Fact]
    public void MergeChatIndexChanges_PreservesPersistedChat_WhenLocalChatWasNotMarkedDirty()
    {
        var chatId = Guid.NewGuid();
        var currentSnapshot = new AppData
        {
            Chats =
            [
                CreateChat(
                    chatId,
                    title: "Stale local",
                    copilotSessionId: "session-live",
                    updatedAt: new DateTimeOffset(2026, 3, 20, 11, 0, 0, TimeSpan.Zero))
            ]
        };
        var persistedSnapshot = new AppData
        {
            Chats =
            [
                CreateChat(
                    chatId,
                    title: "Recovered persisted",
                    copilotSessionId: null,
                    updatedAt: new DateTimeOffset(2026, 3, 20, 11, 5, 0, TimeSpan.Zero))
            ]
        };

        var merged = InvokeMergeChatIndexChanges(currentSnapshot, persistedSnapshot, [], []);

        var chat = Assert.Single(merged.Chats);
        Assert.Equal("Recovered persisted", chat.Title);
        Assert.Null(chat.CopilotSessionId);
        Assert.Equal(persistedSnapshot.Chats[0].UpdatedAt, chat.UpdatedAt);
    }

    [Fact]
    public void MergeChatIndexChanges_PreservesDirtyLocalChat_WhenItIsNewerThanPersisted()
    {
        var chatId = Guid.NewGuid();
        var currentSnapshot = new AppData
        {
            Chats =
            [
                CreateChat(
                    chatId,
                    title: "Recovered locally",
                    copilotSessionId: null,
                    updatedAt: new DateTimeOffset(2026, 3, 20, 11, 5, 0, TimeSpan.Zero),
                    skillIds: [Guid.NewGuid()],
                    mcpServers: ["new-mcp"],
                    lastModelUsed: "gpt-5.4",
                    totalInputTokens: 100,
                    totalOutputTokens: 200,
                    planContent: "updated plan")
            ]
        };
        var persistedSnapshot = new AppData
        {
            Chats =
            [
                CreateChat(
                    chatId,
                    title: "Stale persisted",
                    copilotSessionId: "stale-session",
                    updatedAt: new DateTimeOffset(2026, 3, 20, 11, 0, 0, TimeSpan.Zero),
                    skillIds: [Guid.NewGuid()],
                    mcpServers: ["old-mcp"],
                    lastModelUsed: "claude-opus-4.6-1m",
                    totalInputTokens: 10,
                    totalOutputTokens: 20,
                    planContent: "stale plan")
            ]
        };

        var merged = InvokeMergeChatIndexChanges(currentSnapshot, persistedSnapshot, [chatId], []);

        var chat = Assert.Single(merged.Chats);
        Assert.Equal("Recovered locally", chat.Title);
        Assert.Null(chat.CopilotSessionId);
        Assert.Equal(currentSnapshot.Chats[0].UpdatedAt, chat.UpdatedAt);
        Assert.Equal(currentSnapshot.Chats[0].ActiveSkillIds, chat.ActiveSkillIds);
        Assert.Equal(currentSnapshot.Chats[0].ActiveMcpServerNames, chat.ActiveMcpServerNames);
        Assert.Equal("gpt-5.4", chat.LastModelUsed);
        Assert.Equal(100, chat.TotalInputTokens);
        Assert.Equal(200, chat.TotalOutputTokens);
        Assert.Equal("updated plan", chat.PlanContent);
    }

    [Fact]
    public void MergeChatIndexChanges_KeepsNewerPersistedChat_WhenDirtyLocalChatIsOlder()
    {
        var chatId = Guid.NewGuid();
        var currentSnapshot = new AppData
        {
            Chats =
            [
                CreateChat(
                    chatId,
                    title: "Older local",
                    copilotSessionId: "stale-session",
                    updatedAt: new DateTimeOffset(2026, 3, 20, 11, 0, 0, TimeSpan.Zero),
                    totalInputTokens: 10,
                    totalOutputTokens: 20)
            ]
        };
        var persistedSnapshot = new AppData
        {
            Chats =
            [
                CreateChat(
                    chatId,
                    title: "Recovered persisted",
                    copilotSessionId: null,
                    updatedAt: new DateTimeOffset(2026, 3, 20, 11, 5, 0, TimeSpan.Zero),
                    totalInputTokens: 100,
                    totalOutputTokens: 200)
            ]
        };

        var merged = InvokeMergeChatIndexChanges(currentSnapshot, persistedSnapshot, [chatId], []);

        var chat = Assert.Single(merged.Chats);
        Assert.Equal("Recovered persisted", chat.Title);
        Assert.Null(chat.CopilotSessionId);
        Assert.Equal(persistedSnapshot.Chats[0].UpdatedAt, chat.UpdatedAt);
        Assert.Equal(100, chat.TotalInputTokens);
        Assert.Equal(200, chat.TotalOutputTokens);
    }

    [Fact]
    public void MergeChatIndexChanges_RemovesDeletedChats()
    {
        var chatId = Guid.NewGuid();
        var currentSnapshot = new AppData
        {
            Chats =
            [
                CreateChat(chatId, title: "Deleted locally", copilotSessionId: null, updatedAt: new DateTimeOffset(2026, 3, 20, 11, 5, 0, TimeSpan.Zero))
            ]
        };
        var persistedSnapshot = new AppData
        {
            Chats =
            [
                CreateChat(chatId, title: "Still on disk", copilotSessionId: "session-live", updatedAt: new DateTimeOffset(2026, 3, 20, 11, 0, 0, TimeSpan.Zero))
            ]
        };

        var merged = InvokeMergeChatIndexChanges(currentSnapshot, persistedSnapshot, [], [chatId]);

        Assert.Empty(merged.Chats);
    }

    [Fact]
    public void MergeChatIndexChanges_PreservesNewDirtyLocalChats()
    {
        var newChatId = Guid.NewGuid();
        var currentSnapshot = new AppData
        {
            Chats =
            [
                CreateChat(newChatId, title: "Brand new", copilotSessionId: "new-session", updatedAt: new DateTimeOffset(2026, 3, 20, 11, 5, 0, TimeSpan.Zero))
            ]
        };
        var persistedSnapshot = new AppData();

        var merged = InvokeMergeChatIndexChanges(currentSnapshot, persistedSnapshot, [newChatId], []);

        var chat = Assert.Single(merged.Chats);
        Assert.Equal(newChatId, chat.Id);
        Assert.Equal("Brand new", chat.Title);
    }

    [Fact]
    public void MergeChatIndexChanges_PreventsRecoveredShutdownFromBeingRevertedByStaleSnapshot()
    {
        var chatId = Guid.NewGuid();
        var staleCurrentSnapshot = new AppData
        {
            Chats =
            [
                CreateChat(
                    chatId,
                    title: "Anniversary chat",
                    copilotSessionId: "old-session",
                    updatedAt: new DateTimeOffset(2026, 3, 20, 13, 49, 39, TimeSpan.Zero),
                    totalInputTokens: 10,
                    totalOutputTokens: 20)
            ]
        };
        var recoveredPersistedSnapshot = new AppData
        {
            Chats =
            [
                CreateChat(
                    chatId,
                    title: "Anniversary chat",
                    copilotSessionId: null,
                    updatedAt: new DateTimeOffset(2026, 3, 20, 13, 49, 47, TimeSpan.Zero),
                    totalInputTokens: 100,
                    totalOutputTokens: 200)
            ]
        };

        var merged = InvokeMergeChatIndexChanges(staleCurrentSnapshot, recoveredPersistedSnapshot, [], []);

        var chat = Assert.Single(merged.Chats);
        Assert.Null(chat.CopilotSessionId);
        Assert.Equal(recoveredPersistedSnapshot.Chats[0].UpdatedAt, chat.UpdatedAt);
        Assert.Equal(100, chat.TotalInputTokens);
        Assert.Equal(200, chat.TotalOutputTokens);
    }

    private static Chat CreateChat(
        Guid id,
        string title,
        string? copilotSessionId,
        DateTimeOffset updatedAt,
        List<Guid>? skillIds = null,
        List<string>? mcpServers = null,
        string? lastModelUsed = null,
        long totalInputTokens = 0,
        long totalOutputTokens = 0,
        string? planContent = null)
    {
        return new Chat
        {
            Id = id,
            Title = title,
            CopilotSessionId = copilotSessionId,
            UpdatedAt = updatedAt,
            ActiveSkillIds = skillIds ?? [],
            ActiveMcpServerNames = mcpServers ?? [],
            LastModelUsed = lastModelUsed,
            TotalInputTokens = totalInputTokens,
            TotalOutputTokens = totalOutputTokens,
            PlanContent = planContent
        };
    }

    private static AppData InvokeMergeChatIndexChanges(
        AppData currentSnapshot,
        AppData persistedSnapshot,
        IReadOnlyCollection<Guid> dirtyChatIds,
        IReadOnlyCollection<Guid> deletedChatIds)
    {
        var factoryType = typeof(DataStore).Assembly.GetType("Lumi.Services.AppDataSnapshotFactory")
            ?? throw new InvalidOperationException("AppDataSnapshotFactory type was not found.");
        var mergeMethod = factoryType.GetMethod(
            "MergeChatIndexChanges",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException("MergeChatIndexChanges method was not found.");
        return (AppData)(mergeMethod.Invoke(
            null,
            [currentSnapshot, persistedSnapshot, new HashSet<Guid>(dirtyChatIds), new HashSet<Guid>(deletedChatIds)])
            ?? throw new InvalidOperationException("MergeChatIndexChanges returned null."));
    }
}

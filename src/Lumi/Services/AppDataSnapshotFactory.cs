using System.Collections.Generic;
using System.Linq;
using Lumi.Models;

namespace Lumi.Services;

internal static class AppDataSnapshotFactory
{
    public static AppData CreateIndexSnapshot(AppData source)
    {
        var settings = source.Settings;

        return new AppData
        {
            Settings = new UserSettings
            {
                UserName = settings.UserName,
                UserSex = settings.UserSex,
                IsOnboarded = settings.IsOnboarded,
                DefaultsSeeded = settings.DefaultsSeeded,
                CodingLumiSeeded = settings.CodingLumiSeeded,
                Language = settings.Language,
                LaunchAtStartup = settings.LaunchAtStartup,
                StartMinimized = settings.StartMinimized,
                MinimizeToTray = settings.MinimizeToTray,
                GlobalHotkey = settings.GlobalHotkey,
                NotificationsEnabled = settings.NotificationsEnabled,
                IsDarkTheme = settings.IsDarkTheme,
                IsCompactDensity = settings.IsCompactDensity,
                FontSize = settings.FontSize,
                ShowAnimations = settings.ShowAnimations,
                SendWithEnter = settings.SendWithEnter,
                ShowTimestamps = settings.ShowTimestamps,
                ShowToolCalls = settings.ShowToolCalls,
                ShowReasoning = settings.ShowReasoning,
                AutoGenerateTitles = settings.AutoGenerateTitles,
                PreferredModel = settings.PreferredModel,
                EnableMemoryAutoSave = settings.EnableMemoryAutoSave,
                AutoSaveChats = settings.AutoSaveChats,
                HasImportedBrowserCookies = settings.HasImportedBrowserCookies,
            },
            Chats = source.Chats
                .Select(CloneChatIndex)
                .ToList(),
            Projects = source.Projects
                .Select(static p => new Project
                {
                    Id = p.Id,
                    Name = p.Name,
                    Instructions = p.Instructions,
                    WorkingDirectory = p.WorkingDirectory,
                    CreatedAt = p.CreatedAt
                })
                .ToList(),
            Skills = source.Skills
                .Select(static s => new Skill
                {
                    Id = s.Id,
                    Name = s.Name,
                    Description = s.Description,
                    Content = s.Content,
                    IconGlyph = s.IconGlyph,
                    IsBuiltIn = s.IsBuiltIn,
                    CreatedAt = s.CreatedAt
                })
                .ToList(),
            Agents = source.Agents
                .Select(static a => new LumiAgent
                {
                    Id = a.Id,
                    Name = a.Name,
                    Description = a.Description,
                    SystemPrompt = a.SystemPrompt,
                    IconGlyph = a.IconGlyph,
                    IsBuiltIn = a.IsBuiltIn,
                    IsLearningAgent = a.IsLearningAgent,
                    SkillIds = [..a.SkillIds],
                    ToolNames = [..a.ToolNames],
                    McpServerIds = [..a.McpServerIds],
                    CreatedAt = a.CreatedAt
                })
                .ToList(),
            McpServers = source.McpServers
                .Select(static s => new McpServer
                {
                    Id = s.Id,
                    Name = s.Name,
                    Description = s.Description,
                    ServerType = s.ServerType,
                    Command = s.Command,
                    Args = [..s.Args],
                    Env = new(s.Env),
                    Url = s.Url,
                    Headers = new(s.Headers),
                    Tools = [..s.Tools],
                    Timeout = s.Timeout,
                    IsEnabled = s.IsEnabled,
                    CreatedAt = s.CreatedAt
                })
                .ToList(),
            Memories = source.Memories
                .Select(static m => new Memory
                {
                    Id = m.Id,
                    Key = m.Key,
                    Content = m.Content,
                    Category = m.Category,
                    Source = m.Source,
                    SourceChatId = m.SourceChatId,
                    CreatedAt = m.CreatedAt,
                    UpdatedAt = m.UpdatedAt
                })
                .ToList(),
        };
    }

    public static AppData MergeChatIndexChanges(
        AppData currentSnapshot,
        AppData persistedSnapshot,
        ISet<Guid> dirtyChatIds,
        ISet<Guid> deletedChatIds)
    {
        if (currentSnapshot.Chats.Count == 0 && persistedSnapshot.Chats.Count == 0)
            return currentSnapshot;

        var currentChatsById = currentSnapshot.Chats.ToDictionary(static c => c.Id);
        var persistedChatIds = new HashSet<Guid>();
        var mergedChats = new List<Chat>(Math.Max(currentSnapshot.Chats.Count, persistedSnapshot.Chats.Count));

        foreach (var persistedChat in persistedSnapshot.Chats)
        {
            persistedChatIds.Add(persistedChat.Id);

            if (deletedChatIds.Contains(persistedChat.Id))
                continue;

            if (dirtyChatIds.Contains(persistedChat.Id)
                && currentChatsById.TryGetValue(persistedChat.Id, out var currentChat)
                && currentChat.UpdatedAt >= persistedChat.UpdatedAt)
            {
                mergedChats.Add(CloneChatIndex(currentChat));
                continue;
            }

            mergedChats.Add(CloneChatIndex(persistedChat));
        }

        foreach (var currentChat in currentSnapshot.Chats)
        {
            if (deletedChatIds.Contains(currentChat.Id)
                || persistedChatIds.Contains(currentChat.Id)
                || !dirtyChatIds.Contains(currentChat.Id))
            {
                continue;
            }

            mergedChats.Add(CloneChatIndex(currentChat));
        }

        currentSnapshot.Chats = mergedChats;
        return currentSnapshot;
    }

    private static Chat CloneChatIndex(Chat source)
    {
        return new Chat
        {
            Id = source.Id,
            Title = source.Title,
            ProjectId = source.ProjectId,
            AgentId = source.AgentId,
            CopilotSessionId = source.CopilotSessionId,
            CreatedAt = source.CreatedAt,
            UpdatedAt = source.UpdatedAt,
            ActiveSkillIds = [..source.ActiveSkillIds],
            ActiveMcpServerNames = [..source.ActiveMcpServerNames],
            SessionMode = source.SessionMode,
            SdkAgentName = source.SdkAgentName,
            WorktreePath = source.WorktreePath,
            LastModelUsed = source.LastModelUsed,
            TotalInputTokens = source.TotalInputTokens,
            TotalOutputTokens = source.TotalOutputTokens,
            PlanContent = source.PlanContent,
        };
    }
}

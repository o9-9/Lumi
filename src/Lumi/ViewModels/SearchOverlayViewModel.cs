using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Models;
using Lumi.Services;

namespace Lumi.ViewModels;

public class SearchResultItem
{
    public string Category { get; init; } = "";
    public string CategoryGlyph { get; init; } = "";
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string IconGlyph { get; init; } = "";
    public int NavIndex { get; init; }
    public object? Item { get; init; }
    public int Priority { get; init; } // Lower = better match
    public bool IsContentMatch { get; init; }
}

public class SearchResultGroup
{
    public string Category { get; init; } = "";
    public string CategoryGlyph { get; init; } = "";
    public bool IsCurrentTab { get; init; }
    public List<SearchResultItem> Items { get; init; } = [];
}

public partial class SearchOverlayViewModel : ObservableObject
{
    private readonly DataStore _dataStore;
    private readonly Func<int> _getCurrentNavIndex;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private int _selectedIndex;

    public ObservableCollection<SearchResultGroup> ResultGroups { get; } = [];

    /// <summary>Flat list of all results for keyboard navigation.</summary>
    public List<SearchResultItem> FlatResults { get; private set; } = [];

    /// <summary>Raised when a result is selected (navigate to it).</summary>
    public event Action<SearchResultItem>? ResultSelected;

    /// <summary>Raises the ResultSelected event for the given item.</summary>
    public void RaiseResultSelected(SearchResultItem item) => ResultSelected?.Invoke(item);

    public SearchOverlayViewModel(DataStore dataStore, Func<int> getCurrentNavIndex)
    {
        _dataStore = dataStore;
        _getCurrentNavIndex = getCurrentNavIndex;
    }

    public void Open()
    {
        SearchQuery = "";
        SelectedIndex = 0;
        IsOpen = true;
        PerformSearch();
    }

    [RelayCommand]
    public void Close()
    {
        IsOpen = false;
        SearchQuery = "";
        ResultGroups.Clear();
        FlatResults.Clear();
    }

    public void SelectCurrent()
    {
        if (SelectedIndex >= 0 && SelectedIndex < FlatResults.Count)
        {
            var result = FlatResults[SelectedIndex];
            Close();
            RaiseResultSelected(result);
        }
    }

    public void MoveSelection(int delta)
    {
        if (FlatResults.Count == 0) return;
        var newIndex = SelectedIndex + delta;
        if (newIndex < 0) newIndex = FlatResults.Count - 1;
        else if (newIndex >= FlatResults.Count) newIndex = 0;
        SelectedIndex = newIndex;
    }

    partial void OnSearchQueryChanged(string value) => PerformSearch();

    private void PerformSearch()
    {
        var query = SearchQuery?.Trim() ?? "";
        var currentNav = _getCurrentNavIndex();
        var allResults = new List<SearchResultItem>();

        // Search all categories
        allResults.AddRange(SearchChats(query, currentNav));
        allResults.AddRange(SearchProjects(query, currentNav));
        allResults.AddRange(SearchSkills(query, currentNav));
        allResults.AddRange(SearchAgents(query, currentNav));
        allResults.AddRange(SearchMemories(query, currentNav));
        allResults.AddRange(SearchMcpServers(query, currentNav));

        // Group results: current tab first, then others
        var groups = allResults
            .GroupBy(r => r.Category)
            .Select(g => new SearchResultGroup
            {
                Category = g.Key,
                CategoryGlyph = g.First().CategoryGlyph,
                IsCurrentTab = g.First().NavIndex == currentNav,
                Items = g.OrderBy(r => r.Priority).ThenBy(r => r.Title).Take(8).ToList()
            })
            .OrderByDescending(g => g.IsCurrentTab)
            .ThenBy(g => g.Category)
            .ToList();

        ResultGroups.Clear();
        foreach (var group in groups)
            ResultGroups.Add(group);

        FlatResults = groups.SelectMany(g => g.Items).ToList();
        SelectedIndex = FlatResults.Count > 0 ? 0 : -1;
    }

    private List<SearchResultItem> SearchChats(string query, int currentNav)
    {
        var results = new List<SearchResultItem>();
        var chats = _dataStore.Data.Chats;

        foreach (var chat in chats.OrderByDescending(c => c.UpdatedAt).Take(100))
        {
            int? priority = null;

            if (string.IsNullOrEmpty(query))
            {
                // Show recent chats when query is empty
                priority = 50;
            }
            else if (chat.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                priority = chat.Title.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : 10;
            }
            else
            {
                // Content search: look in messages
                var hasContentMatch = chat.Messages.Any(m =>
                    m.Role is "user" or "assistant" &&
                    m.Content.Contains(query, StringComparison.OrdinalIgnoreCase));
                if (hasContentMatch)
                    priority = 30;
            }

            if (priority.HasValue)
            {
                var subtitle = chat.UpdatedAt.ToString("MMM d, yyyy");
                var projectName = GetProjectName(chat.ProjectId);
                if (projectName != null)
                    subtitle = $"{projectName} · {subtitle}";

                results.Add(new SearchResultItem
                {
                    Category = "Chats",
                    CategoryGlyph = "💬",
                    Title = chat.Title,
                    Subtitle = subtitle,
                    NavIndex = 0,
                    Item = chat,
                    Priority = priority.Value,
                    IsContentMatch = !string.IsNullOrEmpty(query) && priority.Value >= 30
                });
            }
        }

        return string.IsNullOrEmpty(query) ? results.Take(3).ToList() : results;
    }

    private List<SearchResultItem> SearchProjects(string query, int currentNav)
    {
        var results = new List<SearchResultItem>();
        foreach (var project in _dataStore.Data.Projects)
        {
            int? priority = null;

            if (string.IsNullOrEmpty(query))
            {
                priority = 50;
            }
            else if (project.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                priority = project.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : 10;
            }
            else if (project.Instructions.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                priority = 30;
            }

            if (priority.HasValue)
            {
                var chatCount = _dataStore.Data.Chats.Count(c => c.ProjectId == project.Id);
                results.Add(new SearchResultItem
                {
                    Category = "Projects",
                    CategoryGlyph = "📁",
                    Title = project.Name,
                    Subtitle = chatCount > 0 ? $"{chatCount} chat{(chatCount != 1 ? "s" : "")}" : "No chats",
                    NavIndex = 1,
                    Item = project,
                    Priority = priority.Value,
                    IsContentMatch = !string.IsNullOrEmpty(query) && priority.Value >= 30
                });
            }
        }
        return string.IsNullOrEmpty(query) ? results.Take(3).ToList() : results;
    }

    private List<SearchResultItem> SearchSkills(string query, int currentNav)
    {
        var results = new List<SearchResultItem>();
        foreach (var skill in _dataStore.Data.Skills)
        {
            int? priority = null;

            if (string.IsNullOrEmpty(query))
            {
                priority = 50;
            }
            else if (skill.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                priority = skill.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : 10;
            }
            else if (skill.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                priority = 20;
            }
            else if (skill.Content.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                priority = 30;
            }

            if (priority.HasValue)
            {
                results.Add(new SearchResultItem
                {
                    Category = "Skills",
                    CategoryGlyph = "⚡",
                    Title = skill.Name,
                    Subtitle = skill.Description,
                    IconGlyph = skill.IconGlyph,
                    NavIndex = 2,
                    Item = skill,
                    Priority = priority.Value,
                    IsContentMatch = !string.IsNullOrEmpty(query) && priority.Value >= 30
                });
            }
        }
        return string.IsNullOrEmpty(query) ? results.Take(3).ToList() : results;
    }

    private List<SearchResultItem> SearchAgents(string query, int currentNav)
    {
        var results = new List<SearchResultItem>();
        foreach (var agent in _dataStore.Data.Agents)
        {
            int? priority = null;

            if (string.IsNullOrEmpty(query))
            {
                priority = 50;
            }
            else if (agent.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                priority = agent.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : 10;
            }
            else if (agent.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                priority = 20;
            }
            else if (agent.SystemPrompt.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                priority = 30;
            }

            if (priority.HasValue)
            {
                results.Add(new SearchResultItem
                {
                    Category = "Lumis",
                    CategoryGlyph = "✦",
                    Title = agent.Name,
                    Subtitle = agent.Description,
                    IconGlyph = agent.IconGlyph,
                    NavIndex = 3,
                    Item = agent,
                    Priority = priority.Value,
                    IsContentMatch = !string.IsNullOrEmpty(query) && priority.Value >= 30
                });
            }
        }
        return string.IsNullOrEmpty(query) ? results.Take(3).ToList() : results;
    }

    private List<SearchResultItem> SearchMemories(string query, int currentNav)
    {
        var results = new List<SearchResultItem>();
        foreach (var memory in _dataStore.Data.Memories)
        {
            int? priority = null;

            if (string.IsNullOrEmpty(query))
            {
                priority = 50;
            }
            else if (memory.Key.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                priority = memory.Key.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : 10;
            }
            else if (memory.Content.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                priority = 20;
            }
            else if (memory.Category.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                priority = 20;
            }

            if (priority.HasValue)
            {
                results.Add(new SearchResultItem
                {
                    Category = "Memories",
                    CategoryGlyph = "🧠",
                    Title = memory.Key,
                    Subtitle = $"[{memory.Category}] {(memory.Content.Length > 60 ? memory.Content[..60] + "…" : memory.Content)}",
                    NavIndex = 4,
                    Item = memory,
                    Priority = priority.Value,
                    IsContentMatch = !string.IsNullOrEmpty(query) && priority.Value >= 30
                });
            }
        }
        return string.IsNullOrEmpty(query) ? results.Take(3).ToList() : results;
    }

    private List<SearchResultItem> SearchMcpServers(string query, int currentNav)
    {
        var results = new List<SearchResultItem>();
        foreach (var server in _dataStore.Data.McpServers)
        {
            int? priority = null;

            if (string.IsNullOrEmpty(query))
            {
                priority = 50;
            }
            else if (server.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                priority = server.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : 10;
            }
            else if (server.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                priority = 20;
            }

            if (priority.HasValue)
            {
                results.Add(new SearchResultItem
                {
                    Category = "MCP Servers",
                    CategoryGlyph = "🔌",
                    Title = server.Name,
                    Subtitle = server.Description,
                    NavIndex = 5,
                    Item = server,
                    Priority = priority.Value,
                    IsContentMatch = !string.IsNullOrEmpty(query) && priority.Value >= 30
                });
            }
        }
        return string.IsNullOrEmpty(query) ? results.Take(3).ToList() : results;
    }

    private string? GetProjectName(Guid? projectId)
    {
        if (!projectId.HasValue) return null;
        return _dataStore.Data.Projects.FirstOrDefault(p => p.Id == projectId.Value)?.Name;
    }
}

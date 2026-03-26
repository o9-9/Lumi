using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Models;
using Lumi.Services;

namespace Lumi.ViewModels;

public class SearchResultItem
{
    public string Category { get; init; } = "";
    public string CategoryIcon { get; init; } = "";
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public int NavIndex { get; init; }
    public object? Item { get; init; }
    public int Priority { get; init; }
    public bool IsContentMatch { get; init; }
    public int SettingsPageIndex { get; init; } = -1;

    private Geometry? _categoryGeometry;
    public Geometry? CategoryGeometry => _categoryGeometry ??=
        !string.IsNullOrEmpty(CategoryIcon) ? StreamGeometry.Parse(CategoryIcon) : null;
}

public class SearchResultGroup
{
    public string Category { get; init; } = "";
    public string CategoryIcon { get; init; } = ""; // SVG path data
    public bool IsCurrentTab { get; init; }
    public List<SearchResultItem> Items { get; init; } = [];

    private Geometry? _categoryGeometry;
    public Geometry? CategoryGeometry => _categoryGeometry ??=
        !string.IsNullOrEmpty(CategoryIcon) ? StreamGeometry.Parse(CategoryIcon) : null;
}

public partial class SearchOverlayViewModel : ObservableObject
{
    private readonly DataStore _dataStore;
    private readonly Func<int> _getCurrentNavIndex;

    // Icon SVG paths matching the nav bar icons
    private const string IconChat = "M4 4h16a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H7l-4 3V6a2 2 0 0 1 2-2z";
    private const string IconFolder = "M2 6a2 2 0 0 1 2-2h5l2 2h9a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V6z";
    private const string IconBolt = "M21.64 3.64l-1.28-1.28a1.21 1.21 0 0 0-1.72 0L2.36 18.64a1.21 1.21 0 0 0 0 1.72l1.28 1.28a1.2 1.2 0 0 0 1.72 0L21.64 5.36a1.2 1.2 0 0 0 0-1.72z M14 7l3 3 M5 6v4 M19 14v4 M10 2v2 M7 8H3 M21 16h-4 M11 3H9";
    private const string IconSparkle = "M11.017 2.814a1 1 0 0 1 1.966 0l1.051 5.558a2 2 0 0 0 1.594 1.594l5.558 1.051a1 1 0 0 1 0 1.966l-5.558 1.051a2 2 0 0 0-1.594 1.594l-1.051 5.558a1 1 0 0 1-1.966 0l-1.051-5.558a2 2 0 0 0-1.594-1.594l-5.558-1.051a1 1 0 0 1 0-1.966l5.558-1.051a2 2 0 0 0 1.594-1.594z M20 2v4 M22 4h-4 M4 20a2 2 0 1 0 0-4 2 2 0 0 0 0 4z";
    private const string IconMemory = "M9.5 2A1.5 1.5 0 0 0 8 3.5V4H4a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2V6a2 2 0 0 0-2-2h-4v-.5A1.5 1.5 0 0 0 14.5 2h-5zM10 4V3.5a.5.5 0 0 1 .5-.5h3a.5.5 0 0 1 .5.5V4h-4zm-2 5a1 1 0 1 1 2 0 1 1 0 0 1-2 0zm5 0a1 1 0 1 1 2 0 1 1 0 0 1-2 0zm-5 4a1 1 0 1 1 2 0 1 1 0 0 1-2 0zm5 0a1 1 0 1 1 2 0 1 1 0 0 1-2 0z";
    private const string IconPlug = "M17 6.1h3V8h-3v2.1a5 5 0 0 1-4 4.9V18h2v2H9v-2h2v-3a5 5 0 0 1-4-4.9V8H4V6.1h3V4h2v2.1h4V4h2v2.1zM9 8v2.1a3 3 0 0 0 6 0V8H9z";
    private const string IconGear = "M9.671 4.136a2.34 2.34 0 0 1 4.659 0 2.34 2.34 0 0 0 3.319 1.915 2.34 2.34 0 0 1 2.33 4.033 2.34 2.34 0 0 0 0 3.831 2.34 2.34 0 0 1-2.33 4.033 2.34 2.34 0 0 0-3.319 1.915 2.34 2.34 0 0 1-4.659 0 2.34 2.34 0 0 0-3.32-1.915 2.34 2.34 0 0 1-2.33-4.033 2.34 2.34 0 0 0 0-3.831A2.34 2.34 0 0 1 6.35 6.051a2.34 2.34 0 0 0 3.319-1.915z M12 12a3 3 0 1 0 0-6 3 3 0 0 0 0 6z";

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
        // Always search — the setter won't fire OnSearchQueryChanged if query was already ""
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
        allResults.AddRange(SearchChats(query));
        allResults.AddRange(SearchProjects(query));
        allResults.AddRange(SearchSkills(query));
        allResults.AddRange(SearchAgents(query));
        allResults.AddRange(SearchMemories(query));
        allResults.AddRange(SearchMcpServers(query));
        allResults.AddRange(SearchSettings(query));

        // Group results: current tab first, then others
        var groups = allResults
            .GroupBy(r => r.Category)
            .Select(g => new SearchResultGroup
            {
                Category = g.Key,
                CategoryIcon = g.First().CategoryIcon,
                IsCurrentTab = g.First().NavIndex == currentNav,
                Items = g.OrderBy(r => r.Priority).ThenBy(r => r.Title).Take(20).ToList()
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

    private List<SearchResultItem> SearchChats(string query)
    {
        var results = new List<SearchResultItem>();
        var chats = _dataStore.Data.Chats;

        foreach (var chat in chats.OrderByDescending(c => c.UpdatedAt))
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
                    CategoryIcon = IconChat,
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

    private List<SearchResultItem> SearchProjects(string query)
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
                    CategoryIcon = IconFolder,
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

    private List<SearchResultItem> SearchSkills(string query)
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
                    CategoryIcon = IconBolt,
                    Title = skill.Name,
                    Subtitle = skill.Description,
                    NavIndex = 2,
                    Item = skill,
                    Priority = priority.Value,
                    IsContentMatch = !string.IsNullOrEmpty(query) && priority.Value >= 30
                });
            }
        }
        return string.IsNullOrEmpty(query) ? results.Take(3).ToList() : results;
    }

    private List<SearchResultItem> SearchAgents(string query)
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
                    CategoryIcon = IconSparkle,
                    Title = agent.Name,
                    Subtitle = agent.Description,
                    NavIndex = 3,
                    Item = agent,
                    Priority = priority.Value,
                    IsContentMatch = !string.IsNullOrEmpty(query) && priority.Value >= 30
                });
            }
        }
        return string.IsNullOrEmpty(query) ? results.Take(3).ToList() : results;
    }

    private List<SearchResultItem> SearchMemories(string query)
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
                    CategoryIcon = IconMemory,
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

    private List<SearchResultItem> SearchMcpServers(string query)
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
                    CategoryIcon = IconPlug,
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

    // ── Settings search ──

    private static readonly (string Name, string Page, int PageIndex)[] SettingsIndex =
    [
        ("Your Name", "Profile", 0),
        ("Language", "Profile", 0),
        ("Launch at Startup", "General", 1),
        ("Start Minimized", "General", 1),
        ("Minimize to Tray", "General", 1),
        ("Enable Notifications", "General", 1),
        ("Global Hotkey", "General", 1),
        ("Dark Mode", "Appearance", 2),
        ("Compact Density", "Appearance", 2),
        ("Font Size", "Appearance", 2),
        ("Show Animations", "Appearance", 2),
        ("Send with Enter", "Chat", 3),
        ("Show Timestamps", "Chat", 3),
        ("Show Tool Calls", "Chat", 3),
        ("Show Reasoning", "Chat", 3),
        ("Expand Reasoning While Streaming", "Chat", 3),
        ("Auto Generate Titles", "Chat", 3),
        ("GitHub Account", "AI & Models", 4),
        ("Preferred Model", "AI & Models", 4),
        ("Reasoning Effort", "AI & Models", 4),
        ("Auto Save Memories", "Privacy & Data", 5),
        ("Auto Save Chats", "Privacy & Data", 5),
        ("Import Browser Cookies", "Privacy & Data", 5),
        ("Clear All Chats", "Privacy & Data", 5),
        ("Clear All Memories", "Privacy & Data", 5),
        ("Reset All Settings", "Privacy & Data", 5),
        ("Version", "About", 6),
    ];

    private List<SearchResultItem> SearchSettings(string query)
    {
        if (string.IsNullOrEmpty(query)) return [];

        var results = new List<SearchResultItem>();
        foreach (var (name, page, pageIndex) in SettingsIndex)
        {
            if (name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                page.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                var priority = name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : 10;
                results.Add(new SearchResultItem
                {
                    Category = "Settings",
                    CategoryIcon = IconGear,
                    Title = name,
                    Subtitle = page,
                    NavIndex = 6,
                    Priority = priority,
                    SettingsPageIndex = pageIndex,
                });
            }
        }
        return results;
    }
}

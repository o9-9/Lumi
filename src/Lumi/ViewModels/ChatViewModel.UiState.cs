using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using StrataTheme.Controls;

namespace Lumi.ViewModels;

public partial class ChatViewModel
{
    private bool _suppressComposerAgentSync;
    private bool _suppressComposerProjectSync;
    private CancellationTokenSource? _fileSearchCts;
    private readonly VoiceInputService _voiceService = new();
    private string _textBeforeVoice = "";
    private bool _voiceStarting;

    [ObservableProperty] private bool _sendWithEnter = true;
    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private string? _selectedAgentName;
    [ObservableProperty] private string _selectedAgentGlyph = "◉";
    [ObservableProperty] private string? _selectedProjectName;
    [ObservableProperty] private string? _projectBadgeText;
    [ObservableProperty] private string? _agentBadgeText;
    [ObservableProperty] private string[]? _qualityLevels;

    public ObservableCollection<StrataComposerChip> AvailableAgentChips { get; } = [];
    public ObservableCollection<StrataComposerChip> AvailableSkillChips { get; } = [];
    public ObservableCollection<StrataComposerChip> AvailableMcpChips { get; } = [];
    public ObservableCollection<StrataComposerChip> AvailableProjectChips { get; } = [];
    [ObservableProperty] private IEnumerable<StrataComposerChip>? _availableFileSuggestions;
    public ObservableCollection<FileAttachmentItem> PendingAttachmentItems { get; } = [];

    public bool IsWelcomeVisible => CurrentChat is null;
    public bool IsChatVisible => CurrentChat is not null;
    public bool HasPendingAttachments => PendingAttachmentItems.Count > 0;
    public bool HasProjectBadge => !string.IsNullOrWhiteSpace(ProjectBadgeText);
    public bool HasAgentBadge => !string.IsNullOrWhiteSpace(AgentBadgeText);
    public bool ShowBrowserToggle => HasUsedBrowser;

    public event Action<Guid?>? ComposerProjectFilterRequested;

    [RelayCommand]
    private void ToggleBrowserVisibility()
    {
        ToggleBrowser();
    }

    private static readonly string[] ReasoningLevels = [Loc.Quality_Low, Loc.Quality_Medium, Loc.Quality_High];

    private static bool IsReasoningModel(string? modelId)
    {
        if (string.IsNullOrEmpty(modelId))
            return false;

        return modelId.StartsWith("o1", StringComparison.OrdinalIgnoreCase)
            || modelId.StartsWith("o3", StringComparison.OrdinalIgnoreCase)
            || modelId.StartsWith("o4", StringComparison.OrdinalIgnoreCase)
            || modelId.Contains("think", StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateQualityLevels(string? modelId)
    {
        QualityLevels = IsReasoningModel(modelId) ? ReasoningLevels : null;
    }

    private void InitializeMvvmUiState()
    {
        SendWithEnter = _dataStore.Data.Settings.SendWithEnter;

        ActiveSkillChips.CollectionChanged += OnActiveSkillChipsCollectionChanged;
        ActiveMcpChips.CollectionChanged += OnActiveMcpChipsCollectionChanged;
        PendingAttachmentItems.CollectionChanged += OnPendingAttachmentItemsCollectionChanged;

        RefreshComposerCatalogs();
        SyncComposerAgentSelectionFromState();
        SyncComposerProjectSelectionFromState();
        RefreshProjectBadge();
        RefreshAgentBadge();
        UpdateQualityLevels(SelectedModel);
    }

    public void RefreshComposerCatalogs()
    {
        ReplaceCollection(AvailableAgentChips,
            _dataStore.Data.Agents
                .OrderBy(a => a.Name)
                .Select(a => new StrataComposerChip(a.Name, a.IconGlyph)));

        ReplaceCollection(AvailableSkillChips,
            _dataStore.Data.Skills
                .OrderBy(s => s.Name)
                .Select(s => new StrataComposerChip(s.Name, s.IconGlyph)));

        ReplaceCollection(AvailableMcpChips,
            _dataStore.Data.McpServers
                .Where(s => s.IsEnabled)
                .OrderBy(s => s.Name)
                .Select(s => new StrataComposerChip(s.Name)));

        ReplaceCollection(AvailableProjectChips,
            _dataStore.Data.Projects
                .OrderBy(p => p.Name)
                .Select(p => new StrataComposerChip(p.Name, "📁")));

        SyncComposerProjectSelectionFromState();
        RefreshProjectBadge();
    }

    public void HandleFileQueryChanged(string query)
    {
        _fileSearchCts?.Cancel();
        _fileSearchCts?.Dispose();

        var cts = new CancellationTokenSource();
        _fileSearchCts = cts;
        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                // A small debounce avoids filesystem churn while the user is still typing.
                await Task.Delay(90, token);
                if (token.IsCancellationRequested)
                    return;

                var results = SearchFiles(query);
                if (token.IsCancellationRequested)
                    return;

                Dispatcher.UIThread.Post(() =>
                {
                    if (token.IsCancellationRequested)
                        return;

                    AvailableFileSuggestions = results;
                }, DispatcherPriority.Background);
            }
            catch (OperationCanceledException)
            {
                // Expected when query changes quickly.
            }
        }, token);
    }

    public void HandleFileSelected(string filePath)
    {
        AddAttachment(filePath);
    }

    partial void OnCurrentChatChanged(Chat? value)
    {
        OnPropertyChanged(nameof(IsWelcomeVisible));
        OnPropertyChanged(nameof(IsChatVisible));
        SyncComposerProjectSelectionFromState();
        RefreshProjectBadge();
    }

    partial void OnActiveAgentChanged(LumiAgent? value)
    {
        SyncComposerAgentSelectionFromState();
        RefreshAgentBadge();
    }

    partial void OnHasUsedBrowserChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowBrowserToggle));
    }

    partial void OnProjectBadgeTextChanged(string? value)
    {
        OnPropertyChanged(nameof(HasProjectBadge));
    }

    partial void OnAgentBadgeTextChanged(string? value)
    {
        OnPropertyChanged(nameof(HasAgentBadge));
    }

    partial void OnSelectedAgentNameChanged(string? value)
    {
        if (_suppressComposerAgentSync)
            return;

        ApplyComposerAgentSelection(value);
    }

    public void ApplyComposerAgentSelection(string? value)
    {
        if (IsLoadingChat)
        {
            SyncComposerAgentSelectionFromState();
            return;
        }

        if (string.Equals(ActiveAgent?.Name, value, StringComparison.Ordinal))
            return;

        if (!CanChangeAgent)
        {
            SyncComposerAgentSelectionFromState();
            return;
        }

        if (string.IsNullOrWhiteSpace(value))
            SetActiveAgent(null);
        else
        {
            var agent = _dataStore.Data.Agents.FirstOrDefault(a => a.Name == value);
            if (agent is null)
            {
                SyncComposerAgentSelectionFromState();
                return;
            }

            SetActiveAgent(agent);
        }
    }

    partial void OnSelectedProjectNameChanged(string? value)
    {
        if (_suppressComposerProjectSync)
            return;

        if (string.IsNullOrWhiteSpace(value))
        {
            ClearProjectId();
            ComposerProjectFilterRequested?.Invoke(null);
            return;
        }

        var project = _dataStore.Data.Projects.FirstOrDefault(p => p.Name == value);
        if (project is null)
        {
            SyncComposerProjectSelectionFromState();
            return;
        }

        var isExistingChat = CurrentChat is not null && CurrentChat.Messages.Count > 0;
        if (!isExistingChat)
            SetProjectId(project.Id);

        ComposerProjectFilterRequested?.Invoke(project.Id);
    }

    private void SyncComposerAgentSelectionFromState()
    {
        _suppressComposerAgentSync = true;
        try
        {
            SelectedAgentName = ActiveAgent?.Name;
            SelectedAgentGlyph = ActiveAgent?.IconGlyph ?? "◉";
        }
        finally
        {
            _suppressComposerAgentSync = false;
        }
    }

    public void SyncComposerProjectSelectionFromState()
    {
        _suppressComposerProjectSync = true;
        try
        {
            SelectedProjectName = GetCurrentProjectName();
        }
        finally
        {
            _suppressComposerProjectSync = false;
        }
    }

    private void RefreshProjectBadge()
    {
        var projectName = GetCurrentProjectName();
        ProjectBadgeText = string.IsNullOrWhiteSpace(projectName) ? null : $"📁 {projectName}";
    }

    private void RefreshAgentBadge()
    {
        AgentBadgeText = ActiveAgent is null ? null : $"{ActiveAgent.IconGlyph} {ActiveAgent.Name}";
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values)
            target.Add(value);
    }

    private void OnPendingAttachmentItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasPendingAttachments));
    }

    private void OnActiveSkillChipsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (IsLoadingChat)
            return;

        if (args.Action == NotifyCollectionChangedAction.Add && args.NewItems is not null)
        {
            foreach (var item in args.NewItems)
            {
                if (item is StrataComposerChip chip)
                    RegisterSkillIdByName(chip.Name);
            }
        }
    }

    private void OnActiveMcpChipsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (IsLoadingChat)
            return;

        if (args.Action == NotifyCollectionChangedAction.Add && args.NewItems is not null)
        {
            foreach (var item in args.NewItems)
            {
                if (item is StrataComposerChip chip)
                    RegisterMcpByName(chip.Name);
            }
            return;
        }

        if (args.Action == NotifyCollectionChangedAction.Remove && args.OldItems is not null)
        {
            foreach (var item in args.OldItems)
            {
                if (item is StrataComposerChip chip)
                    ActiveMcpServerNames.Remove(chip.Name);
            }
            SyncActiveMcpsToChat();
            return;
        }

        if (args.Action == NotifyCollectionChangedAction.Reset)
        {
            ActiveMcpServerNames.Clear();
            SyncActiveMcpsToChat();
        }
    }

    // ── Voice input ──────────────────────────────────────

    [RelayCommand]
    private async Task ToggleVoice()
    {
        if (!_voiceService.IsAvailable || _voiceStarting)
            return;

        if (_voiceService.IsRecording)
        {
            await _voiceService.StopAsync();
            IsRecording = false;
            return;
        }

        _voiceStarting = true;
        _textBeforeVoice = PromptText ?? "";

        _voiceService.HypothesisGenerated += OnVoiceHypothesis;
        _voiceService.ResultGenerated += OnVoiceResult;
        _voiceService.Stopped += OnVoiceStopped;
        _voiceService.Error += OnVoiceError;

        var culture = CultureInfo.CurrentUICulture;
        var language = culture.Name.Contains('-') ? culture.Name : culture.IetfLanguageTag;
        if (string.IsNullOrEmpty(language) || !language.Contains('-'))
            language = "en-US";

        await _voiceService.StartAsync(language);

        _voiceStarting = false;
        if (_voiceService.IsRecording)
            IsRecording = true;

        FocusComposerRequested?.Invoke();
    }

    private void OnVoiceHypothesis(string text)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var baseText = _textBeforeVoice;
            if (!string.IsNullOrEmpty(baseText) && !baseText.EndsWith(' '))
                baseText += " ";
            PromptText = baseText + text;
        });
    }

    private void OnVoiceResult(string text)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var baseText = _textBeforeVoice;
            if (!string.IsNullOrEmpty(baseText) && !baseText.EndsWith(' '))
                baseText += " ";
            _textBeforeVoice = baseText + text;
            PromptText = _textBeforeVoice;
        });
    }

    private void OnVoiceError(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            StatusText = message == "speech_privacy"
                ? Loc.Voice_SpeechPrivacyRequired
                : $"{Loc.Voice_Error}: {message}";
        });
    }

    private void OnVoiceStopped()
    {
        _voiceService.HypothesisGenerated -= OnVoiceHypothesis;
        _voiceService.ResultGenerated -= OnVoiceResult;
        _voiceService.Stopped -= OnVoiceStopped;
        _voiceService.Error -= OnVoiceError;

        Dispatcher.UIThread.Post(() => IsRecording = false);
    }

    /// <summary>Cleans up voice resources. Called when the view is being detached.</summary>
    public void StopVoiceIfRecording()
    {
        if (_voiceService.IsRecording)
        {
            _ = _voiceService.StopAsync();
            IsRecording = false;
        }
    }

    /// <summary>Raised when the composer should receive focus (e.g., after attaching files or voice toggle).</summary>
    public event Action? FocusComposerRequested;

    // ── Attach files (requires view interaction for file picker) ──

    /// <summary>Raised when user requests file attachment. The view handles the file picker dialog.</summary>
    public event Action? AttachFilesRequested;

    [RelayCommand]
    private void RequestAttachFiles()
    {
        AttachFilesRequested?.Invoke();
    }

    // ── Chip removal commands (bound via Strata ICommand properties) ──

    [RelayCommand]
    private void RemoveAgent()
    {
        ApplyComposerAgentSelection(null);
    }

    [RelayCommand]
    private void RemoveProject()
    {
        SelectedProjectName = null;
    }

    [RelayCommand]
    private void RemoveSkill(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
            RemoveSkillByName(name);
    }

    [RelayCommand]
    private void RemoveMcp(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
            RemoveMcpByName(name);
    }

    // ── File autocomplete commands ──

    [RelayCommand]
    private void HandleFileQuery(string? query)
    {
        HandleFileQueryChanged(query ?? "");
    }

    [RelayCommand]
    private void HandleFileSelection(string? filePath)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            HandleFileSelected(filePath);
            FocusComposerRequested?.Invoke();
        }
    }

    // ── Clipboard paste (View handles actual clipboard access, ViewModel handles attachment) ──

    /// <summary>Raised when the composer detects a clipboard image paste.
    /// The view should read the clipboard, save the image, and call <see cref="AddAttachment"/>.</summary>
    public event Action? ClipboardPasteRequested;

    [RelayCommand]
    private void RequestClipboardPaste()
    {
        ClipboardPasteRequested?.Invoke();
    }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using StrataTheme.Controls;

namespace Lumi.ViewModels;

internal static class TranscriptVirtualization
{
    public static string CreateRuntimeKey(string prefix) => $"{prefix}:{Guid.NewGuid():N}";

    public static double EstimateWrappedTextHeight(
        string? text,
        double minimumHeight,
        int charsPerLine,
        double lineHeight,
        double chromeHeight,
        double maximumHeight = 12000d)
    {
        if (string.IsNullOrWhiteSpace(text))
            return minimumHeight;

        var wrappedLines = 0;
        var normalized = text.Replace("\r\n", "\n");
        foreach (var rawLine in normalized.Split('\n'))
        {
            var lineLength = Math.Max(1, rawLine.Length);
            wrappedLines += Math.Max(1, (int)Math.Ceiling(lineLength / (double)Math.Max(12, charsPerLine)));
        }

        var estimatedHeight = chromeHeight + (wrappedLines * lineHeight);
        return Math.Clamp(estimatedHeight, minimumHeight, maximumHeight);
    }

    public static double EstimateUserMessageHeight(string? content, int attachmentCount = 0, int skillCount = 0)
    {
        var chrome = 40d + (attachmentCount * 34d) + (skillCount * 20d);
        return EstimateWrappedTextHeight(content, 72d, 56, 18d, chrome, 5000d);
    }

    public static double EstimateAssistantMessageHeight(string? content, int skillCount = 0, int attachmentCount = 0, int sourceCount = 0)
    {
        var chrome = 42d + (skillCount * 20d) + (attachmentCount * 30d) + (sourceCount * 18d);
        return EstimateWrappedTextHeight(content, 72d, 68, 18d, chrome, 6000d);
    }

    public static double EstimateErrorHeight(string? content)
    {
        return EstimateWrappedTextHeight(content, 56d, 64, 18d, 36d, 1200d);
    }

    public static double EstimateReasoningHeight(string? content, bool isExpanded)
    {
        if (!isExpanded)
            return 32d;

        return EstimateWrappedTextHeight(content, 72d, 72, 17d, 28d, 3000d);
    }

    public static double EstimateToolGroupHeight(string? label, IEnumerable<ToolCallItemBase> toolCalls, bool isExpanded)
    {
        var collapsed = 36d;
        if (!isExpanded)
            return collapsed;

        var callsHeight = toolCalls.Sum(call => Math.Max(32d, call.EstimatedHeightHint));
        var callCount = toolCalls.Count();
        return collapsed + callsHeight + Math.Max(0, callCount - 1) * 8d + 12d;
    }

    public static double EstimateToolCallHeight(string? inputParameters, string? moreInfo, bool hasDiff, bool isCompact)
    {
        var minimum = 32d;
        var chrome = 18d + (hasDiff ? 20d : 0d);
        var text = string.Join("\n", new[] { inputParameters, moreInfo }.Where(static s => !string.IsNullOrWhiteSpace(s)));
        return EstimateWrappedTextHeight(text, minimum, 84, 17d, chrome, 720d);
    }

    public static double EstimateTerminalHeight(string? command, string? output, bool isExpanded)
    {
        var commandHeight = EstimateWrappedTextHeight(command, 34d, 84, 17d, 18d, 160d);
        if (!isExpanded || string.IsNullOrWhiteSpace(output))
            return commandHeight;

        var outputHeight = EstimateWrappedTextHeight(output, 0d, 92, 16d, 18d, 1400d);
        return commandHeight + outputHeight;
    }

    public static double EstimateTodoHeight(string? inputParameters)
    {
        return EstimateWrappedTextHeight(inputParameters, 34d, 84, 17d, 18d, 900d);
    }

    public static double EstimateQuestionHeight(string? question, string? options, bool allowFreeText)
    {
        var chrome = 108d + (allowFreeText ? 44d : 0d);
        var text = string.Join("\n", new[] { question, options }.Where(static s => !string.IsNullOrWhiteSpace(s)));
        return EstimateWrappedTextHeight(text, 156d, 54, 19d, chrome, 1800d);
    }

    public static double EstimateTurnSummaryHeight(string? label, int innerItemCount, bool isExpanded)
    {
        var collapsed = 28d;
        if (!isExpanded)
            return collapsed;

        return collapsed + (innerItemCount * 44d) + Math.Max(0, innerItemCount - 1) * 6d;
    }

    public static double EstimateFileChangesHeight(IReadOnlyCollection<FileChangeItem> fileChanges)
    {
        var editCount = fileChanges.Sum(fileChange => Math.Max(1, fileChange.Edits.Count));
        return Math.Clamp(56d + fileChanges.Count * 46d + editCount * 10d, 72d, 1400d);
    }

    public static double EstimatePlanCardHeight(string? statusText)
    {
        return EstimateWrappedTextHeight(statusText, 48d, 72, 17d, 24d, 180d);
    }
}

// ── Base ─────────────────────────────────────────────

/// <summary>Base class for all items displayed in the chat transcript.</summary>
public abstract partial class TranscriptItem : ObservableObject, IStrataVirtualizedItem
{
    protected TranscriptItem(object virtualizationMeasureKey, object virtualizationRecycleKey, double virtualizationHeightHint)
    {
        VirtualizationMeasureKey = virtualizationMeasureKey;
        VirtualizationRecycleKey = virtualizationRecycleKey;
        VirtualizationHeightHint = virtualizationHeightHint;
    }

    public object? VirtualizationRecycleKey { get; protected set; }
    public object? VirtualizationMeasureKey { get; protected set; }
    public double? VirtualizationHeightHint { get; protected set; }

    protected void UpdateVirtualizationHeightHint(double heightHint)
    {
        var newHeight = Math.Max(24d, heightHint);
        if (VirtualizationHeightHint is double current && Math.Abs(current - newHeight) < 0.5d)
            return;

        VirtualizationHeightHint = newHeight;
        OnPropertyChanged(nameof(VirtualizationHeightHint));
    }
}

// ── User message ─────────────────────────────────────

public partial class UserMessageItem : TranscriptItem
{
    private readonly ChatMessageViewModel _source;
    private readonly Action<ChatMessage, bool>? _resendAction;

    [ObservableProperty] private string _content;
    [ObservableProperty] private string _timestampText;

    public string? Author => _source.Author;
    public ChatMessage Message => _source.Message;
    public List<FileAttachmentItem> Attachments { get; }
    public List<SkillReference> Skills { get; }
    public bool HasAttachments => Attachments.Count > 0;
    public bool HasSkills => Skills.Count > 0;
    public List<FileAttachmentItem>? DisplayAttachments => HasAttachments ? Attachments : null;
    public List<SkillReference>? DisplaySkills => HasSkills ? Skills : null;

    /// <summary>Command invoked when user clicks Edit on the message. Sets EditText to current content.</summary>
    public ICommand BeginEditCommand { get; }

    /// <summary>Command invoked when user confirms an edit. Parameter is the new text string.</summary>
    public ICommand ConfirmEditCommand { get; }

    /// <summary>Command invoked when user clicks Regenerate/Retry on the message.</summary>
    public ICommand ResendCommand { get; }

    public UserMessageItem(ChatMessageViewModel source, bool showTimestamps, Action<ChatMessage, bool>? resendAction = null)
        : base(
            $"message:user:{source.Message.Id}",
            typeof(UserMessageItem),
            TranscriptVirtualization.EstimateUserMessageHeight(source.Content, source.Message.Attachments.Count, source.Message.ActiveSkills.Count))
    {
        _source = source;
        _resendAction = resendAction;
        _content = source.Content;
        _timestampText = showTimestamps ? source.TimestampText : "";
        Attachments = source.Message.Attachments.Select(fp => new FileAttachmentItem(fp)).ToList();
        Skills = source.Message.ActiveSkills.ToList();

        BeginEditCommand = new RelayCommand(() => { /* Strata handles entering edit mode internally */ });
        ConfirmEditCommand = new RelayCommand<string>(text => EditAndResend(text ?? Content));
        ResendCommand = new RelayCommand(ResendFromMessage);
    }

    public void ResendFromMessage() => _resendAction?.Invoke(_source.Message, false);

    public void EditAndResend(string newContent)
    {
        _source.Message.Content = newContent;
        _source.NotifyContentChanged();
        Content = newContent;
        UpdateVirtualizationHeightHint(TranscriptVirtualization.EstimateUserMessageHeight(newContent, Attachments.Count, Skills.Count));
        _resendAction?.Invoke(_source.Message, true);
    }
}

// ── Assistant message ────────────────────────────────

public partial class AssistantMessageItem : TranscriptItem
{
    private readonly ChatMessageViewModel _source;

    [ObservableProperty] private string _content;
    [ObservableProperty] private string _timestampText;
    [ObservableProperty] private bool _isStreaming;

    // Extras — populated when streaming ends
    [ObservableProperty] private bool _hasSkills;
    [ObservableProperty] private bool _hasFileAttachments;
    [ObservableProperty] private bool _hasSources;
    [ObservableProperty] private string _sourcesLabel = "";

    partial void OnHasSkillsChanged(bool value) => OnPropertyChanged(nameof(DisplaySkills));
    partial void OnHasFileAttachmentsChanged(bool value) => OnPropertyChanged(nameof(DisplayFileAttachments));
    partial void OnHasSourcesChanged(bool value) => OnPropertyChanged(nameof(DisplaySourcesSection));

    public string? Author => _source.Author;
    public ObservableCollection<SkillReference> Skills { get; } = [];
    public ObservableCollection<FileAttachmentItem> FileAttachments { get; } = [];
    public ObservableCollection<SourceItem> Sources { get; } = [];
    public ObservableCollection<SkillReference>? DisplaySkills => HasSkills ? Skills : null;
    public ObservableCollection<FileAttachmentItem>? DisplayFileAttachments => HasFileAttachments ? FileAttachments : null;
    public AssistantMessageItem? DisplaySourcesSection => HasSources ? this : null;

    public AssistantMessageItem(ChatMessageViewModel source, bool showTimestamps)
        : base(
            $"message:assistant:{source.Message.Id}",
            typeof(AssistantMessageItem),
            TranscriptVirtualization.EstimateAssistantMessageHeight(
                source.Content,
                source.Message.ActiveSkills.Count,
                source.Message.Attachments.Count,
                source.Message.Sources.Count))
    {
        _source = source;
        _content = source.Content;
        _timestampText = showTimestamps ? source.TimestampText : "";
        _isStreaming = source.IsStreaming;

        // Only subscribe while content is still changing (streaming).
        // Once streaming ends, content is final — unsubscribe to avoid leaks.
        if (source.IsStreaming)
            source.PropertyChanged += OnSourcePropertyChanged;
    }

    private void OnSourcePropertyChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatMessageViewModel.Content))
        {
            Content = _source.Content;
            // Skip expensive height estimation during streaming — content changes too fast.
            // Height is recalculated once when streaming ends.
        }
        else if (e.PropertyName == nameof(ChatMessageViewModel.IsStreaming) && !_source.IsStreaming)
        {
            IsStreaming = false;
            UpdateVirtualizationHeightHint(TranscriptVirtualization.EstimateAssistantMessageHeight(Content, Skills.Count, FileAttachments.Count, Sources.Count));
            _source.PropertyChanged -= OnSourcePropertyChanged;
        }
    }

    /// <summary>
    /// Populates extras from pending state. Called by the ChatViewModel
    /// when the assistant turn completes (or during transcript rebuild).
    /// </summary>
    public void ApplyExtras(
        List<FileAttachmentItem>? fileChips)
    {
        // Skills come from the persisted model
        Skills.Clear();
        foreach (var skill in _source.Message.ActiveSkills)
            Skills.Add(skill);
        HasSkills = Skills.Count > 0;

        // File attachments
        if (fileChips is { Count: > 0 })
        {
            foreach (var fc in fileChips)
                FileAttachments.Add(fc);
        }
        HasFileAttachments = FileAttachments.Count > 0;

        // Sources come from the persisted model
        Sources.Clear();
        foreach (var src in _source.Message.Sources)
            Sources.Add(new SourceItem(src));
        HasSources = Sources.Count > 0;
        SourcesLabel = Sources.Count == 1 ? Loc.Sources_One : string.Format(Loc.Sources_N, Sources.Count);
        UpdateVirtualizationHeightHint(TranscriptVirtualization.EstimateAssistantMessageHeight(Content, Skills.Count, FileAttachments.Count, Sources.Count));
    }
}

// ── Error message ────────────────────────────────────

public partial class ErrorMessageItem : TranscriptItem
{
    [ObservableProperty] private string _content;
    [ObservableProperty] private string _timestampText;

    public string? Author { get; }

    public ErrorMessageItem(ChatMessageViewModel source, bool showTimestamps)
        : base($"message:error:{source.Message.Id}", typeof(ErrorMessageItem), TranscriptVirtualization.EstimateErrorHeight(source.Content))
    {
        Author = source.Author;
        _content = source.Content;
        _timestampText = showTimestamps ? source.TimestampText : "";
    }

    public ErrorMessageItem(string content, string? author = null)
        : base(TranscriptVirtualization.CreateRuntimeKey("error"), typeof(ErrorMessageItem), TranscriptVirtualization.EstimateErrorHeight(content))
    {
        Author = author;
        _content = content;
        _timestampText = "";
    }
}

// ── Reasoning block ──────────────────────────────────

public partial class ReasoningItem : TranscriptItem
{
    [ObservableProperty] private string _content;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isExpanded;

    private readonly ChatMessageViewModel? _source;
    private readonly bool _expandWhileStreaming;

    public ReasoningItem(ChatMessageViewModel source, bool expandWhileStreaming)
        : base(
            $"message:reasoning:{source.Message.Id}",
            typeof(ReasoningItem),
            TranscriptVirtualization.EstimateReasoningHeight(source.Content, expandWhileStreaming && source.IsStreaming))
    {
        _content = source.Content;
        _isActive = source.IsStreaming;
        _isExpanded = expandWhileStreaming && source.IsStreaming;

        // Only subscribe while streaming. Once done, content is final.
        if (source.IsStreaming)
        {
            _source = source;
            _expandWhileStreaming = expandWhileStreaming;
            source.PropertyChanged += OnSourcePropertyChanged;
        }
    }

    private void OnSourcePropertyChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatMessageViewModel.Content) && _source is not null)
        {
            Content = _source.Content;
            // Skip expensive height estimation during streaming — recalculated when streaming ends.
        }
        else if (e.PropertyName == nameof(ChatMessageViewModel.IsStreaming) && _source is not null && !_source.IsStreaming)
        {
            IsActive = false;
            if (_expandWhileStreaming)
                IsExpanded = false;
            UpdateVirtualizationHeightHint(TranscriptVirtualization.EstimateReasoningHeight(Content, IsExpanded || IsActive));
            _source.PropertyChanged -= OnSourcePropertyChanged;
        }
    }

    partial void OnIsExpandedChanged(bool value)
    {
        UpdateVirtualizationHeightHint(TranscriptVirtualization.EstimateReasoningHeight(Content, value || IsActive));
    }
}

// ── Tool group (collapsible container of tool calls) ─

public partial class ToolGroupItem : TranscriptItem
{
    [ObservableProperty] private string _label;
    [ObservableProperty] private string? _meta;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private double _progressValue = -1;
    [ObservableProperty] private bool _isExpanded;

    public ObservableCollection<ToolCallItemBase> ToolCalls { get; } = [];

    public ToolGroupItem(string label, string? stableId = null)
        : base(stableId ?? TranscriptVirtualization.CreateRuntimeKey("tool-group"), typeof(ToolGroupItem), 96d)
    {
        _label = label;
        ToolCalls.CollectionChanged += OnToolCallsChanged;
        RefreshHeightHint();
    }

    partial void OnIsExpandedChanged(bool value) => RefreshHeightHint();

    private void OnToolCallsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (var call in e.OldItems.OfType<ToolCallItemBase>())
                call.PropertyChanged -= OnToolCallPropertyChanged;
        }

        if (e.NewItems is not null)
        {
            foreach (var call in e.NewItems.OfType<ToolCallItemBase>())
                call.PropertyChanged += OnToolCallPropertyChanged;
        }

        RefreshHeightHint();
    }

    private void OnToolCallPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ToolCallItemBase.EstimatedHeightHint))
            RefreshHeightHint();
    }

    private void RefreshHeightHint()
    {
        UpdateVirtualizationHeightHint(TranscriptVirtualization.EstimateToolGroupHeight(Label, ToolCalls, IsExpanded));
    }
}

// ── Single tool (flattened — rendered as StrataThink pill) ─

public partial class SingleToolItem : TranscriptItem
{
    public ToolCallItemBase Inner { get; }

    [ObservableProperty] private bool _isExpanded;

    public string Label => Inner switch
    {
        ToolCallItem tc => tc.ToolName,
        TerminalPreviewItem tp => tp.ToolName,
        TodoProgressItem todo => todo.ToolName,
        _ => ""
    };

    public bool IsActive => Inner switch
    {
        ToolCallItem tc => tc.Status == StrataAiToolCallStatus.InProgress,
        TerminalPreviewItem tp => tp.Status == StrataAiToolCallStatus.InProgress,
        TodoProgressItem todo => todo.Status == StrataAiToolCallStatus.InProgress,
        _ => false
    };

    public string? Meta
    {
        get
        {
            double ms = Inner switch
            {
                ToolCallItem tc => tc.DurationMs,
                TerminalPreviewItem tp => tp.DurationMs,
                _ => 0
            };
            if (ms <= 0) return null;
            return ms >= 1000 ? $"{ms / 1000:F1}s" : $"{ms:F0} ms";
        }
    }

    public string? InputParameters => Inner switch
    {
        ToolCallItem tc => tc.InputParameters,
        TodoProgressItem todo => todo.InputParameters,
        _ => null
    };

    public string? MoreInfo => Inner switch
    {
        ToolCallItem tc => tc.MoreInfo,
        _ => null
    };

    // Terminal-specific
    public string? TerminalCommand => Inner is TerminalPreviewItem tp ? tp.Command : null;
    public string? TerminalOutput => Inner is TerminalPreviewItem tp && !string.IsNullOrWhiteSpace(tp.Output) ? tp.Output : null;
    public bool IsTerminal => Inner is TerminalPreviewItem;

    public bool HasContent => !string.IsNullOrWhiteSpace(InputParameters) || !string.IsNullOrWhiteSpace(MoreInfo);

    public bool HasDiff => Inner is ToolCallItem { HasDiff: true };
    public ICommand? ShowDiffCommand => Inner is ToolCallItem tc ? tc.ShowDiffCommand : null;

    public SingleToolItem(ToolCallItemBase inner)
        : base($"single:{inner.StableId}", typeof(SingleToolItem), inner.EstimatedHeightHint)
    {
        Inner = inner;
    }
}

// ── Base for items inside a tool group ───────────────

public abstract partial class ToolCallItemBase : ObservableObject
{
    protected ToolCallItemBase(string stableId, double estimatedHeightHint)
    {
        StableId = stableId;
        EstimatedHeightHint = estimatedHeightHint;
    }

    public string StableId { get; }
    public double EstimatedHeightHint { get; protected set; }

    protected void UpdateEstimatedHeightHint(double estimatedHeightHint)
    {
        var newHeight = Math.Max(24d, estimatedHeightHint);
        if (Math.Abs(EstimatedHeightHint - newHeight) < 0.5d)
            return;

        EstimatedHeightHint = newHeight;
        OnPropertyChanged(nameof(EstimatedHeightHint));
    }
}

// ── Regular tool call ────────────────────────────────

public partial class ToolCallItem : ToolCallItemBase
{
    [ObservableProperty] private string _toolName;
    [ObservableProperty] private StrataAiToolCallStatus _status;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private string? _inputParameters;
    [ObservableProperty] private string? _moreInfo;
    [ObservableProperty] private double _durationMs;
    [ObservableProperty] private bool _hasDiff;
    [ObservableProperty] private bool _isCompact;

    public string? DiffFilePath { get; set; }
    public string? DiffToolName { get; set; }
    public List<(string? OldText, string? NewText)>? DiffEdits { get; set; }
    public Action<FileChangeItem>? ShowFileChangeAction { get; set; }

    public ToolCallItem(string toolName, StrataAiToolCallStatus status, string? stableId = null)
        : base(stableId ?? TranscriptVirtualization.CreateRuntimeKey("tool"), 88d)
    {
        _toolName = toolName;
        _status = status;
        RefreshHeightHint();
    }

    partial void OnInputParametersChanged(string? value) => RefreshHeightHint();
    partial void OnMoreInfoChanged(string? value) => RefreshHeightHint();
    partial void OnHasDiffChanged(bool value) => RefreshHeightHint();
    partial void OnIsCompactChanged(bool value) => RefreshHeightHint();

    private void RefreshHeightHint()
    {
        UpdateEstimatedHeightHint(TranscriptVirtualization.EstimateToolCallHeight(InputParameters, MoreInfo, HasDiff, IsCompact));
    }

    [RelayCommand]
    private void ShowDiff()
    {
        if (DiffFilePath is null || ShowFileChangeAction is null) return;
        var isCreate = DiffToolName is not null && ToolDisplayHelper.IsFileCreateTool(DiffToolName);
        var item = new FileChangeItem(DiffFilePath, isCreate, null);
        if (DiffEdits is not null)
            foreach (var (old, @new) in DiffEdits)
                item.AddEdit(old, @new);
        ShowFileChangeAction.Invoke(item);
    }
}

// ── Terminal preview (powershell output) ─────────────

public partial class TerminalPreviewItem : ToolCallItemBase
{
    [ObservableProperty] private string _toolName;
    [ObservableProperty] private string _command;
    [ObservableProperty] private string _output = "";
    [ObservableProperty] private StrataAiToolCallStatus _status;
    [ObservableProperty] private double _durationMs;
    [ObservableProperty] private bool _isExpanded;

    public TerminalPreviewItem(string toolName, string command, StrataAiToolCallStatus status, string? stableId = null)
        : base(stableId ?? TranscriptVirtualization.CreateRuntimeKey("terminal"), 168d)
    {
        _toolName = toolName;
        _command = command;
        _status = status;
        RefreshHeightHint();
    }

    partial void OnCommandChanged(string value) => RefreshHeightHint();
    partial void OnOutputChanged(string value) => RefreshHeightHint();
    partial void OnIsExpandedChanged(bool value) => RefreshHeightHint();

    private void RefreshHeightHint()
    {
        UpdateEstimatedHeightHint(TranscriptVirtualization.EstimateTerminalHeight(Command, Output, IsExpanded));
    }
}

// ── Todo progress (inside tool group) ────────────────

public partial class TodoProgressItem : ToolCallItemBase
{
    [ObservableProperty] private string _toolName;
    [ObservableProperty] private StrataAiToolCallStatus _status;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private string? _inputParameters;
    [ObservableProperty] private string? _moreInfo;

    public TodoProgressItem(string toolName, StrataAiToolCallStatus status, string? stableId = null)
        : base(stableId ?? TranscriptVirtualization.CreateRuntimeKey("todo"), 140d)
    {
        _toolName = toolName;
        _status = status;
        RefreshHeightHint();
    }

    partial void OnInputParametersChanged(string? value) => RefreshHeightHint();

    private void RefreshHeightHint()
    {
        UpdateEstimatedHeightHint(TranscriptVirtualization.EstimateTodoHeight(InputParameters));
    }
}

// ── Question card ────────────────────────────────────

public partial class QuestionItem : TranscriptItem
{
    private readonly Action<string, string>? _submitAction;
    private bool _isSubmitting;

    public string QuestionId { get; }
    [ObservableProperty] private string _question;
    [ObservableProperty] private string _options;
    [ObservableProperty] private bool _allowFreeText;
    [ObservableProperty] private bool _allowMultiSelect;
    [ObservableProperty] private string? _selectedAnswer;
    [ObservableProperty] private bool _isAnswered;

    public QuestionItem(string questionId, string question, string options, bool allowFreeText,
        Action<string, string>? submitAction = null, bool allowMultiSelect = false)
        : base(
            $"question:{questionId}",
            typeof(QuestionItem),
            TranscriptVirtualization.EstimateQuestionHeight(question, options, allowFreeText))
    {
        QuestionId = questionId;
        _question = question;
        _options = options;
        _allowFreeText = allowFreeText;
        _allowMultiSelect = allowMultiSelect;
        _submitAction = submitAction;
    }

    partial void OnIsAnsweredChanged(bool value)
    {
        if (value && !_isSubmitting && !string.IsNullOrEmpty(SelectedAnswer))
        {
            _isSubmitting = true;
            _submitAction?.Invoke(QuestionId, SelectedAnswer);
            _isSubmitting = false;
        }
    }

    public void Submit(string answer)
    {
        _isSubmitting = true;
        SelectedAnswer = answer;
        IsAnswered = true;
        _submitAction?.Invoke(QuestionId, answer);
        _isSubmitting = false;
    }

    partial void OnQuestionChanged(string value)
    {
        UpdateVirtualizationHeightHint(TranscriptVirtualization.EstimateQuestionHeight(Question, Options, AllowFreeText));
    }

    partial void OnOptionsChanged(string value)
    {
        UpdateVirtualizationHeightHint(TranscriptVirtualization.EstimateQuestionHeight(Question, Options, AllowFreeText));
    }

    partial void OnAllowFreeTextChanged(bool value)
    {
        UpdateVirtualizationHeightHint(TranscriptVirtualization.EstimateQuestionHeight(Question, Options, AllowFreeText));
    }
}

// ── Typing indicator ─────────────────────────────────

public partial class TypingIndicatorItem : TranscriptItem
{
    [ObservableProperty] private string _label;
    [ObservableProperty] private bool _isActive;

    public TypingIndicatorItem(string label)
        : base("typing-indicator", typeof(TypingIndicatorItem), 52d)
    {
        _label = label;
        _isActive = true;
    }
}

// ── Turn summary (collapses tool groups + reasoning) ─

public partial class TurnSummaryItem : TranscriptItem
{
    [ObservableProperty] private string _label;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _hasFailures;

    public ObservableCollection<TranscriptItem> InnerItems { get; } = [];

    public TurnSummaryItem(string label, string? stableId = null)
        : base(stableId ?? TranscriptVirtualization.CreateRuntimeKey("turn-summary"), typeof(TurnSummaryItem), 92d)
    {
        _label = label;
        InnerItems.CollectionChanged += OnInnerItemsChanged;
        RefreshHeightHint();
    }

    partial void OnIsExpandedChanged(bool value) => RefreshHeightHint();

    private void OnInnerItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshHeightHint();

    private void RefreshHeightHint()
    {
        UpdateVirtualizationHeightHint(TranscriptVirtualization.EstimateTurnSummaryHeight(Label, InnerItems.Count, IsExpanded));
    }
}

// ── File attachment display item ─────────────────────

public partial class FileAttachmentItem : ObservableObject
{
    private readonly Action<string>? _removeAction;

    public string FilePath { get; }
    public string FileName { get; }
    public string? FileSize { get; }
    public bool IsRemovable { get; }

    public FileAttachmentItem(string filePath, bool isRemovable = false, Action<string>? removeAction = null)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        IsRemovable = isRemovable;
        _removeAction = removeAction;

        try
        {
            var info = new FileInfo(filePath);
            if (info.Exists)
                FileSize = ToolDisplayHelper.FormatFileSize(info.Length);
        }
        catch { /* ignore */ }
    }

    [RelayCommand]
    private void Open()
    {
        try { Process.Start(new ProcessStartInfo(FilePath) { UseShellExecute = true }); }
        catch { /* ignore if file doesn't exist */ }
    }

    [RelayCommand]
    private void Remove()
    {
        if (IsRemovable)
            _removeAction?.Invoke(FilePath);
    }
}

// ── Source citation display item ─────────────────────

public partial class SourceItem : ObservableObject
{
    public string Title { get; }
    public string Domain { get; }
    public string Url { get; }

    public SourceItem(SearchSource source)
    {
        Title = source.Title;
        Url = source.Url;
        Domain = ExtractDomain(source.Url);
    }

    [RelayCommand]
    private void Open()
    {
        try { Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    private static string ExtractDomain(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return uri.Host.Replace("www.", "");
        return url;
    }
}

// ── File change display item ─────────────────────────

public partial class FileChangeItem : ObservableObject
{
    public string FilePath { get; }
    public string FileName { get; }
    public string ActionIcon { get; }
    public string ActionLabel { get; }
    public string? Directory { get; }
    public bool IsCreate { get; }
    public int LinesAdded { get; private set; }
    public int LinesRemoved { get; private set; }
    public string StatsAdded => $"+{LinesAdded}";
    public string StatsRemoved => LinesRemoved > 0 ? $"−{LinesRemoved}" : "";
    public bool HasRemovals => LinesRemoved > 0;

    /// <summary>All edits applied to this file (old text → new text pairs).</summary>
    public List<(string? OldText, string? NewText)> Edits { get; } = [];

    private readonly Action<FileChangeItem>? _showDiffAction;

    public FileChangeItem(string filePath, bool isCreate, Action<FileChangeItem>? showDiffAction = null)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        Directory = Path.GetDirectoryName(filePath);
        IsCreate = isCreate;
        _showDiffAction = showDiffAction;

        ActionIcon = isCreate ? "📄" : "📝";
        ActionLabel = isCreate ? Loc.FileChange_Created : Loc.FileChange_Modified;
    }

    /// <summary>Adds an edit and updates line stats.</summary>
    public void AddEdit(string? oldText, string? newText)
    {
        Edits.Add((oldText, newText));
        LinesAdded += CountLines(newText);
        LinesRemoved += CountLines(oldText);
    }

    /// <summary>
    /// For created files where we couldn't extract content from tool args,
    /// read the file to get accurate line count.
    /// </summary>
    public void EnsureStatsForCreatedFile()
    {
        if (!IsCreate || LinesAdded > 0) return;
        try
        {
            if (File.Exists(FilePath))
                LinesAdded = File.ReadAllLines(FilePath).Length;
        }
        catch { /* ignore */ }
    }

    private static int CountLines(string? text)
        => string.IsNullOrEmpty(text) ? 0 : text.Split('\n').Length;

    [RelayCommand]
    private void ShowDiff() => _showDiffAction?.Invoke(this);
}

// ── Git file change display item ─────────────────────

public partial class GitFileChangeViewModel : ObservableObject
{
    public GitFileChange Change { get; }
    private readonly Action<GitFileChangeViewModel>? _showDiffAction;

    public string FileName => Change.FileName;
    public string? Directory => Change.Directory;
    public string KindIcon => Change.KindIcon;
    public string KindLabel => Change.KindLabel;
    public GitChangeKind Kind => Change.Kind;
    public int LinesAdded => Change.LinesAdded;
    public int LinesRemoved => Change.LinesRemoved;
    public bool HasStats => LinesAdded > 0 || LinesRemoved > 0;

    public GitFileChangeViewModel(GitFileChange change, Action<GitFileChangeViewModel>? showDiffAction = null)
    {
        Change = change;
        _showDiffAction = showDiffAction;
    }

    [RelayCommand]
    private void ShowDiff() => _showDiffAction?.Invoke(this);
}

// ── File changes summary (standalone transcript item at end of turn) ──

public partial class FileChangesSummaryItem : TranscriptItem
{
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string _totalStatsAdded = "";
    [ObservableProperty] private string _totalStatsRemoved = "";
    [ObservableProperty] private bool _hasTotalRemovals;

    public ObservableCollection<FileChangeItem> FileChanges { get; } = [];

    public FileChangesSummaryItem(List<FileChangeItem> fileChanges, string? stableId = null)
        : base(
            stableId ?? TranscriptVirtualization.CreateRuntimeKey("file-changes"),
            typeof(FileChangesSummaryItem),
            TranscriptVirtualization.EstimateFileChangesHeight(fileChanges))
    {
        foreach (var fc in fileChanges)
            FileChanges.Add(fc);

        var totalAdded = fileChanges.Sum(fc => fc.LinesAdded);
        var totalRemoved = fileChanges.Sum(fc => fc.LinesRemoved);
        TotalStatsAdded = $"+{totalAdded}";
        TotalStatsRemoved = totalRemoved > 0 ? $"−{totalRemoved}" : "";
        HasTotalRemovals = totalRemoved > 0;
        Label = fileChanges.Count == 1 ? Loc.FileChanges_One : string.Format(Loc.FileChanges_N, fileChanges.Count);
    }
}

// ── Plan card (inline indicator when agent creates/updates a plan) ──

public partial class PlanCardItem : TranscriptItem
{
    private readonly Action? _openAction;

    [ObservableProperty] private string _statusText;

    public PlanCardItem(string statusText, Action? openAction, string? stableId = null)
        : base(
            stableId ?? TranscriptVirtualization.CreateRuntimeKey("plan-card"),
            typeof(PlanCardItem),
            TranscriptVirtualization.EstimatePlanCardHeight(statusText))
    {
        _statusText = statusText;
        _openAction = openAction;
    }

    partial void OnStatusTextChanged(string value)
    {
        UpdateVirtualizationHeightHint(TranscriptVirtualization.EstimatePlanCardHeight(value));
    }

    [RelayCommand]
    private void Open() => _openAction?.Invoke();
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using StrataTheme.Controls;

namespace Lumi.ViewModels;

public class TranscriptBuilder
{
    private readonly DataStore _dataStore;
    private readonly Action<FileChangeItem> _showDiffAction;
    private readonly Action<string, string> _submitQuestionAnswerAction;
    private readonly Func<ChatMessage, bool, Task> _resendFromMessageAction;

    // ── Transcript building state ──
    private ToolGroupItem? _currentToolGroup;
    private int _currentToolGroupCount;
    private TodoProgressItem? _currentTodoToolCall;
    private TodoProgressState? _currentTodoProgress;
    private int _todoUpdateCount;
    private string? _currentIntentText;
    private TypingIndicatorItem? _typingIndicator;
    private readonly Dictionary<string, TerminalPreviewItem> _terminalPreviewsByToolCallId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _toolStartTimes = [];
    public List<FileAttachmentItem> PendingToolFileChips { get; } = [];
    public List<(string FilePath, string ToolName, string? OldText, string? NewText)> PendingFileEdits { get; } = [];
    private IList<TranscriptItem>? _rebuildTarget;
    public bool IsRebuildingTranscript { get; set; }

    public HashSet<string> ShownFileChips { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<SkillReference> PendingFetchedSkillRefs { get; } = [];
    private PlanCardItem? _pendingPlanCard;

    /// <summary>Sets the live collection that the builder operates on for non-rebuild operations.</summary>
    public void SetLiveTarget(ObservableCollection<TranscriptItem> target) => _liveTarget = target;

    /// <summary>The live transcript collection returned by the last <see cref="Rebuild"/> call.</summary>
    private ObservableCollection<TranscriptItem>? _liveTarget;

    private sealed class TodoProgressState
    {
        public string ToolStatus { get; set; } = "InProgress";
        public int Total { get; set; }
        public int Completed { get; set; }
        public int Failed { get; set; }
    }

    public TranscriptBuilder(
        DataStore dataStore,
        Action<FileChangeItem> showDiffAction,
        Action<string, string> submitQuestionAnswerAction,
        Func<ChatMessage, bool, Task> resendFromMessageAction)
    {
        _dataStore = dataStore;
        _showDiffAction = showDiffAction;
        _submitQuestionAnswerAction = submitQuestionAnswerAction;
        _resendFromMessageAction = resendFromMessageAction;
    }

    /// <summary>
    /// Rebuilds transcript items from the given messages collection.
    /// Returns a new <see cref="ObservableCollection{TranscriptItem}"/> and stores it as the live target.
    /// </summary>
    public ObservableCollection<TranscriptItem> Rebuild(IEnumerable<ChatMessageViewModel> messages)
    {
        IsRebuildingTranscript = true;
        var tempItems = new List<TranscriptItem>();
        _rebuildTarget = tempItems;
        ResetState();

        foreach (var msg in messages)
            ProcessMessageToTranscript(msg);

        CloseCurrentToolGroup();
        FlushPendingFileEdits();
        FlushPendingPlanCard();
        CollapseAllCompletedTurns();

        _rebuildTarget = null;
        var result = new ObservableCollection<TranscriptItem>(tempItems);
        _liveTarget = result;
        IsRebuildingTranscript = false;
        return result;
    }

    /// <summary>Resets all transcript building state.</summary>
    public void ResetState()
    {
        _currentToolGroup = null;
        _currentToolGroupCount = 0;
        _currentTodoToolCall = null;
        _currentTodoProgress = null;
        _todoUpdateCount = 0;
        _currentIntentText = null;
        _typingIndicator = null;
        _pendingPlanCard = null;
        _terminalPreviewsByToolCallId.Clear();
        _toolStartTimes.Clear();
        PendingToolFileChips.Clear();
        PendingFileEdits.Clear();
        PendingFetchedSkillRefs.Clear();
        ShownFileChips.Clear();
    }

    /// <summary>Processes a single ChatMessageViewModel into the appropriate TranscriptItem(s).</summary>
    public void ProcessMessageToTranscript(ChatMessageViewModel msgVm)
    {
        var showToolCalls = _dataStore.Data.Settings.ShowToolCalls;
        var showReasoning = _dataStore.Data.Settings.ShowReasoning;
        var showTimestamps = _dataStore.Data.Settings.ShowTimestamps;
        var expandReasoning = _dataStore.Data.Settings.ExpandReasoningWhileStreaming;

        if (msgVm.Role == "tool")
            ProcessToolMessage(msgVm, showToolCalls, showTimestamps);
        else if (msgVm.Role == "reasoning")
            ProcessReasoningMessage(msgVm, showReasoning, expandReasoning);
        else
            ProcessChatMessage(msgVm, showTimestamps);
    }

    private void ProcessToolMessage(ChatMessageViewModel msgVm, bool showToolCalls, bool showTimestamps)
    {
        var toolName = msgVm.ToolName ?? "";
        var initialStatus = msgVm.ToolStatus switch
        {
            "Completed" => StrataAiToolCallStatus.Completed,
            "Failed" => StrataAiToolCallStatus.Failed,
            _ => StrataAiToolCallStatus.InProgress
        };

        // Invisible tool calls
        if (toolName is "stop_powershell" or "write_powershell" or "read_powershell"
            or "task_complete" or "read_agent" or "list_agents")
            return;

        // Question card — rendered as QuestionItem
        if (toolName is "ask_question")
        {
            if (IsRebuildingTranscript)
            {
                var question = ToolDisplayHelper.ExtractJsonField(msgVm.Content, "question") ?? "";
                var opts = ToolDisplayHelper.ExtractJsonField(msgVm.Content, "options") ?? "";
                var answer = msgVm.Message.ToolOutput;
                if (!string.IsNullOrEmpty(answer) && answer.StartsWith("User answered: "))
                    answer = answer["User answered: ".Length..];

                CloseCurrentToolGroup();
                var card = new QuestionItem("replay_" + msgVm.Message.Id, question, opts, false, _submitQuestionAnswerAction);
                if (!string.IsNullOrEmpty(answer))
                {
                    card.SelectedAnswer = answer;
                    card.IsAnswered = true;
                }
                InsertBeforeTypingIndicator(card);
            }
            // Live question cards are created via the QuestionAsked event
            return;
        }

        // announce_file — collect for file chip display
        if (toolName == "announce_file")
        {
            var filePath = ToolDisplayHelper.ExtractJsonField(msgVm.Content, "filePath");
            if (filePath is not null && File.Exists(filePath) && ShownFileChips.Add(filePath))
                PendingToolFileChips.Add(new FileAttachmentItem(filePath));
            return;
        }

        // fetch_skill — collect skill reference
        if (toolName == "fetch_skill")
        {
            var skillName = ToolDisplayHelper.ExtractJsonField(msgVm.Content, "name");
            if (!string.IsNullOrEmpty(skillName))
            {
                var skill = _dataStore.Data.Skills.FirstOrDefault(s => s.Name.Equals(skillName, StringComparison.OrdinalIgnoreCase));
                PendingFetchedSkillRefs.Add(new SkillReference
                {
                    Name = skillName,
                    Glyph = skill?.IconGlyph ?? "\u26A1"
                });
            }
            return;
        }

        // report_intent — capture intent text for group label
        if (toolName == "report_intent")
        {
            var intentText = ToolDisplayHelper.ExtractJsonField(msgVm.Content, "intent");
            if (!string.IsNullOrEmpty(intentText))
            {
                _currentIntentText = intentText;
                if (showToolCalls)
                {
                    EnsureCurrentToolGroup(initialStatus);
                    UpdateToolGroupLabel();
                }
            }
            return;
        }

        // Todo progress
        if (toolName is "update_todo" or "manage_todo_list")
        {
            if (!showToolCalls) return;

            var steps = ToolDisplayHelper.ParseTodoSteps(msgVm.Content);
            if (steps.Count == 0) return;

            EnsureCurrentToolGroup(initialStatus);
            _todoUpdateCount++;
            UpsertTodoProgressToolCall(steps, msgVm.ToolStatus ?? "InProgress");

            if (_currentToolGroup is not null && initialStatus == StrataAiToolCallStatus.InProgress && !IsRebuildingTranscript)
                _currentToolGroup.IsExpanded = true;

            UpdateToolGroupLabel();

            if (!IsRebuildingTranscript)
            {
                var capturedGroup = _currentToolGroup;
                var capturedTodoProgress = _currentTodoProgress;
                var capturedTodoToolCall = _currentTodoToolCall;
                msgVm.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(ChatMessageViewModel.ToolStatus) && capturedTodoProgress is not null)
                    {
                        capturedTodoProgress.ToolStatus = msgVm.ToolStatus ?? "InProgress";
                        if (capturedTodoToolCall is not null)
                        {
                            capturedTodoToolCall.Status = msgVm.ToolStatus switch
                            {
                                "Completed" => StrataAiToolCallStatus.Completed,
                                "Failed" => StrataAiToolCallStatus.Failed,
                                _ => StrataAiToolCallStatus.InProgress
                            };
                        }
                        UpdateToolGroupState(capturedGroup);
                    }
                };
            }
            return;
        }

        if (!showToolCalls) return;

        var (friendlyName, friendlyInfo) = ToolDisplayHelper.GetFriendlyToolDisplay(toolName, msgVm.Author, msgVm.Content);
        friendlyName = $"{ToolDisplayHelper.GetToolGlyph(toolName)} {friendlyName}";

        var toolCallId = msgVm.Message.ToolCallId;
        if (toolCallId is not null && initialStatus == StrataAiToolCallStatus.InProgress)
            _toolStartTimes[toolCallId] = Stopwatch.GetTimestamp();

        // Powershell → terminal preview
        if (toolName == "powershell")
        {
            var command = ToolDisplayHelper.ExtractJsonField(msgVm.Content, "command") ?? "";
            var termPreview = new TerminalPreviewItem(friendlyName, command, initialStatus)
            {
                Output = msgVm.Message.ToolOutput ?? string.Empty,
                IsExpanded = !IsRebuildingTranscript,
            };
            if (toolCallId is not null)
                _terminalPreviewsByToolCallId[toolCallId] = termPreview;

            EnsureCurrentToolGroup(initialStatus);
            var capturedTermGroup = _currentToolGroup!;

            if (!IsRebuildingTranscript)
            {
                msgVm.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName != nameof(ChatMessageViewModel.ToolStatus)) return;
                    termPreview.Status = msgVm.ToolStatus switch
                    {
                        "Completed" => StrataAiToolCallStatus.Completed,
                        "Failed" => StrataAiToolCallStatus.Failed,
                        _ => StrataAiToolCallStatus.InProgress
                    };
                    if (toolCallId is not null && termPreview.Status is not StrataAiToolCallStatus.InProgress
                        && _toolStartTimes.TryGetValue(toolCallId, out var startTick))
                    {
                        termPreview.DurationMs = Stopwatch.GetElapsedTime(startTick).TotalMilliseconds;
                        _toolStartTimes.Remove(toolCallId);
                    }
                    UpdateToolGroupState(capturedTermGroup);
                };
            }
            _currentToolGroup!.ToolCalls.Add(termPreview);
            _currentToolGroupCount++;

            if (_currentToolGroup is not null && initialStatus == StrataAiToolCallStatus.InProgress && !IsRebuildingTranscript)
                _currentToolGroup.IsExpanded = true;

            UpdateToolGroupLabel();
            return;
        }

        // Regular tool call
        var toolCall = new ToolCallItem(friendlyName, initialStatus)
        {
            InputParameters = ToolDisplayHelper.FormatToolArgsFriendly(toolName, msgVm.Content),
            MoreInfo = friendlyInfo,
            IsCompact = ToolDisplayHelper.IsCompactEligible(toolName)
                && initialStatus == StrataAiToolCallStatus.Completed,
        };

        // File-edit tools: collect diff data (skip failed tools — the edit didn't apply)
        if (ToolDisplayHelper.IsFileEditTool(toolName) && initialStatus != StrataAiToolCallStatus.Failed)
        {
            var diffs = ToolDisplayHelper.ExtractAllDiffs(toolName, msgVm.Content);
            foreach (var d in diffs)
                PendingFileEdits.Add((d.FilePath, toolName, d.OldText, d.NewText));

            if (diffs.Count > 0)
            {
                toolCall.HasDiff = true;
                toolCall.DiffFilePath = diffs[0].FilePath;
                toolCall.DiffToolName = toolName;
                toolCall.DiffEdits = diffs.Select(d => (d.OldText, d.NewText)).ToList();
                toolCall.ShowFileChangeAction = _showDiffAction;
            }
        }

        EnsureCurrentToolGroup(initialStatus);
        var capturedToolGroup = _currentToolGroup!;

        if (!IsRebuildingTranscript)
        {
            msgVm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != nameof(ChatMessageViewModel.ToolStatus)) return;
                toolCall.Status = msgVm.ToolStatus switch
                {
                    "Completed" => StrataAiToolCallStatus.Completed,
                    "Failed" => StrataAiToolCallStatus.Failed,
                    _ => StrataAiToolCallStatus.InProgress
                };
                if (toolCallId is not null && toolCall.Status is not StrataAiToolCallStatus.InProgress
                    && _toolStartTimes.TryGetValue(toolCallId, out var startTick))
                {
                    toolCall.DurationMs = Stopwatch.GetElapsedTime(startTick).TotalMilliseconds;
                    _toolStartTimes.Remove(toolCallId);
                }
                // Remove pending file edits if this file-edit tool failed
                if (toolCall.Status == StrataAiToolCallStatus.Failed && toolCall.HasDiff && toolCall.DiffFilePath is not null)
                    PendingFileEdits.RemoveAll(fe => fe.FilePath == toolCall.DiffFilePath);
                UpdateToolGroupState(capturedToolGroup);
            };
        }
        _currentToolGroup!.ToolCalls.Add(toolCall);
        _currentToolGroupCount++;
        UpdateToolGroupLabel();
    }

    private void ProcessReasoningMessage(ChatMessageViewModel msgVm, bool showReasoning, bool expandWhileStreaming)
    {
        CloseCurrentToolGroup();
        if (!showReasoning) return;

        var item = new ReasoningItem(msgVm, expandWhileStreaming);
        InsertBeforeTypingIndicator(item);
    }

    private void ProcessChatMessage(ChatMessageViewModel msgVm, bool showTimestamps)
    {
        CloseCurrentToolGroup();

        if (msgVm.Role == "user")
        {
            // Flush any pending file edits from the previous turn
            FlushPendingFileEdits();
            FlushPendingPlanCard();
            var item = new UserMessageItem(msgVm, showTimestamps, (msg, edited) => _ = _resendFromMessageAction(msg, edited));
            InsertBeforeTypingIndicator(item);
        }
        else if (msgVm.Role == "error")
        {
            var item = new ErrorMessageItem(msgVm, showTimestamps);
            InsertBeforeTypingIndicator(item);
        }
        else // assistant or system
        {
            var item = new AssistantMessageItem(msgVm, showTimestamps);

            // For completed messages during rebuild, apply extras (but NOT file changes — those flush at end of turn)
            if (!msgVm.IsStreaming && (PendingToolFileChips.Count > 0
                || msgVm.Message.Sources.Count > 0 || msgVm.Message.ActiveSkills.Count > 0))
            {
                item.ApplyExtras(
                    PendingToolFileChips.Count > 0 ? PendingToolFileChips.ToList() : null);
                PendingToolFileChips.Clear();
                PendingFetchedSkillRefs.Clear();
            }

            InsertBeforeTypingIndicator(item);

            // For streaming messages, apply extras when streaming ends
            if (msgVm.IsStreaming)
            {
                var capturedItem = item;
                msgVm.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(ChatMessageViewModel.IsStreaming) && !msgVm.IsStreaming)
                    {
                        capturedItem.ApplyExtras(
                            PendingToolFileChips.Count > 0 ? PendingToolFileChips.ToList() : null);
                        PendingToolFileChips.Clear();
                        PendingFetchedSkillRefs.Clear();

                        // Collapse completed turn blocks after assistant finishes
                        CollapseCompletedTurnBlocks(capturedItem);

                        // Flush file changes and plan card at end of turn
                        FlushPendingFileEdits();
                        FlushPendingPlanCard();
                    }
                };
            }
        }
    }

    /// <summary>
    /// Inserts a FileChangesSummaryItem for any accumulated file edits, then clears the pending list.
    /// Called at the end of a turn (when user message arrives or streaming ends).
    /// </summary>
    private void FlushPendingFileEdits()
    {
        if (PendingFileEdits.Count == 0) return;
        InsertBeforeTypingIndicator(new FileChangesSummaryItem(GroupFileEdits()));
        PendingFileEdits.Clear();
    }

    /// <summary>
    /// Stages a plan card for insertion at the end of the current turn.
    /// If a plan card was already staged, updates its status text instead of creating a duplicate.
    /// </summary>
    public void SetPendingPlanCard(string statusText, Action openAction)
    {
        if (_pendingPlanCard is not null)
        {
            _pendingPlanCard.StatusText = statusText;
            return;
        }
        _pendingPlanCard = new PlanCardItem(statusText, openAction);
    }

    /// <summary>
    /// Inserts the staged plan card into the transcript, then clears it.
    /// Called at the end of a turn (alongside FlushPendingFileEdits).
    /// </summary>
    private void FlushPendingPlanCard()
    {
        if (_pendingPlanCard is null) return;
        InsertBeforeTypingIndicator(_pendingPlanCard);
        _pendingPlanCard = null;
    }

    /// <summary>Groups raw pending edits by file path into one FileChangeItem per unique file.</summary>
    private List<FileChangeItem> GroupFileEdits()
    {
        var grouped = new Dictionary<string, FileChangeItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var (filePath, toolName, oldText, newText) in PendingFileEdits)
        {
            if (!grouped.TryGetValue(filePath, out var item))
            {
                var isCreate = ToolDisplayHelper.IsFileCreateTool(toolName);
                item = new FileChangeItem(filePath, isCreate, _showDiffAction);
                grouped[filePath] = item;
            }
            item.AddEdit(oldText, newText);
        }
        foreach (var item in grouped.Values)
            item.EnsureStatsForCreatedFile();
        return grouped.Values.ToList();
    }

    private void EnsureCurrentToolGroup(StrataAiToolCallStatus initialStatus)
    {
        if (_currentToolGroup is not null) return;

        _currentToolGroupCount = 0;
        _currentTodoToolCall = null;
        _currentTodoProgress = null;

        _currentToolGroup = new ToolGroupItem(
            _currentIntentText is not null ? _currentIntentText + "\u2026" : Loc.ToolGroup_Working)
        {
            IsActive = initialStatus == StrataAiToolCallStatus.InProgress,
            ProgressValue = -1,
        };

        InsertBeforeTypingIndicator(_currentToolGroup);
    }

    /// <summary>Closes the current tool group. Safe to call when no group is open.</summary>
    public void CloseCurrentToolGroup()
    {
        if (_currentToolGroup is null) return;

        var target = _rebuildTarget ?? (IList<TranscriptItem>?)_liveTarget;

        if (_currentToolGroupCount == 0)
        {
            target?.Remove(_currentToolGroup);
        }
        else
        {
            UpdateToolGroupLabel();

            // Flatten: replace a completed single-tool group with a SingleToolItem
            if (_currentToolGroupCount == 1 && !_currentToolGroup.IsActive
                && _currentToolGroup.ToolCalls.Count == 1 && target is not null)
            {
                var idx = target.IndexOf(_currentToolGroup);
                if (idx >= 0)
                {
                    var single = new SingleToolItem(_currentToolGroup.ToolCalls[0]);
                    target[idx] = single;
                }
            }
        }

        _currentToolGroup = null;
        _currentToolGroupCount = 0;
        _currentTodoToolCall = null;
        _currentTodoProgress = null;
        _todoUpdateCount = 0;
        _currentIntentText = null;
        _terminalPreviewsByToolCallId.Clear();
    }

    private void UpdateToolGroupLabel()
    {
        if (_currentToolGroup is null) return;
        UpdateToolGroupState(_currentToolGroup);
    }

    /// <summary>
    /// Updates a specific tool group's label, meta, progress, and IsActive state
    /// based on the current status of its tool calls. Safe to call after the group
    /// is no longer the _currentToolGroup (e.g., from late PropertyChanged callbacks).
    /// </summary>
    private void UpdateToolGroupState(ToolGroupItem? group)
    {
        if (group is null) return;

        // For the current group, use the richer state (todo progress, intent text, etc.)
        var isCurrent = ReferenceEquals(group, _currentToolGroup);

        if (isCurrent && _currentTodoProgress is not null && _currentTodoProgress.Total > 0)
        {
            var todoDone = _currentTodoProgress.Completed + _currentTodoProgress.Failed;
            var running = Math.Max(0, _currentTodoProgress.Total - todoDone);

            group.Label = Loc.ToolTodo_Title;
            group.Meta = _currentTodoProgress.Failed > 0
                ? string.Format(Loc.ToolTodo_MetaWithFailed, _currentTodoProgress.Completed, _currentTodoProgress.Total, _currentTodoProgress.Failed)
                : string.Format(Loc.ToolTodo_Meta, _currentTodoProgress.Completed, _currentTodoProgress.Total);

            if (_todoUpdateCount > 1)
                group.Meta += " · " + string.Format(Loc.ToolTodo_Updates, _todoUpdateCount);

            var progress = Math.Clamp((todoDone * 100d) / _currentTodoProgress.Total, 0d, 100d);
            group.ProgressValue = IsRebuildingTranscript ? -1 : progress;
            group.IsActive = running > 0 && _currentTodoProgress.ToolStatus != "Failed";

            if (!group.IsActive || IsRebuildingTranscript)
                group.IsExpanded = false;
            return;
        }

        var toolCount = isCurrent ? _currentToolGroupCount : group.ToolCalls.Count;
        var completedCount = 0;
        var failedCount = 0;
        foreach (var call in group.ToolCalls)
        {
            if (call is ToolCallItem tc)
            {
                if (tc.Status == StrataAiToolCallStatus.Completed) completedCount++;
                else if (tc.Status == StrataAiToolCallStatus.Failed) failedCount++;
            }
            else if (call is TerminalPreviewItem tp)
            {
                if (tp.Status == StrataAiToolCallStatus.Completed) completedCount++;
                else if (tp.Status == StrataAiToolCallStatus.Failed) failedCount++;
            }
            else if (call is TodoProgressItem todo)
            {
                if (todo.Status == StrataAiToolCallStatus.Completed) completedCount++;
                else if (todo.Status == StrataAiToolCallStatus.Failed) failedCount++;
            }
        }

        if (toolCount <= 0)
        {
            group.Meta = null;
            group.ProgressValue = -1;
            group.IsActive = true;
            group.Label = isCurrent && _currentIntentText is not null
                ? _currentIntentText + "\u2026" : Loc.ToolGroup_Working;
            return;
        }

        var intentText = isCurrent ? _currentIntentText : null;
        var allDone = completedCount + failedCount == toolCount && toolCount > 0;
        if (allDone)
        {
            group.Label = intentText is not null
                ? (failedCount > 0 ? string.Format(Loc.ToolGroup_FinishedWithFailed, intentText, failedCount) : intentText)
                : (failedCount > 0 ? string.Format(Loc.ToolGroup_FinishedFailed, failedCount)
                    : toolCount == 1 ? Loc.ToolGroup_Finished : string.Format(Loc.ToolGroup_FinishedCount, toolCount));
            group.IsActive = false;
            group.Meta = failedCount > 0
                ? string.Format(Loc.ToolGroup_MetaFailed, completedCount, toolCount, failedCount)
                : string.Format(Loc.ToolGroup_MetaDone, completedCount, toolCount);
            if (IsRebuildingTranscript) group.IsExpanded = false;

            // Live flatten: single-tool group just completed → replace with SingleToolItem
            if (toolCount == 1 && group.ToolCalls.Count == 1 && !IsRebuildingTranscript && _liveTarget is not null)
            {
                var idx = _liveTarget.IndexOf(group);
                if (idx >= 0)
                    _liveTarget[idx] = new SingleToolItem(group.ToolCalls[0]);
            }
        }
        else
        {
            group.IsActive = true;
            group.Label = intentText is not null
                ? intentText + "\u2026"
                : (toolCount == 1 ? Loc.ToolGroup_Working : string.Format(Loc.ToolGroup_WorkingCount, toolCount));
            var runningCount = Math.Max(0, toolCount - completedCount - failedCount);
            group.Meta = runningCount > 0
                ? string.Format(Loc.ToolGroup_MetaRunning, completedCount, toolCount, runningCount)
                : string.Format(Loc.ToolGroup_MetaDone, completedCount, toolCount);
            if (IsRebuildingTranscript) group.IsExpanded = false;
        }

        var genericProgress = toolCount > 0
            ? Math.Clamp(((completedCount + failedCount) * 100d) / toolCount, 0d, 100d) : -1;
        group.ProgressValue = IsRebuildingTranscript ? -1 : genericProgress;
    }

    private void UpsertTodoProgressToolCall(List<ToolDisplayHelper.TodoStepSnapshot> steps, string toolStatus)
    {
        var total = steps.Count;
        var completed = steps.Count(s => string.Equals(s.Status, "completed", StringComparison.OrdinalIgnoreCase));
        var failed = steps.Count(s => string.Equals(s.Status, "failed", StringComparison.OrdinalIgnoreCase));
        var detailsMd = ToolDisplayHelper.BuildTodoDetailsMarkdown(steps);

        if (_currentTodoProgress is null)
        {
            _currentTodoProgress = new TodoProgressState
            {
                ToolStatus = toolStatus,
                Total = total,
                Completed = completed,
                Failed = failed,
            };
        }
        else
        {
            _currentTodoProgress.ToolStatus = toolStatus;
            _currentTodoProgress.Total = total;
            _currentTodoProgress.Completed = completed;
            _currentTodoProgress.Failed = failed;
        }

        if (_currentTodoToolCall is null)
        {
            _currentTodoToolCall = new TodoProgressItem($"✅ {Loc.ToolTodo_Title}", StrataAiToolCallStatus.InProgress)
            {
                InputParameters = detailsMd,
            };
            _currentToolGroup?.ToolCalls.Add(_currentTodoToolCall);
        }
        else
        {
            _currentTodoToolCall.InputParameters = detailsMd;
        }
    }

    /// <summary>Collapses consecutive tool groups + reasoning + single tools before an assistant message into a TurnSummaryItem.</summary>
    private void CollapseCompletedTurnBlocks(AssistantMessageItem assistantItem)
    {
        var target = _rebuildTarget ?? (IList<TranscriptItem>?)_liveTarget;
        if (target is null) return;
        var idx = target.IndexOf(assistantItem);
        if (idx <= 0) return;

        var blocksToMerge = new List<TranscriptItem>();
        for (int i = idx - 1; i >= 0; i--)
        {
            if (target[i] is ToolGroupItem or ReasoningItem or SingleToolItem)
                blocksToMerge.Add(target[i]);
            else
                break;
        }

        if (blocksToMerge.Count < 2) return;
        blocksToMerge.Reverse();

        int totalToolCalls = 0, failedCount = 0;
        bool hasReasoning = false, hasTodoProgress = false;
        string? todoMeta = null;
        string? lastIntentLabel = null;

        foreach (var block in blocksToMerge)
        {
            if (block is ToolGroupItem tg)
            {
                // Use the group's label as intent if it's not a generic "Working/Finished" label
                if (!string.IsNullOrWhiteSpace(tg.Label)
                    && !tg.Label.StartsWith(Loc.ToolGroup_Working.TrimEnd('\u2026', '.'), StringComparison.CurrentCulture)
                    && !tg.Label.StartsWith(Loc.ToolGroup_Finished, StringComparison.CurrentCulture))
                    lastIntentLabel = tg.Label;

                foreach (var call in tg.ToolCalls)
                {
                    if (call is ToolCallItem tc)
                    {
                        totalToolCalls++;
                        if (tc.Status == StrataAiToolCallStatus.Failed) failedCount++;
                        if (!string.IsNullOrWhiteSpace(tc.ToolName) && tc.ToolName.Contains(Loc.ToolTodo_Title, StringComparison.CurrentCultureIgnoreCase))
                        {
                            hasTodoProgress = true;
                            todoMeta = tg.Meta ?? tc.MoreInfo;
                        }
                    }
                    else if (call is TerminalPreviewItem tp)
                    {
                        totalToolCalls++;
                        if (tp.Status == StrataAiToolCallStatus.Failed) failedCount++;
                    }
                }
            }
            else if (block is SingleToolItem st)
            {
                totalToolCalls++;
                if (st.Inner is ToolCallItem stc && stc.Status == StrataAiToolCallStatus.Failed) failedCount++;
                else if (st.Inner is TerminalPreviewItem stp && stp.Status == StrataAiToolCallStatus.Failed) failedCount++;
            }
            else hasReasoning = true;
        }

        string label;
        if (hasTodoProgress)
            label = !string.IsNullOrWhiteSpace(todoMeta) ? $"{Loc.ToolTodo_Title} · {todoMeta}" : Loc.ToolTodo_Title;
        else if (lastIntentLabel is not null)
        {
            // Use the intent-based label from report_intent
            label = hasReasoning ? $"{lastIntentLabel} · {Loc.TurnSummary_Reasoned.ToLowerInvariant()}" : lastIntentLabel;
            if (totalToolCalls > 1)
                label += $" · {string.Format(Loc.ToolGroup_FinishedCount, totalToolCalls)}";
        }
        else if (hasReasoning && totalToolCalls > 0)
            label = totalToolCalls == 1 ? Loc.TurnSummary_ReasonedAndOneAction : string.Format(Loc.TurnSummary_ReasonedAndActions, totalToolCalls);
        else if (totalToolCalls > 0)
            label = totalToolCalls == 1 ? Loc.ToolGroup_FinishedOne : string.Format(Loc.ToolGroup_FinishedCount, totalToolCalls);
        else
            label = Loc.TurnSummary_ReasonedAndOneAction;

        if (failedCount > 0)
            label += " " + string.Format(Loc.ToolGroup_FinishedFailed, failedCount);

        int firstIdx = target.IndexOf(blocksToMerge[0]);
        foreach (var block in blocksToMerge)
            target.Remove(block);

        var summary = new TurnSummaryItem(label)
        {
            IsExpanded = hasTodoProgress && !IsRebuildingTranscript,
            HasFailures = failedCount > 0,
        };
        foreach (var block in blocksToMerge)
            summary.InnerItems.Add(block);

        target.Insert(firstIdx, summary);
    }

    private void CollapseAllCompletedTurns()
    {
        // Collect assistant items, process last-to-first so index shifts don't affect earlier items
        var target = _rebuildTarget ?? (IList<TranscriptItem>?)_liveTarget;
        if (target is null) return;
        var assistantItems = new List<AssistantMessageItem>();
        for (int i = 0; i < target.Count; i++)
        {
            if (target[i] is AssistantMessageItem a)
                assistantItems.Add(a);
        }
        for (int i = assistantItems.Count - 1; i >= 0; i--)
            CollapseCompletedTurnBlocks(assistantItems[i]);
    }

    /// <summary>Shows or updates the typing indicator at the bottom of the transcript.</summary>
    public void ShowTypingIndicator(string? label)
    {
        if (_liveTarget is null) return;

        if (_typingIndicator is null)
        {
            _typingIndicator = new TypingIndicatorItem(label ?? Loc.Status_Thinking);
            _liveTarget.Add(_typingIndicator);
        }
        else
        {
            _typingIndicator.Label = label ?? Loc.Status_Thinking;
            _typingIndicator.IsActive = true;
            // Reposition to end if not already there
            var count = _liveTarget.Count;
            if (count == 0 || _liveTarget[count - 1] != _typingIndicator)
            {
                _liveTarget.Remove(_typingIndicator);
                _liveTarget.Add(_typingIndicator);
            }
        }
    }

    public void HideTypingIndicator()
    {
        if (_typingIndicator is not null && _liveTarget is not null)
        {
            _liveTarget.Remove(_typingIndicator);
            _typingIndicator = null;
        }
    }

    public void UpdateTypingIndicatorLabel(string? label)
    {
        if (_typingIndicator is not null && !string.IsNullOrEmpty(label))
            _typingIndicator.Label = label;
    }

    private void InsertBeforeTypingIndicator(TranscriptItem item)
    {
        if (_rebuildTarget is not null)
        {
            _rebuildTarget.Add(item);
            return;
        }

        if (_liveTarget is null)
            return;

        if (_typingIndicator is not null)
        {
            var idx = _liveTarget.IndexOf(_typingIndicator);
            if (idx >= 0)
            {
                _liveTarget.Insert(idx, item);
                return;
            }
        }

        _liveTarget.Add(item);
    }

    /// <summary>Updates a terminal preview's output. Called from SubscribeToSession event handlers.</summary>
    public void UpdateTerminalOutput(string rootToolCallId, string output, bool replaceExistingOutput)
    {
        if (string.IsNullOrEmpty(output)) return;

        if (!_terminalPreviewsByToolCallId.TryGetValue(rootToolCallId, out var target))
            return;

        if (replaceExistingOutput || string.IsNullOrEmpty(target.Output))
        {
            target.Output = output;
            return;
        }

        if (output.StartsWith(target.Output, StringComparison.Ordinal))
            target.Output = output;
        else if (!target.Output.EndsWith(output, StringComparison.Ordinal))
            target.Output = target.Output + "\n" + output;
    }

    /// <summary>Creates a QuestionItem and inserts it into the transcript. Called from the QuestionAsked event.</summary>
    public void AddQuestionToTranscript(string questionId, string question, string options, bool allowFreeText)
    {
        CloseCurrentToolGroup();
        var card = new QuestionItem(questionId, question, options, allowFreeText, _submitQuestionAnswerAction);
        InsertBeforeTypingIndicator(card);
    }
}

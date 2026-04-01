using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Localization;
using Lumi.Models;
using Lumi.ViewModels;
using StrataTheme.Controls;

namespace Lumi.Views;

public partial class ChatView : UserControl
{
    private StrataChatShell? _chatShell;
    private StrataChatComposer? _composer;
    private Panel? _composerSpacer;
    private Panel? _dropOverlay;
    private ItemsControl? _transcript;
    private ScrollViewer? _transcriptScrollViewer;

    private ChatViewModel? _subscribedVm;
    private Chat? _lastObservedCurrentChat;
    private ObservableCollection<TranscriptTurn>? _subscribedMountedTurns;
    private Border? _worktreeHighlight;
    private Button? _localToggleBtn;
    private Button? _worktreeToggleBtn;
    private bool _isApplyingTranscriptMutation;
    private bool _resizeRestoreQueued;
    private bool _viewportEvaluationQueued;
    private bool _viewportEvaluationRequested;
    private bool _heightCompensationQueued;
    private double _pendingHeightCompensationDelta;
    private ScrollAnchorState? _pendingResizeAnchor;
    private readonly Dictionary<string, double> _observedTurnHeights = new(StringComparer.Ordinal);
    private readonly HashSet<TranscriptTurn> _heightSubscribedTurns = new();

    // ── Ctrl+F search state ──
    private Border? _searchBar;
    private TextBox? _searchInput;
    private TextBlock? _searchMatchCounter;
    private readonly List<SearchHit> _searchHits = [];
    private int _currentHitIndex = -1;
    private SelectableTextBlock? _highlightedStb;
    private System.Threading.CancellationTokenSource? _searchDebounce;

    /// <summary>A match against a TranscriptItem's raw content, with the occurrence index within that item.</summary>
    private sealed record SearchHit(TranscriptTurn Turn, TranscriptItem Item, int OccurrenceInItem, string Query);

    private static readonly string ClipboardImagesDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Lumi", "clipboard-images");

    private sealed record ScrollAnchorState(string StableId, double ViewportY);

    public ChatView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        _chatShell = this.FindControl<StrataChatShell>("ChatShell");
        _composer = this.FindControl<StrataChatComposer>("Composer");
        _composerSpacer = this.FindControl<Panel>("ComposerSpacer");
        _dropOverlay = this.FindControl<Panel>("DropOverlay");
        _transcript = this.FindControl<ItemsControl>("Transcript");

        // Slide-up animation for coding strip
        var codingStrip = this.FindControl<Border>("CodingStrip");
        if (codingStrip is not null)
        {
            codingStrip.PropertyChanged += (_, e) =>
            {
                if (e.Property == IsVisibleProperty && codingStrip.IsVisible)
                    PlaySlideUpAnimation(codingStrip);
            };
        }

        // Keep the shell spacer height in sync with the real composer container
        var composerContainer = this.FindControl<StackPanel>("ComposerContainer");
        if (composerContainer is not null && _composerSpacer is not null)
        {
            composerContainer.SizeChanged += (_, _) =>
                _composerSpacer.Height = composerContainer.Bounds.Height;
        }

        // Worktree toggle sliding highlight
        _worktreeHighlight = this.FindControl<Border>("WorktreeToggleHighlight");
        _localToggleBtn = this.FindControl<Button>("LocalToggleBtn");
        _worktreeToggleBtn = this.FindControl<Button>("WorktreeToggleBtn");

        var togglePanel = this.FindControl<StackPanel>("WorktreeTogglePanel");
        if (togglePanel is not null)
            togglePanel.SizeChanged += (_, _) => UpdateWorktreeToggleHighlight();

        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(StrataFileAttachment.OpenRequestedEvent, OnFileAttachmentOpenRequested);
        AddHandler(StrataChatMessage.CopyTurnRequestedEvent, OnCopyTurnRequested);
        SizeChanged += OnChatViewSizeChanged;

        // ── Search bar controls ──
        _searchBar = this.FindControl<Border>("SearchBar");
        _searchInput = this.FindControl<TextBox>("SearchInput");
        _searchMatchCounter = this.FindControl<TextBlock>("SearchMatchCounter");

        var searchPrevBtn = this.FindControl<Button>("SearchPrevBtn");
        var searchNextBtn = this.FindControl<Button>("SearchNextBtn");
        var searchCloseBtn = this.FindControl<Button>("SearchCloseBtn");

        if (_searchInput is not null)
        {
            _searchInput.TextChanged += (_, _) => OnSearchQueryChanged();
            _searchInput.KeyDown += OnSearchInputKeyDown;
        }

        if (searchPrevBtn is not null) searchPrevBtn.Click += (_, _) => NavigateSearchMatch(-1);
        if (searchNextBtn is not null) searchNextBtn.Click += (_, _) => NavigateSearchMatch(1);
        if (searchCloseBtn is not null) searchCloseBtn.Click += (_, _) => CloseSearch();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        UnsubscribeFromViewModel();
        ResetSearchState();
        _pendingHeightCompensationDelta = 0;
        _heightCompensationQueued = false;
        _viewportEvaluationRequested = false;
        _lastObservedCurrentChat = null;

        if (DataContext is ChatViewModel vm)
        {
            _subscribedVm = vm;
            _lastObservedCurrentChat = vm.CurrentChat;
            vm.ScrollToEndRequested += OnScrollToEndRequested;
            vm.UserMessageSent += OnUserMessageSent;
            vm.TranscriptRebuilt += OnTranscriptRebuilt;
            vm.PropertyChanged += OnViewModelPropertyChanged;
            vm.AttachFilesRequested += OnAttachFilesRequested;
            vm.ClipboardPasteRequested += OnClipboardPasteRequested;
            vm.CopyToClipboardRequested += OnCopyToClipboardRequested;
            vm.FocusComposerRequested += FocusComposer;
            SubscribeToMountedTurns(vm.MountedTranscriptTurns);
            Dispatcher.UIThread.Post(EnsureTranscriptScrollViewer, DispatcherPriority.Loaded);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Dispatcher.UIThread.Post(EnsureTranscriptScrollViewer, DispatcherPriority.Loaded);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        DetachTranscriptScrollViewer();
        UnsubscribeMountedTurns();
        UnsubscribeFromViewModel();
        _subscribedVm?.StopVoiceIfRecording();
        base.OnDetachedFromVisualTree(e);
    }

    public void FocusComposer()
    {
        _composer?.FocusInput();
    }

    private void UnsubscribeFromViewModel()
    {
        if (_subscribedVm is null) return;
        _subscribedVm.ScrollToEndRequested -= OnScrollToEndRequested;
        _subscribedVm.UserMessageSent -= OnUserMessageSent;
        _subscribedVm.TranscriptRebuilt -= OnTranscriptRebuilt;
        _subscribedVm.PropertyChanged -= OnViewModelPropertyChanged;
        _subscribedVm.AttachFilesRequested -= OnAttachFilesRequested;
        _subscribedVm.ClipboardPasteRequested -= OnClipboardPasteRequested;
        _subscribedVm.CopyToClipboardRequested -= OnCopyToClipboardRequested;
        _subscribedVm.FocusComposerRequested -= FocusComposer;
        _subscribedVm = null;
        _lastObservedCurrentChat = null;
    }

    private void SubscribeToMountedTurns(ObservableCollection<TranscriptTurn> mountedTurns)
    {
        UnsubscribeMountedTurns();
        _subscribedMountedTurns = mountedTurns;
        _subscribedMountedTurns.CollectionChanged += OnMountedTurnsChanged;
        foreach (var turn in _subscribedMountedTurns)
            SubscribeToTurnHeight(turn);
    }

    private void UnsubscribeMountedTurns()
    {
        if (_subscribedMountedTurns is null)
            return;

        _subscribedMountedTurns.CollectionChanged -= OnMountedTurnsChanged;
        UnsubscribeAllTurnHeights();
        _subscribedMountedTurns = null;
    }

    private void EnsureTranscriptScrollViewer()
    {
        if (_transcriptScrollViewer is not null || _chatShell is null)
            return;

        _transcriptScrollViewer = _chatShell.TranscriptScrollViewer;
        if (_transcriptScrollViewer is null)
        {
            Dispatcher.UIThread.Post(EnsureTranscriptScrollViewer, DispatcherPriority.Loaded);
            return;
        }

        _chatShell.TranscriptViewportChanged += OnTranscriptViewportChanged;
        _chatShell.JumpToLatestRequested += OnJumpToLatestRequested;
        _transcriptScrollViewer.SizeChanged += OnTranscriptViewportSizeChanged;
    }

    private void DetachTranscriptScrollViewer()
    {
        if (_transcriptScrollViewer is null)
            return;

        if (_chatShell is not null)
        {
            _chatShell.TranscriptViewportChanged -= OnTranscriptViewportChanged;
            _chatShell.JumpToLatestRequested -= OnJumpToLatestRequested;
        }
        _transcriptScrollViewer.SizeChanged -= OnTranscriptViewportSizeChanged;
        _transcriptScrollViewer = null;
    }

    private void OnMountedTurnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            // Reset fires with OldItems=null — use tracked set to unsubscribe.
            UnsubscribeAllTurnHeights();

            if (_subscribedMountedTurns is not null)
            {
                foreach (var turn in _subscribedMountedTurns)
                    SubscribeToTurnHeight(turn);
            }
            return;
        }

        if (e.OldItems is not null)
        {
            foreach (TranscriptTurn turn in e.OldItems)
            {
                turn.PropertyChanged -= OnMountedTurnPropertyChanged;
                _observedTurnHeights.Remove(turn.StableId);
                _heightSubscribedTurns.Remove(turn);
            }
        }

        if (e.NewItems is not null)
        {
            foreach (TranscriptTurn turn in e.NewItems)
                SubscribeToTurnHeight(turn);
        }
    }

    private void UnsubscribeAllTurnHeights()
    {
        foreach (var turn in _heightSubscribedTurns)
            turn.PropertyChanged -= OnMountedTurnPropertyChanged;
        _heightSubscribedTurns.Clear();
        _observedTurnHeights.Clear();
    }

    private void SubscribeToTurnHeight(TranscriptTurn turn)
    {
        _observedTurnHeights[turn.StableId] = turn.MeasuredHeight;
        _heightSubscribedTurns.Add(turn);
        turn.PropertyChanged += OnMountedTurnPropertyChanged;
    }

    private void OnMountedTurnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TranscriptTurn.MeasuredHeight)
            || sender is not TranscriptTurn turn
            || _chatShell is null
            || _transcriptScrollViewer is null
            || _isApplyingTranscriptMutation)
            return;

        _observedTurnHeights.TryGetValue(turn.StableId, out var previousHeight);
        _observedTurnHeights[turn.StableId] = turn.MeasuredHeight;

        var delta = turn.MeasuredHeight - previousHeight;
        if (Math.Abs(delta) < 0.5)
            return;

        // During active streaming ScrollToEnd() manages positioning — skip
        // compensation for all turns to avoid scroll-position fights.
        if (_subscribedVm is { IsBusy: true })
            return;

        if (_chatShell.IsPinnedToBottom)
            return;

        var control = FindRealizedTurnControl(turn.StableId);
        var point = control?.TranslatePoint(default, _transcriptScrollViewer);
        if (control is null || point is null)
            return;

        // Only compensate for turns fully above the viewport.
        if (point.Value.Y + control.Bounds.Height > 0)
            return;

        _pendingHeightCompensationDelta += delta;
        if (_heightCompensationQueued)
            return;

        _heightCompensationQueued = true;
        Dispatcher.UIThread.Post(ApplyPendingHeightCompensation, DispatcherPriority.Loaded);
    }

    private void OnScrollToEndRequested() => _chatShell?.ScrollToEnd();

    private void SyncTranscriptPinnedState()
    {
        if (_subscribedVm is null || _chatShell is null)
            return;

        _subscribedVm.UpdateTranscriptPinnedState(_chatShell.IsPinnedToBottom, _chatShell.CurrentDistanceFromBottom);
    }

    private void ApplyPendingHeightCompensation()
    {
        _heightCompensationQueued = false;

        if (_chatShell is null || _subscribedVm is null)
        {
            _pendingHeightCompensationDelta = 0;
            return;
        }

        if (_isApplyingTranscriptMutation)
        {
            _heightCompensationQueued = true;
            Dispatcher.UIThread.Post(ApplyPendingHeightCompensation, DispatcherPriority.Loaded);
            return;
        }

        var delta = _pendingHeightCompensationDelta;
        _pendingHeightCompensationDelta = 0;
        if (Math.Abs(delta) < 0.5 || _subscribedVm.IsBusy || _chatShell.IsPinnedToBottom)
            return;

        var beforeOffset = _chatShell.VerticalOffset;
        _chatShell.ScrollToVerticalOffset(beforeOffset + delta);
        _subscribedVm.RecordTranscriptScrollCompensation("height-change", beforeOffset, _chatShell.VerticalOffset);
    }

    private void OnJumpToLatestRequested() => JumpToLatest(focusComposer: false);

    private void JumpToLatest(bool focusComposer)
    {
        _subscribedVm?.EnsureLatestTranscriptMounted();
        _chatShell?.JumpToLatest();
        Dispatcher.UIThread.Post(SyncTranscriptPinnedState, DispatcherPriority.Loaded);

        if (focusComposer)
            Dispatcher.UIThread.Post(FocusComposer, DispatcherPriority.Input);
    }

    private void OnUserMessageSent()
    {
        JumpToLatest(focusComposer: true);
    }

    private async void OnTranscriptRebuilt()
    {
        if (_subscribedVm is null || _chatShell is null)
            return;

        var ready = await EnsureTranscriptScrollViewerReadyAsync();
        if (!ready || _subscribedVm is null || _chatShell is null)
            return;

        var chatShell = _chatShell;
        var viewModel = _subscribedVm;

        chatShell.EnterFollowTailMode();
        viewModel.InitializeMountedTranscript(chatShell.ViewportHeight);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        viewModel.EnsureMountedTranscriptCoverage(chatShell.ViewportHeight);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        chatShell.JumpToLatest();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        SyncTranscriptPinnedState();
        FocusComposer();

        // Re-execute search if active (mounted turns changed)
        if (!string.IsNullOrWhiteSpace(_searchInput?.Text))
            ExecuteSearch();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatViewModel.CurrentChat))
        {
            var currentChat = _subscribedVm?.CurrentChat;
            var chatReferenceChanged = !ReferenceEquals(currentChat, _lastObservedCurrentChat);
            _lastObservedCurrentChat = currentChat;

            if (chatReferenceChanged && currentChat is not null)
                _chatShell?.EnterFollowTailMode();
        }

        if (e.PropertyName == nameof(ChatViewModel.IsWorktreeMode))
            UpdateWorktreeToggleHighlight();
    }

    private void OnTranscriptViewportChanged(object? sender, StrataTranscriptViewportChangedEventArgs e)
    {
        if (_subscribedVm is null)
            return;

        _subscribedVm.UpdateTranscriptPinnedState(e.IsPinnedToBottom, e.DistanceFromBottom);

        if (_isApplyingTranscriptMutation)
        {
            _viewportEvaluationRequested = true;
            return;
        }

        if (e.IsPinnedToBottom)
            return;

        QueueTranscriptViewportEvaluation();
    }

    private void QueueTranscriptViewportEvaluation()
    {
        _viewportEvaluationRequested = true;
        if (_viewportEvaluationQueued)
            return;

        _viewportEvaluationQueued = true;
        Dispatcher.UIThread.Post(() => _ = EvaluateTranscriptViewportAsync(), DispatcherPriority.Loaded);
    }

    private async Task EvaluateTranscriptViewportAsync()
    {
        try
        {
            for (var round = 0; round < 8; round++)
            {
                _viewportEvaluationRequested = false;

                if (_isApplyingTranscriptMutation || _subscribedVm is null || _chatShell is null || _transcriptScrollViewer is null)
                    return;

                var anchor = CaptureAnchor();
                var mutation = _subscribedVm.UpdateTranscriptViewport(
                    _chatShell.VerticalOffset,
                    _chatShell.ViewportHeight,
                    _chatShell.ExtentHeight,
                    _chatShell.IsPinnedToBottom,
                    _chatShell.CurrentDistanceFromBottom);

                if (!mutation.HasChanges)
                {
                    if (!_viewportEvaluationRequested)
                        return;

                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
                    continue;
                }

                await CompleteTranscriptMutationAsync(anchor, mutation);

                if (mutation.Kind != TranscriptWindowMutationKind.Prepend && !_viewportEvaluationRequested)
                    return;

                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            }
        }
        finally
        {
            _viewportEvaluationQueued = false;
            if (_viewportEvaluationRequested)
                QueueTranscriptViewportEvaluation();
        }
    }

    private async void OnTranscriptViewportSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_isApplyingTranscriptMutation || _subscribedVm is null || _chatShell is null)
            return;

        var anchor = _chatShell.IsPinnedToBottom ? null : CaptureAnchor();
        var mutation = _subscribedVm.EnsureMountedTranscriptCoverage(_chatShell.ViewportHeight);
        if (mutation.HasChanges)
            await CompleteTranscriptMutationAsync(anchor, mutation);

        if (_chatShell.IsPinnedToBottom)
            Dispatcher.UIThread.Post(() => _chatShell?.ScrollToEnd(), DispatcherPriority.Loaded);
    }

    private void OnChatViewSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_chatShell is null || _isApplyingTranscriptMutation)
            return;

        if (Math.Abs(e.PreviousSize.Width - e.NewSize.Width) < 0.5)
            return;

        if (_chatShell.IsPinnedToBottom)
        {
            Dispatcher.UIThread.Post(() => _chatShell?.ScrollToEnd(), DispatcherPriority.Loaded);
            return;
        }

        _pendingResizeAnchor ??= CaptureAnchor();
        if (_resizeRestoreQueued)
            return;

        _resizeRestoreQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _resizeRestoreQueued = false;
            var anchor = _pendingResizeAnchor;
            _pendingResizeAnchor = null;
            RestoreAnchor(anchor, "resize");
        }, DispatcherPriority.Loaded);
    }

    private async Task<bool> EnsureTranscriptScrollViewerReadyAsync()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            EnsureTranscriptScrollViewer();
            if (_transcriptScrollViewer is not null && _chatShell is not null)
                return true;

            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        }

        return false;
    }

    private async Task CompleteTranscriptMutationAsync(ScrollAnchorState? anchor, TranscriptWindowMutation mutation)
    {
        _isApplyingTranscriptMutation = true;
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            if (mutation.RequiresAnchorRestore)
                RestoreAnchor(anchor, mutation.Kind == TranscriptWindowMutationKind.Prepend ? "prepend" : "cleanup");

            SyncTranscriptPinnedState();
        }
        finally
        {
            _isApplyingTranscriptMutation = false;
            if (_viewportEvaluationRequested && _chatShell is { IsPinnedToBottom: false })
                QueueTranscriptViewportEvaluation();
        }
    }

    private ScrollAnchorState? CaptureAnchor()
    {
        if (_transcriptScrollViewer is null)
            return null;

        foreach (var control in EnumerateRealizedTurnControls())
        {
            var point = control.TranslatePoint(default, _transcriptScrollViewer);
            if (point is null)
                continue;

            if (point.Value.Y + control.Bounds.Height < 0)
                continue;

            if (control.Turn is null)
                continue;

            return new ScrollAnchorState(control.Turn.StableId, point.Value.Y);
        }

        return null;
    }

    private void RestoreAnchor(ScrollAnchorState? anchor, string reason)
    {
        if (anchor is null || _chatShell is null || _transcriptScrollViewer is null)
            return;

        var control = FindRealizedTurnControl(anchor.StableId);
        var point = control?.TranslatePoint(default, _transcriptScrollViewer);
        if (control is null || point is null)
            return;

        var delta = point.Value.Y - anchor.ViewportY;
        if (Math.Abs(delta) < 0.5)
            return;

        var beforeOffset = _chatShell.VerticalOffset;
        _chatShell.ScrollToVerticalOffset(beforeOffset + delta);
        _subscribedVm?.RecordTranscriptScrollCompensation(reason, beforeOffset, _chatShell.VerticalOffset);
    }

    private TranscriptTurnControl? FindRealizedTurnControl(string stableId)
    {
        return EnumerateRealizedTurnControls().FirstOrDefault(control => control.Turn?.StableId == stableId);
    }

    private IEnumerable<TranscriptTurnControl> EnumerateRealizedTurnControls()
    {
        var itemsHost = _transcript?.ItemsPanelRoot;
        return itemsHost is null
            ? Enumerable.Empty<TranscriptTurnControl>()
            : itemsHost.GetVisualDescendants().OfType<TranscriptTurnControl>();
    }

    // ── File picker (requires View-level StorageProvider) ──

    private async void OnAttachFilesRequested()
    {
        if (DataContext is not ChatViewModel vm) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.FilePicker_AttachFiles,
            AllowMultiple = true
        });

        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path))
                vm.AddAttachment(path);
        }

        if (files.Count > 0)
            FocusComposer();
    }

    // ── Clipboard image paste (requires View-level Clipboard) ──

    private async void OnClipboardPasteRequested()
    {
        if (DataContext is not ChatViewModel vm) return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;

        try
        {
            var dataTransfer = await clipboard.TryGetDataAsync();
            if (dataTransfer is null) return;

            using var bitmap = await dataTransfer.TryGetBitmapAsync();
            if (bitmap is null) return;

            Directory.CreateDirectory(ClipboardImagesDir);
            var fileName = $"clipboard-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.png";
            var filePath = Path.Combine(ClipboardImagesDir, fileName);
            bitmap.Save(filePath);

            vm.AddAttachment(filePath);
            FocusComposer();
        }
        catch
        {
            // Ignore transient clipboard failures.
        }
    }

    // ── Copy to clipboard (ViewModel raises event, View handles clipboard API) ──

    private async void OnCopyToClipboardRequested(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        try
        {
            var data = new Avalonia.Input.DataTransfer();
            data.Add(Avalonia.Input.DataTransferItem.CreateText(text));
            await clipboard.SetDataAsync(data);
        }
        catch { /* ignore */ }
    }

    // ── Copy turn (context menu on assistant messages) ───

    private async void OnCopyTurnRequested(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        e.Handled = true;

        // Walk up from the event source to find the parent TranscriptTurnControl
        TranscriptTurnControl? turn = null;
        if (e.Source is Avalonia.Visual visual)
        {
            var current = visual.GetVisualParent();
            while (current is not null)
            {
                if (current is TranscriptTurnControl ttc) { turn = ttc; break; }
                current = (current as Avalonia.Visual)?.GetVisualParent();
            }
        }

        if (turn is null) return;

        var sb = new System.Text.StringBuilder();
        foreach (var item in turn.Items ?? Enumerable.Empty<TranscriptItem>())
        {
            if (item is AssistantMessageItem assistantMsg)
            {
                var text = assistantMsg.Content;
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (sb.Length > 0) sb.Append(Environment.NewLine).Append(Environment.NewLine);
                sb.Append(text);
            }
        }

        if (sb.Length == 0) return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        try
        {
            var data = new Avalonia.Input.DataTransfer();
            data.Add(Avalonia.Input.DataTransferItem.CreateText(sb.ToString()));
            await clipboard.SetDataAsync(data);
        }
        catch { /* ignore */ }
    }

    // ── Drag & drop ──────────────────────────────────────

    private void OnFileAttachmentOpenRequested(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (e.Source is StrataFileAttachment { DataContext: FileAttachmentItem item })
            item.OpenCommand.Execute(null);
    }

    private static bool HasFiles(DragEventArgs e)
        => e.DataTransfer.Formats.Contains(DataFormat.File);

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (HasFiles(e))
        {
            e.DragEffects = DragDropEffects.Copy;
            if (_dropOverlay is not null) _dropOverlay.IsVisible = true;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
        => e.DragEffects = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        if (_dropOverlay is not null) _dropOverlay.IsVisible = false;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (_dropOverlay is not null) _dropOverlay.IsVisible = false;
        if (DataContext is not ChatViewModel vm) return;

        foreach (var item in e.DataTransfer.Items)
        {
            if (item.TryGetRaw(DataFormat.File) is IStorageItem storageItem)
            {
                var path = storageItem.TryGetLocalPath();
                if (!string.IsNullOrWhiteSpace(path))
                    vm.AddAttachment(path);
            }
        }

        FocusComposer();
    }

    private static async void PlaySlideUpAnimation(Control target)
    {
        target.Opacity = 0;
        target.RenderTransform = new Avalonia.Media.TranslateTransform(0, 6);

        var anim = new Avalonia.Animation.Animation
        {
            Duration = TimeSpan.FromMilliseconds(250),
            Easing = new Avalonia.Animation.Easings.CubicEaseOut(),
            FillMode = Avalonia.Animation.FillMode.Forward,
            Children =
            {
                new Avalonia.Animation.KeyFrame { Cue = new Avalonia.Animation.Cue(0), Setters = { new Avalonia.Styling.Setter(OpacityProperty, 0.0), new Avalonia.Styling.Setter(Avalonia.Media.TranslateTransform.YProperty, 6.0) } },
                new Avalonia.Animation.KeyFrame { Cue = new Avalonia.Animation.Cue(1), Setters = { new Avalonia.Styling.Setter(OpacityProperty, 1.0), new Avalonia.Styling.Setter(Avalonia.Media.TranslateTransform.YProperty, 0.0) } },
            }
        };

        try { await anim.RunAsync(target); } catch { }
        target.Opacity = 1;
        target.RenderTransform = null;
    }

    private void UpdateWorktreeToggleHighlight()
    {
        if (_worktreeHighlight is null || _localToggleBtn is null || _worktreeToggleBtn is null)
            return;

        var isWorktree = _subscribedVm?.IsWorktreeMode ?? false;
        var target = isWorktree ? _worktreeToggleBtn : _localToggleBtn;

        if (target.Bounds.Width <= 0) return;

        _worktreeHighlight.Width = target.Bounds.Width;
        _worktreeHighlight.Margin = new Thickness(target.Bounds.Left, 0, 0, 0);
    }

    // ── Ctrl+F in-chat search ────────────────────────────

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;

        var ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;

        if (ctrl && e.Key == Key.F)
        {
            OpenSearch();
            e.Handled = true;
        }
    }

    private void OnSearchInputKeyDown(object? sender, KeyEventArgs e)
    {
        var shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;

        switch (e.Key)
        {
            case Key.Escape:
                CloseSearch();
                e.Handled = true;
                break;
            case Key.Enter:
                FlushPendingSearch();
                NavigateSearchMatch(shift ? -1 : 1);
                e.Handled = true;
                break;
            case Key.F3:
                FlushPendingSearch();
                NavigateSearchMatch(shift ? -1 : 1);
                e.Handled = true;
                break;
        }
    }

    /// <summary>If a debounced search is pending, execute it immediately.</summary>
    private void FlushPendingSearch()
    {
        if (_searchDebounce is not null && !_searchDebounce.IsCancellationRequested)
        {
            _searchDebounce.Cancel();
            _searchDebounce.Dispose();
            _searchDebounce = null;
            ExecuteSearch();
        }
    }

    private void OpenSearch()
    {
        if (_searchBar is null || _searchInput is null) return;

        _searchBar.Classes.Add("open");

        Dispatcher.UIThread.Post(() =>
        {
            _searchInput.Focus();
            _searchInput.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void CloseSearch()
    {
        if (_searchBar is null) return;

        _searchDebounce?.Cancel();
        _searchDebounce?.Dispose();
        _searchDebounce = null;
        _searchBar.Classes.Remove("open");
        ResetSearchState();

        FocusComposer();
    }

    private void OnSearchQueryChanged()
    {
        _searchDebounce?.Cancel();
        _searchDebounce?.Dispose();
        _searchDebounce = new System.Threading.CancellationTokenSource();
        var token = _searchDebounce.Token;

        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await Task.Delay(200, token);
            }
            catch (TaskCanceledException)
            {
                return;
            }

            if (token.IsCancellationRequested) return;
            ExecuteSearch();
        });
    }

    private void ExecuteSearch()
    {
        var query = _searchInput?.Text;
        _searchHits.Clear();
        _currentHitIndex = -1;
        ClearSearchHighlight();

        if (string.IsNullOrWhiteSpace(query) || _subscribedVm is null)
        {
            UpdateSearchCounter();
            return;
        }

        // Search ALL transcript turns (including unmounted/off-screen)
        foreach (var turn in _subscribedVm.TranscriptTurns)
        {
            foreach (var item in turn.Items)
            {
                var content = item switch
                {
                    UserMessageItem u => u.Content,
                    AssistantMessageItem a => a.Content,
                    ErrorMessageItem err => err.Content,
                    ReasoningItem r => r.Content,
                    _ => null
                };
                if (content is null) continue;

                // Count occurrences in the raw content (case-insensitive)
                var pos = 0;
                var occurrence = 0;
                while (pos < content.Length)
                {
                    var idx = content.IndexOf(query, pos, StringComparison.OrdinalIgnoreCase);
                    if (idx < 0) break;
                    _searchHits.Add(new SearchHit(turn, item, occurrence, query));
                    occurrence++;
                    pos = idx + query.Length;
                }
            }
        }

        if (_searchHits.Count > 0)
            _currentHitIndex = 0;

        UpdateSearchCounter();
    }

    private async void NavigateSearchMatch(int direction)
    {
        if (_searchHits.Count == 0) return;

        ClearSearchHighlight();
        _currentHitIndex = (_currentHitIndex + direction + _searchHits.Count) % _searchHits.Count;
        UpdateSearchCounter();

        var hit = _searchHits[_currentHitIndex];
        if (_subscribedVm is null) return;

        // Ensure the turn's page is mounted
        _subscribedVm.MountTranscriptPageContainingTurn(hit.Turn);

        // Wait for layout so the newly mounted controls are in the visual tree
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);

        HighlightHit(hit);
    }

    private void HighlightHit(SearchHit hit)
    {
        var query = hit.Query;
        if (string.IsNullOrEmpty(query) || _transcript is null) return;

        // Find the visual for this item
        Control? itemVisual = null;
        foreach (var d in _transcript.GetVisualDescendants())
        {
            if (d is Avalonia.Controls.Presenters.ContentPresenter cp &&
                ReferenceEquals(cp.Content, hit.Item))
            { itemVisual = cp; break; }
        }
        if (itemVisual is null) return;

        // Walk SelectableTextBlocks inside, find the Nth occurrence
        var occurrencesSeen = 0;
        foreach (var d in itemVisual.GetVisualDescendants())
        {
            if (d is not SelectableTextBlock stb || !stb.IsVisible) continue;

            var text = ExtractStbText(stb, out var posMap);
            if (string.IsNullOrEmpty(text)) continue;

            var searchFrom = 0;
            while (searchFrom < text.Length)
            {
                var idx = text.IndexOf(query, searchFrom, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) break;

                if (occurrencesSeen == hit.OccurrenceInItem)
                {
                    var selStart = posMap is not null ? posMap[idx] : idx;
                    var selEnd = posMap is not null ? posMap[idx + query.Length - 1] + 1 : idx + query.Length;
                    stb.SelectionStart = selStart;
                    stb.SelectionEnd = selEnd;
                    _highlightedStb = stb;
                    stb.BringIntoView();
                    return;
                }

                occurrencesSeen++;
                searchFrom = idx + query.Length;
            }
        }
    }

    private static string? ExtractStbText(SelectableTextBlock stb, out List<int>? posMap)
    {
        posMap = null;
        var text = stb.Text;
        if (!string.IsNullOrEmpty(text)) return text;

        if (stb.Inlines is not { Count: > 0 }) return null;

        var rawSb = new System.Text.StringBuilder();
        foreach (var inline in stb.Inlines)
        {
            if (inline is Run run)
                rawSb.Append(run.Text ?? "");
            else if (inline is Avalonia.Controls.Documents.LineBreak)
                rawSb.Append('\n');
            else
                rawSb.Append('\uFFFC');
        }
        var rawText = rawSb.ToString();

        // Strip \u2005 inline code padding, build position map
        posMap = new List<int>(rawText.Length);
        var strippedSb = new System.Text.StringBuilder(rawText.Length);
        for (var i = 0; i < rawText.Length; i++)
        {
            if (rawText[i] != '\u2005')
            {
                posMap.Add(i);
                strippedSb.Append(rawText[i]);
            }
        }
        return strippedSb.ToString();
    }

    private void UpdateSearchCounter()
    {
        if (_searchMatchCounter is null) return;

        if (_searchHits.Count == 0)
        {
            var hasQuery = !string.IsNullOrWhiteSpace(_searchInput?.Text);
            _searchMatchCounter.Text = hasQuery ? "No results" : "";
        }
        else
        {
            _searchMatchCounter.Text = $"{_currentHitIndex + 1} of {_searchHits.Count}";
        }
    }

    private void ClearSearchHighlight()
    {
        if (_highlightedStb is not null)
        {
            _highlightedStb.SelectionStart = 0;
            _highlightedStb.SelectionEnd = 0;
            _highlightedStb = null;
        }
    }

    private void ResetSearchState()
    {
        ClearSearchHighlight();
        _searchHits.Clear();
        _currentHitIndex = -1;
        if (_searchMatchCounter is not null)
            _searchMatchCounter.Text = "";
    }
}

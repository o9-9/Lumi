using System;
using System.Collections.ObjectModel;
using System.Linq;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class TranscriptPagingTests
{
    private sealed class TestTranscriptItem : TranscriptItem
    {
        public TestTranscriptItem(string stableId)
            : base(stableId)
        {
        }
    }

    [Fact]
    public void PageBuilder_SplitsTurnsDeterministically()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaxPageWeight = 6,
            MaxTurnsPerPage = 3,
            MinInitialPages = 1,
        });
        var source = CreateTurns(7);

        controller.BindTranscript(source, "page-build");

        Assert.Equal(3, controller.Pages.Count);
        Assert.Equal((0, 2), (controller.Pages[0].FirstTurnIndex, controller.Pages[0].LastTurnIndex));
        Assert.Equal((3, 5), (controller.Pages[1].FirstTurnIndex, controller.Pages[1].LastTurnIndex));
        Assert.Equal((6, 6), (controller.Pages[2].FirstTurnIndex, controller.Pages[2].LastTurnIndex));
    }

    [Fact]
    public void InitialReset_MountsNewestPagesOnly()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaxPageWeight = 4,
            MaxTurnsPerPage = 2,
            MinInitialPages = 2,
            MaxMountedPages = 4,
        });
        var source = CreateTurns(8);

        controller.BindTranscript(source, "initial");
        controller.ResetToLatest(200, "initial");

        var mountedIds = controller.MountedTurns.Select(static turn => turn.StableId).ToArray();
        Assert.Equal(new[] { "turn:0004", "turn:0005", "turn:0006", "turn:0007" }, mountedIds);
    }

    [Fact]
    public void NearTopScroll_PrependsOlderPageInOrder()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaxPageWeight = 4,
            MaxTurnsPerPage = 2,
            MinInitialPages = 2,
            MaxMountedPages = 5,
        });
        var source = CreateTurns(8);

        controller.BindTranscript(source, "prepend");
        controller.ResetToLatest(200, "prepend");
        controller.UpdatePinnedState(false, 180, "prepend");

        var mutation = controller.UpdateViewport(
            new TranscriptViewportState(0, 200, 900, false, 180),
            "prepend");

        Assert.Equal(TranscriptWindowMutationKind.Prepend, mutation.Kind);
        Assert.Equal(new[]
        {
            "turn:0002", "turn:0003", "turn:0004", "turn:0005", "turn:0006", "turn:0007"
        }, controller.MountedTurns.Select(static turn => turn.StableId).ToArray());
    }

    [Fact]
    public void PrependingOlderPages_KeepsMountedWindowBounded()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaxPageWeight = 4,
            MaxTurnsPerPage = 2,
            MinInitialPages = 2,
            MaxMountedPages = 3,
            TrimToMountedPages = 2,
            PrependTriggerPixels = 150,
            RetainAboveViewportPixels = 80,
        });
        var source = CreateTurns(10, measuredHeightFactory: _ => 180);

        controller.BindTranscript(source, "cleanup");
        controller.ResetToLatest(200, "cleanup");
        controller.UpdatePinnedState(false, 240, "cleanup");
        controller.UpdateViewport(new TranscriptViewportState(0, 200, 1200, false, 240), "prepend-1");
        controller.UpdateViewport(new TranscriptViewportState(0, 200, 1200, false, 240), "prepend-2");

        var snapshot = controller.CaptureSnapshot();
        var mutation = controller.UpdateViewport(
            new TranscriptViewportState(1400, 200, 2200, false, 240),
            "cleanup");

        Assert.True(snapshot.MountedPageCount <= 3);
        Assert.Equal(TranscriptWindowMutationKind.None, mutation.Kind);
        Assert.True(controller.CaptureSnapshot().MountedPageCount <= 3);
    }

    [Fact]
    public void NearTopScroll_CanPrependMultiplePagesWithoutRearmScroll()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaxPageWeight = 4,
            MaxTurnsPerPage = 2,
            MinInitialPages = 1,
            MaxMountedPages = 6,
            PrependTriggerPixels = 160,
        });
        var source = CreateTurns(12, measuredHeightFactory: _ => 120);

        controller.BindTranscript(source, "multi-prepend");
        controller.ResetToLatest(200, "multi-prepend");
        controller.UpdatePinnedState(false, 260, "multi-prepend");

        var firstVisibleBefore = controller.MountedTurns[0].StableId;
        TranscriptWindowMutation lastMutation = TranscriptWindowMutation.None;
        for (var i = 0; i < 4; i++)
        {
            lastMutation = controller.UpdateViewport(
                new TranscriptViewportState(0, 200, 1400 + (i * 120), false, 260),
                $"multi-prepend-{i}");
            Assert.Equal(TranscriptWindowMutationKind.Prepend, lastMutation.Kind);
        }

        Assert.NotEqual(firstVisibleBefore, controller.MountedTurns[0].StableId);
        Assert.Equal(new[]
        {
            "turn:0000", "turn:0001", "turn:0002", "turn:0003", "turn:0004", "turn:0005",
            "turn:0006", "turn:0007", "turn:0008", "turn:0009", "turn:0010", "turn:0011"
        }, controller.MountedTurns.Select(static turn => turn.StableId).ToArray());
    }

    [Fact]
    public void PinnedStateLogic_TracksTransitions()
    {
        var controller = new TranscriptWindowController();
        controller.UpdatePinnedState(false, 140, "scroll-away");
        Assert.False(controller.CaptureSnapshot().IsPinnedToBottom);

        controller.UpdatePinnedState(true, 0, "bottom");
        var snapshot = controller.CaptureSnapshot();
        Assert.True(snapshot.IsPinnedToBottom);
        Assert.Equal(0, snapshot.DistanceFromBottom);
    }

    [Fact]
    public void StreamingWhilePinned_KeepsPinnedAndAppendsLatestTurn()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaxPageWeight = 4,
            MaxTurnsPerPage = 2,
            MinInitialPages = 1,
        });
        var source = CreateTurns(4);

        controller.BindTranscript(source, "stream-pinned");
        controller.ResetToLatest(180, "stream-pinned");
        controller.UpdatePinnedState(true, 0, "stream-pinned");

        source.Add(CreateTurn(4));

        var snapshot = controller.CaptureSnapshot();
        Assert.True(snapshot.IsPinnedToBottom);
        Assert.Equal("turn:0004", controller.MountedTurns[^1].StableId);
    }

    [Fact]
    public void StreamingWhileNotPinned_DoesNotForcePinningBackOn()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaxPageWeight = 4,
            MaxTurnsPerPage = 2,
            MinInitialPages = 1,
        });
        var source = CreateTurns(4);

        controller.BindTranscript(source, "stream-reader");
        controller.ResetToLatest(180, "stream-reader");
        controller.UpdatePinnedState(false, 240, "stream-reader");

        source.Add(CreateTurn(4));

        var snapshot = controller.CaptureSnapshot();
        Assert.False(snapshot.IsPinnedToBottom);
        Assert.Equal("turn:0004", controller.MountedTurns[^1].StableId);
    }

    [Fact]
    public void ScrollCompensation_IsCapturedInDiagnostics()
    {
        var controller = new TranscriptWindowController();

        controller.RecordScrollCompensation("prepend", 120, 360);

        var snapshot = controller.CaptureSnapshot();
        Assert.Equal(120, snapshot.LastCompensationBeforeOffset);
        Assert.Equal(360, snapshot.LastCompensationAfterOffset);
    }

    [Fact]
    public void EnsureViewportCoverage_UsesMeasuredPageHeights()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaxPageWeight = 4,
            MaxTurnsPerPage = 2,
            MinInitialPages = 1,
            MaxMountedPages = 4,
            EstimatedPixelsPerWeightUnit = 20,
        });
        var source = CreateTurns(6, measuredHeightFactory: index => index >= 4 ? 120 : 260);

        controller.BindTranscript(source, "coverage");
        controller.ResetToLatest(180, "coverage");

        var mutation = controller.EnsureViewportCoverage(420, "coverage");

        Assert.Equal(TranscriptWindowMutationKind.EnsureCoverage, mutation.Kind);
        Assert.True(controller.CaptureSnapshot().MountedPageCount >= 2);
    }

    [Fact]
    public void EnsureViewportCoverage_AccountsForPageBoundarySpacing()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaxPageWeight = 4,
            MaxTurnsPerPage = 2,
            MinInitialPages = 2,
            MaxMountedPages = 3,
        });
        var source = CreateTurns(6, measuredHeightFactory: _ => 100);

        controller.BindTranscript(source, "spacing");
        controller.ResetToLatest(200, "spacing");

        var mutation = controller.EnsureViewportCoverage(430, "spacing");

        Assert.Equal(TranscriptWindowMutationKind.EnsureCoverage, mutation.Kind);
        Assert.Equal(3, controller.CaptureSnapshot().MountedPageCount);
    }

    [Fact]
    public void StreamingWhileReaderWindowShiftedOffTail_KeepsMountedWindowBounded()
    {
        var controller = new TranscriptWindowController(new TranscriptPagingOptions
        {
            MaxPageWeight = 4,
            MaxTurnsPerPage = 2,
            MinInitialPages = 1,
            MaxMountedPages = 3,
            PrependTriggerPixels = 160,
        });
        var source = CreateTurns(16, measuredHeightFactory: _ => 120);

        controller.BindTranscript(source, "reader-window");
        controller.ResetToLatest(200, "reader-window");
        controller.UpdatePinnedState(false, 260, "reader-window");

        for (var i = 0; i < 6; i++)
        {
            var mutation = controller.UpdateViewport(
                new TranscriptViewportState(0, 200, 1800 + (i * 120), false, 260),
                $"reader-window-{i}");
            Assert.Equal(TranscriptWindowMutationKind.Prepend, mutation.Kind);
        }

        Assert.Equal("turn:0000", controller.MountedTurns[0].StableId);

        source.Add(CreateTurn(16, measuredHeight: 120));

        var mountedIds = controller.MountedTurns.Select(static turn => turn.StableId).ToArray();
        Assert.True(controller.CaptureSnapshot().MountedPageCount <= 3);
        Assert.Equal("turn:0000", mountedIds[0]);
        Assert.DoesNotContain("turn:0016", mountedIds);
    }

    private static ObservableCollection<TranscriptTurn> CreateTurns(
        int count,
        int itemCount = 1,
        Func<int, double>? measuredHeightFactory = null)
    {
        return new ObservableCollection<TranscriptTurn>(
            Enumerable.Range(0, count)
                .Select(index => CreateTurn(index, itemCount, measuredHeightFactory?.Invoke(index) ?? 0))
                .ToArray());
    }

    private static TranscriptTurn CreateTurn(int index, int itemCount = 1, double measuredHeight = 0)
    {
        var turn = new TranscriptTurn($"turn:{index:D4}");
        for (var itemIndex = 0; itemIndex < itemCount; itemIndex++)
            turn.Items.Add(new TestTranscriptItem($"item:{index:D4}:{itemIndex:D2}"));

        if (measuredHeight > 0)
            turn.MeasuredHeight = measuredHeight;

        return turn;
    }
}

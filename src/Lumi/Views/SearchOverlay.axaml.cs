using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.ViewModels;

namespace Lumi.Views;

public partial class SearchOverlay : UserControl
{
    private Border? _scrim;
    private Border? _searchCard;
    private Border? _stratumLine;
    private TextBox? _searchInput;
    private ItemsControl? _resultsList;
    private TextBlock? _emptyState;
    private ScrollViewer? _resultsScroller;
    private int _lastRenderedSelection = -1;
    private long _lastAnimateOpenTick;

    public SearchOverlay()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _scrim = this.FindControl<Border>("Scrim");
        _searchCard = this.FindControl<Border>("SearchCard");
        _stratumLine = this.FindControl<Border>("StratumLine");
        _searchInput = this.FindControl<TextBox>("SearchInput");
        _resultsList = this.FindControl<ItemsControl>("ResultsList");
        _emptyState = this.FindControl<TextBlock>("EmptyState");
        _resultsScroller = this.FindControl<ScrollViewer>("ResultsScroller");

        if (_scrim is not null)
            _scrim.PointerPressed += OnScrimPressed;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsVisibleProperty && change.GetNewValue<bool>())
        {
            // Debounce: ignore repeated IsVisible=true within the animation window
            var now = Environment.TickCount64;
            if (now - _lastAnimateOpenTick < 400) return;
            _lastAnimateOpenTick = now;

            // Reset stratum line to collapsed state before animating
            if (_stratumLine is not null)
            {
                _stratumLine.Opacity = 0;
                _stratumLine.RenderTransform = TransformOperations.Parse("scaleX(0)");
            }

            Dispatcher.UIThread.Post(() =>
            {
                _searchInput?.Focus();
                _searchInput?.SelectAll();
                AnimateOpen();
            }, DispatcherPriority.Render);
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is SearchOverlayViewModel vm)
        {
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(SearchOverlayViewModel.SelectedIndex))
                    UpdateSelectionVisuals();
                else if (args.PropertyName == nameof(SearchOverlayViewModel.IsOpen))
                {
                    if (!vm.IsOpen)
                        _lastRenderedSelection = -1;
                }
            };

            vm.ResultGroups.CollectionChanged += (_, _) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    UpdateEmptyState();
                    UpdateSelectionVisuals();
                }, DispatcherPriority.Render);
            };
        }
    }

    private void UpdateEmptyState()
    {
        if (_emptyState is null || DataContext is not SearchOverlayViewModel vm) return;
        _emptyState.IsVisible = !string.IsNullOrEmpty(vm.SearchQuery) && vm.FlatResults.Count == 0;
    }

    private void UpdateSelectionVisuals()
    {
        if (DataContext is not SearchOverlayViewModel vm || _resultsList is null) return;

        var flatIndex = vm.SelectedIndex;
        _lastRenderedSelection = flatIndex;

        int currentFlatIndex = 0;
        foreach (var groupContainer in _resultsList.GetVisualDescendants().OfType<ItemsControl>())
        {
            if (groupContainer == _resultsList) continue;

            foreach (var button in groupContainer.GetVisualDescendants().OfType<Button>())
            {
                if (!button.Classes.Contains("search-result-item")) continue;

                var isSelected = currentFlatIndex == flatIndex;

                if (isSelected)
                {
                    if (!button.Classes.Contains("selected"))
                        button.Classes.Add("selected");
                    button.BringIntoView();
                }
                else
                {
                    button.Classes.Remove("selected");
                }

                // Toggle the selection pipe inside the button
                var pipe = button.GetVisualDescendants()
                    .OfType<Border>()
                    .FirstOrDefault(b => b.Name == "SelectionPipe");
                if (pipe is not null)
                    pipe.Opacity = isSelected ? 1 : 0;

                currentFlatIndex++;
            }
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (DataContext is not SearchOverlayViewModel vm) return;

        switch (e.Key)
        {
            case Key.Escape:
                vm.Close();
                e.Handled = true;
                break;

            case Key.Down:
                vm.MoveSelection(1);
                e.Handled = true;
                break;

            case Key.Up:
                vm.MoveSelection(-1);
                e.Handled = true;
                break;

            case Key.Enter:
                vm.SelectCurrent();
                e.Handled = true;
                break;

            case Key.Tab:
                // Tab also moves selection down
                vm.MoveSelection(1);
                e.Handled = true;
                break;
        }
    }

    private void OnScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is SearchOverlayViewModel vm)
            vm.Close();
        e.Handled = true;
    }

    private void AnimateOpen()
    {
        // ── Scrim fade-in ──
        if (_scrim is not null)
        {
            var scrimVisual = ElementComposition.GetElementVisual(_scrim);
            if (scrimVisual?.Compositor is { } scrimComp)
            {
                var scrimFade = scrimComp.CreateScalarKeyFrameAnimation();
                scrimFade.InsertKeyFrame(0f, 0f);
                scrimFade.InsertKeyFrame(1f, 1f);
                scrimFade.Duration = TimeSpan.FromMilliseconds(180);
                scrimVisual.StartAnimation("Opacity", scrimFade);
            }
        }

        // ── Card entrance (OverlayAnimationHelper-style spring) ──
        if (_searchCard is null) return;

        var visual = ElementComposition.GetElementVisual(_searchCard);
        if (visual?.Compositor is not { } compositor) return;

        var w = (float)_searchCard.Bounds.Width;
        var h = (float)_searchCard.Bounds.Height;
        if (w <= 0) w = 560;
        if (h <= 0) h = 400;
        visual.CenterPoint = new System.Numerics.Vector3(w / 2f, h / 2f, 0f);

        // Scale: 0.92 → 1.005 (overshoot at 60%) → 1.0
        var scaleAnim = compositor.CreateVector3KeyFrameAnimation();
        scaleAnim.InsertKeyFrame(0f, new System.Numerics.Vector3(0.92f, 0.92f, 1f));
        scaleAnim.InsertKeyFrame(0.6f, new System.Numerics.Vector3(1.005f, 1.005f, 1f));
        scaleAnim.InsertKeyFrame(1f, new System.Numerics.Vector3(1f, 1f, 1f));
        scaleAnim.Duration = TimeSpan.FromMilliseconds(220);
        visual.StartAnimation("Scale", scaleAnim);

        // Opacity: 0 → 1 (fast, completes before scale)
        var opacityAnim = compositor.CreateScalarKeyFrameAnimation();
        opacityAnim.InsertKeyFrame(0f, 0f);
        opacityAnim.InsertKeyFrame(0.45f, 1f);
        opacityAnim.InsertKeyFrame(1f, 1f);
        opacityAnim.Duration = TimeSpan.FromMilliseconds(220);
        visual.StartAnimation("Opacity", opacityAnim);

        // ── Stratum line sweep-in ──
        if (_stratumLine is not null)
        {
            Dispatcher.UIThread.Post(async () =>
            {
                await Task.Delay(100);
                if (_stratumLine is null) return;
                _stratumLine.Opacity = 1;
                _stratumLine.RenderTransform = TransformOperations.Parse("scaleX(1)");
            });
        }
    }

    /// <summary>Called from result item button clicks.</summary>
    public void OnResultItemClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is SearchResultItem item
            && DataContext is SearchOverlayViewModel vm)
        {
            vm.Close();
            vm.RaiseResultSelected(item);
        }
    }
}

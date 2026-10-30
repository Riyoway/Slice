using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Slice.Models;
using Windows.UI;

namespace Slice.Controls;

public sealed partial class TimelineControl : UserControl
{
    private const double TrackPadding = 4;
    private const double ClipGap = 1;
    private const double HandleHitWidth = 7;
    private const double DragThreshold = 6;

    private readonly List<ClipLayout> _layouts = [];
    private EditSession? _session;
    private double _playhead;
    private Rectangle? _playheadMarker;
    private Polygon? _rulerPlayheadHead;
    private DragMode _dragMode;
    private uint _pointerId;
    private double _pressX;
    private double _pressTimeline;
    private double _dragPointerX;
    private double _grabOffsetInClip;
    private double _dragPixelsPerSecond = 1;
    private double _trimOriginalStart;
    private double _trimOriginalEnd;
    private bool _dragMoved;
    private bool _editStartedForGesture;
    private bool _gestureChanged;

    public TimelineControl()
    {
        InitializeComponent();

        Surface.PointerPressed += Surface_PointerPressed;
        Surface.PointerMoved += Surface_PointerMoved;
        Surface.PointerReleased += Surface_PointerReleased;
        Surface.PointerCanceled += Surface_PointerCanceled;
        Surface.PointerCaptureLost += Surface_PointerCaptureLost;
        Surface.SizeChanged += (_, _) => Redraw();
        Ruler.SizeChanged += (_, _) => Redraw();
        ActualThemeChanged += (_, _) => Redraw();
    }

    public string MediaName { get; set; } = "Clip";

    public event EventHandler<double>? SeekRequested;
    public event EventHandler? EditStarted;
    public event EventHandler? EditCompleted;
    public event EventHandler? SelectionChanged;

    public EditSession? Session
    {
        get => _session;
        set
        {
            _session = value;
            Redraw();
        }
    }

    public double Playhead
    {
        get => _playhead;
        set
        {
            _playhead = value;
            PositionPlayhead();
        }
    }

    public void Refresh() => Redraw();

    private void Surface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_session is null || _session.Clips.Count == 0 || Surface.ActualWidth <= 0)
            return;

        var point = e.GetCurrentPoint(Surface);
        if (!point.Properties.IsLeftButtonPressed)
            return;

        var x = Math.Clamp(point.Position.X, 0, Surface.ActualWidth);
        var layout = HitLayout(x);
        if (layout is null)
            return;

        if (_session.SelectedIndex != layout.Index)
        {
            _session.SelectClip(layout.Index);
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            Redraw();
            layout = _layouts.First(item => item.Index == _session.SelectedIndex);
        }

        var clip = _session.SelectedClip;
        if (clip is null)
            return;

        _pointerId = point.PointerId;
        _pressX = x;
        _pressTimeline = LayoutToTimeline(layout, x);
        _dragPointerX = x;
        _grabOffsetInClip = x - layout.X;
        _dragMoved = false;
        _editStartedForGesture = false;
        _gestureChanged = false;
        _trimOriginalStart = clip.SourceStart;
        _trimOriginalEnd = clip.SourceEnd;
        _dragPixelsPerSecond = Math.Max(0.001, layout.Width / Math.Max(clip.Length, 0.001));

        if (Math.Abs(x - layout.X) <= HandleHitWidth)
            _dragMode = DragMode.TrimStart;
        else if (Math.Abs(x - (layout.X + layout.Width)) <= HandleHitWidth)
            _dragMode = DragMode.TrimEnd;
        else
            _dragMode = DragMode.MoveOrSeek;

        Surface.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Surface_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_session is null || _dragMode == DragMode.None)
            return;

        var point = e.GetCurrentPoint(Surface);
        if (point.PointerId != _pointerId)
            return;

        var x = Math.Clamp(point.Position.X, 0, Surface.ActualWidth);
        _dragPointerX = x;
        var deltaX = x - _pressX;

        if (_dragMode is DragMode.TrimStart or DragMode.TrimEnd)
        {
            if (Math.Abs(deltaX) < 0.5)
                return;

            BeginEditOnce();
            var deltaSeconds = deltaX / _dragPixelsPerSecond;
            var changed = _dragMode == DragMode.TrimStart
                ? _session.TrimClipStart(_session.SelectedIndex, _trimOriginalStart + deltaSeconds)
                : _session.TrimClipEnd(_session.SelectedIndex, _trimOriginalEnd + deltaSeconds);

            if (changed)
            {
                _dragMoved = true;
                _gestureChanged = true;
                Redraw();
            }

            return;
        }

        if (_dragMode == DragMode.MoveOrSeek)
        {
            // With a single clip, body gestures are seeks. There is nothing to reorder,
            // so small pointer drift must never turn a click into a fake drag.
            if (_session.Clips.Count <= 1)
                return;

            if (!_dragMoved && Math.Abs(deltaX) < DragThreshold)
                return;

            BeginEditOnce();
            _dragMoved = true;
            var targetIndex = GetReorderTarget(x);
            var currentIndex = _session.SelectedIndex;

            if (targetIndex != currentIndex && _session.MoveClip(currentIndex, targetIndex))
                _gestureChanged = true;

            Redraw();
        }
    }

    private void Surface_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var releasedPoint = e.GetCurrentPoint(Surface);
        if (releasedPoint.PointerId != _pointerId)
            return;

        if (_dragMode == DragMode.MoveOrSeek && !_dragMoved)
            SeekRequested?.Invoke(this, _pressTimeline);

        var changed = _gestureChanged;
        EndPointer(e.Pointer);
        if (changed)
        {
            EditCompleted?.Invoke(this, EventArgs.Empty);
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
        e.Handled = true;
    }

    private void Surface_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(Surface).PointerId != _pointerId)
            return;

        var changed = _gestureChanged;
        EndPointer(e.Pointer);
        if (changed)
            EditCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void Surface_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_dragMode == DragMode.None)
            return;

        var changed = _gestureChanged;
        ResetPointerState();
        if (changed)
            EditCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void BeginEditOnce()
    {
        if (_editStartedForGesture)
            return;

        _editStartedForGesture = true;
        EditStarted?.Invoke(this, EventArgs.Empty);
    }

    private void EndPointer(Pointer pointer)
    {
        // Reset first: ReleasePointerCapture synchronously raises PointerCaptureLost.
        // This keeps completion from firing twice for the same gesture.
        ResetPointerState();
        Surface.ReleasePointerCapture(pointer);
    }

    private void ResetPointerState()
    {
        _dragMode = DragMode.None;
        _pointerId = 0;
        _dragMoved = false;
        _editStartedForGesture = false;
        _gestureChanged = false;
    }

    private void Redraw()
    {
        Surface.Children.Clear();
        Ruler.Children.Clear();
        _layouts.Clear();
        _playheadMarker = null;
        _rulerPlayheadHead = null;

        if (_session is null || _session.Clips.Count == 0 || _session.TimelineDuration <= 0 || Surface.ActualWidth <= 1)
            return;

        BuildLayouts();
        DrawRuler();

        var trackHeight = Math.Max(1, Surface.ActualHeight / 2d);
        var videoColor = IsLightTheme
            ? Color.FromArgb(255, 70, 139, 177)
            : Color.FromArgb(255, 40, 101, 137);
        var audioColor = IsLightTheme
            ? Color.FromArgb(255, 72, 145, 105)
            : Color.FromArgb(255, 37, 112, 78);

        foreach (var layout in _layouts)
        {
            var selected = layout.Index == _session.SelectedIndex;
            var clip = _session.Clips[layout.Index];
            var label = $"{MediaName}  [{layout.Index + 1}]";

            var visualX = GetVisualX(layout, selected);
            DrawClipRow(layout with { X = visualX }, clip, selected, 2, Math.Max(1, trackHeight - 4), videoColor, label);
            DrawClipRow(layout with { X = visualX }, clip, selected, trackHeight + 2, Math.Max(1, trackHeight - 4), audioColor, label);

            if (selected)
            {
                AddTrimHandle(visualX, Surface.ActualHeight);
                AddTrimHandle(visualX + layout.Width, Surface.ActualHeight);
            }
        }

        var accent = AccentColor;
        _playheadMarker = new Rectangle
        {
            Width = 1,
            Height = Surface.ActualHeight,
            Fill = new SolidColorBrush(accent),
            IsHitTestVisible = false
        };
        Canvas.SetTop(_playheadMarker, 0);
        Canvas.SetZIndex(_playheadMarker, 30);
        Surface.Children.Add(_playheadMarker);

        _rulerPlayheadHead = new Polygon
        {
            Points = new PointCollection
            {
                new Windows.Foundation.Point(0, 0),
                new Windows.Foundation.Point(8, 0),
                new Windows.Foundation.Point(4, 5)
            },
            Fill = new SolidColorBrush(accent),
            IsHitTestVisible = false
        };
        Canvas.SetTop(_rulerPlayheadHead, 19);
        Canvas.SetZIndex(_rulerPlayheadHead, 30);
        Ruler.Children.Add(_rulerPlayheadHead);
        PositionPlayhead();
    }

    private void DrawClipRow(
        ClipLayout layout,
        ClipSegment clip,
        bool selected,
        double top,
        double height,
        Color fill,
        string label)
    {
        var card = new Border
        {
            Width = layout.Width,
            Height = height,
            CornerRadius = new CornerRadius(1),
            Background = new SolidColorBrush(fill),
            BorderBrush = new SolidColorBrush(selected ? SelectionColor : BorderColor),
            BorderThickness = new Thickness(selected ? 2 : 1),
            IsHitTestVisible = false,
            Child = CreateClipLabel(label, clip, layout.Width)
        };

        Canvas.SetLeft(card, layout.X);
        Canvas.SetTop(card, top);
        Canvas.SetZIndex(card, selected ? 5 : 1);
        Surface.Children.Add(card);
    }

    private FrameworkElement CreateClipLabel(string label, ClipSegment clip, double width)
    {
        var grid = new Grid
        {
            Margin = new Thickness(5, 1, 4, 0),
            IsHitTestVisible = false
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (width >= 110)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(new TextBlock
        {
            Text = width < 32 ? string.Empty : label,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 9,
            Foreground = new SolidColorBrush(Colors.White),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Top
        });

        if (width >= 110)
        {
            var duration = new TextBlock
            {
                Text = FormatTime(clip.Length),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 8,
                Foreground = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
            Grid.SetColumn(duration, 1);
            grid.Children.Add(duration);
        }

        return grid;
    }

    private void AddTrimHandle(double x, double height)
    {
        var handle = new Rectangle
        {
            Width = 3,
            Height = Math.Max(1, height),
            Fill = new SolidColorBrush(SelectionColor),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(handle, x - 1.5);
        Canvas.SetTop(handle, 0);
        Canvas.SetZIndex(handle, 20);
        Surface.Children.Add(handle);
    }

    private double GetVisualX(ClipLayout layout, bool selected)
    {
        if (!selected || _dragMode != DragMode.MoveOrSeek || !_dragMoved)
            return layout.X;

        return Math.Clamp(_dragPointerX - _grabOffsetInClip, TrackPadding, Math.Max(TrackPadding, Surface.ActualWidth - TrackPadding - layout.Width));
    }

    private bool IsLightTheme => ActualTheme == ElementTheme.Light;
    private Color AccentColor => IsLightTheme ? Color.FromArgb(255, 2, 101, 220) : Color.FromArgb(255, 94, 170, 247);
    private Color SelectionColor => IsLightTheme ? Color.FromArgb(255, 2, 101, 220) : Color.FromArgb(255, 114, 183, 249);
    private Color BorderColor => IsLightTheme ? Color.FromArgb(255, 190, 190, 190) : Color.FromArgb(255, 35, 35, 35);
    private Color RulerTextColor => IsLightTheme ? Color.FromArgb(255, 85, 85, 85) : Color.FromArgb(255, 167, 167, 167);
    private Color RulerTickColor => IsLightTheme ? Color.FromArgb(255, 170, 170, 170) : Color.FromArgb(255, 74, 74, 74);

    private void BuildLayouts()
    {
        if (_session is null)
            return;

        var count = _session.Clips.Count;
        var total = _session.TimelineDuration;
        var gapWidth = ClipGap * Math.Max(0, count - 1);
        var usableWidth = Math.Max(1, Surface.ActualWidth - TrackPadding * 2 - gapWidth);
        var pixelsPerSecond = usableWidth / total;
        var x = TrackPadding;
        var timelineStart = 0d;

        for (var i = 0; i < count; i++)
        {
            var clip = _session.Clips[i];
            var width = Math.Max(1, clip.Length * pixelsPerSecond);
            _layouts.Add(new ClipLayout(i, x, width, timelineStart, timelineStart + clip.Length));
            x += width + ClipGap;
            timelineStart += clip.Length;
        }
    }

    private void DrawRuler()
    {
        if (_session is null || _layouts.Count == 0)
            return;

        var divisions = Math.Clamp((int)(Ruler.ActualWidth / 130), 4, 10);
        for (var i = 0; i <= divisions; i++)
        {
            var time = _session.TimelineDuration * i / divisions;
            var x = TimelineToX(time);

            var tick = new Rectangle
            {
                Width = 1,
                Height = 5,
                Fill = new SolidColorBrush(RulerTickColor),
                IsHitTestVisible = false
            };
            Canvas.SetLeft(tick, x);
            Canvas.SetTop(tick, 20);
            Ruler.Children.Add(tick);

            var label = new TextBlock
            {
                Text = FormatTime(time),
                FontSize = 8,
                FontFamily = new FontFamily("Segoe UI"),
                Foreground = new SolidColorBrush(RulerTextColor),
                IsHitTestVisible = false
            };
            Canvas.SetLeft(label, Math.Max(2, x + 3));
            Canvas.SetTop(label, 3);
            Ruler.Children.Add(label);
        }
    }

    private ClipLayout? HitLayout(double x)
    {
        if (_session is not null && _session.SelectedIndex >= 0)
        {
            var selected = _layouts.FirstOrDefault(layout => layout.Index == _session.SelectedIndex);
            if (selected is not null &&
                x >= selected.X - HandleHitWidth &&
                x <= selected.X + selected.Width + HandleHitWidth)
            {
                return selected;
            }
        }

        return _layouts.FirstOrDefault(layout => x >= layout.X && x <= layout.X + layout.Width);
    }

    private int GetReorderTarget(double x)
    {
        if (_layouts.Count == 0)
            return 0;

        for (var i = 0; i < _layouts.Count; i++)
        {
            if (x < _layouts[i].X + _layouts[i].Width / 2)
                return i;
        }

        return _layouts.Count - 1;
    }

    private double LayoutToTimeline(ClipLayout layout, double x)
    {
        var ratio = layout.Width <= 0 ? 0 : Math.Clamp((x - layout.X) / layout.Width, 0, 1);
        return layout.TimelineStart + ratio * (layout.TimelineEnd - layout.TimelineStart);
    }

    private double TimelineToX(double timelinePosition)
    {
        if (_session is null || _layouts.Count == 0)
            return 0;

        var clamped = Math.Clamp(timelinePosition, 0, _session.TimelineDuration);
        for (var i = 0; i < _layouts.Count; i++)
        {
            var layout = _layouts[i];
            if (clamped < layout.TimelineEnd - 0.0001 || i == _layouts.Count - 1)
            {
                var length = layout.TimelineEnd - layout.TimelineStart;
                var ratio = length <= 0 ? 0 : Math.Clamp((clamped - layout.TimelineStart) / length, 0, 1);
                return layout.X + ratio * layout.Width;
            }
        }

        return _layouts[^1].X + _layouts[^1].Width;
    }

    private void PositionPlayhead()
    {
        if (_playheadMarker is null || _session is null || _layouts.Count == 0)
            return;

        var x = TimelineToX(Math.Clamp(_playhead, 0, _session.TimelineDuration));
        Canvas.SetLeft(_playheadMarker, x - 0.5);
        if (_rulerPlayheadHead is not null)
            Canvas.SetLeft(_rulerPlayheadHead, x - 4);
    }

    private static string FormatTime(double seconds)
    {
        seconds = Math.Max(0, seconds);
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes}:{time.Seconds:00}";
    }

    private sealed record ClipLayout(
        int Index,
        double X,
        double Width,
        double TimelineStart,
        double TimelineEnd);

    private enum DragMode
    {
        None,
        MoveOrSeek,
        TrimStart,
        TrimEnd
    }
}

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
    private const double TrackPadding = 8;
    private const double ClipGap = 4;
    private const double HandleHitWidth = 10;
    private const double DragThreshold = 6;

    private readonly List<ClipLayout> _layouts = [];
    private EditSession? _session;
    private double _playhead;
    private Rectangle? _playheadMarker;
    private DragMode _dragMode;
    private uint _pointerId;
    private double _pressX;
    private double _pressTimeline;
    private double _dragPixelsPerSecond = 1;
    private double _trimOriginalStart;
    private double _trimOriginalEnd;
    private bool _dragMoved;
    private bool _editStartedForGesture;

    public TimelineControl()
    {
        InitializeComponent();

        Surface.PointerPressed += Surface_PointerPressed;
        Surface.PointerMoved += Surface_PointerMoved;
        Surface.PointerReleased += Surface_PointerReleased;
        Surface.PointerCanceled += Surface_PointerCanceled;
        Surface.SizeChanged += (_, _) => Redraw();
        Ruler.SizeChanged += (_, _) => Redraw();
    }

    public event EventHandler<double>? SeekRequested;
    public event EventHandler? EditStarted;
    public event EventHandler? EditChanged;
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
        _dragMoved = false;
        _editStartedForGesture = false;
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
                EditChanged?.Invoke(this, EventArgs.Empty);
                Redraw();
            }

            return;
        }

        if (_dragMode == DragMode.MoveOrSeek && point.Properties.IsLeftButtonPressed)
        {
            if (!_dragMoved && Math.Abs(deltaX) < DragThreshold)
                return;

            BeginEditOnce();
            _dragMoved = true;
            var targetIndex = GetReorderTarget(x);
            var currentIndex = _session.SelectedIndex;

            if (targetIndex != currentIndex && _session.MoveClip(currentIndex, targetIndex))
            {
                EditChanged?.Invoke(this, EventArgs.Empty);
                SelectionChanged?.Invoke(this, EventArgs.Empty);
                Redraw();
            }
        }
    }

    private void Surface_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(Surface).PointerId != _pointerId)
            return;

        if (_dragMode == DragMode.MoveOrSeek && !_dragMoved)
            SeekRequested?.Invoke(this, _pressTimeline);

        EndPointer(e.Pointer);
        e.Handled = true;
    }

    private void Surface_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(Surface).PointerId == _pointerId)
            EndPointer(e.Pointer);
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
        Surface.ReleasePointerCapture(pointer);
        _dragMode = DragMode.None;
        _pointerId = 0;
        _dragMoved = false;
        _editStartedForGesture = false;
    }

    private void Redraw()
    {
        Surface.Children.Clear();
        Ruler.Children.Clear();
        _layouts.Clear();
        _playheadMarker = null;

        if (_session is null || _session.Clips.Count == 0 || _session.TimelineDuration <= 0 || Surface.ActualWidth <= 1)
            return;

        BuildLayouts();
        DrawRuler();

        foreach (var layout in _layouts)
        {
            var selected = layout.Index == _session.SelectedIndex;
            var clip = _session.Clips[layout.Index];

            var card = new Border
            {
                Width = layout.Width,
                Height = Math.Max(1, Surface.ActualHeight - 12),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(selected
                    ? Color.FromArgb(255, 38, 72, 103)
                    : Color.FromArgb(255, 31, 34, 40)),
                BorderBrush = new SolidColorBrush(selected
                    ? Color.FromArgb(255, 125, 196, 255)
                    : Color.FromArgb(255, 63, 68, 78)),
                BorderThickness = new Thickness(selected ? 2 : 1),
                IsHitTestVisible = false,
                Child = CreateClipLabel(layout.Index, clip, layout.Width)
            };

            Canvas.SetLeft(card, layout.X);
            Canvas.SetTop(card, 6);
            Surface.Children.Add(card);

            if (selected)
            {
                AddTrimHandle(layout.X, Surface.ActualHeight);
                AddTrimHandle(layout.X + layout.Width, Surface.ActualHeight);
            }
        }

        _playheadMarker = new Rectangle
        {
            Width = 2,
            Height = Surface.ActualHeight,
            RadiusX = 1,
            RadiusY = 1,
            Fill = new SolidColorBrush(Colors.White),
            IsHitTestVisible = false
        };
        Canvas.SetTop(_playheadMarker, 0);
        Canvas.SetZIndex(_playheadMarker, 20);
        Surface.Children.Add(_playheadMarker);
        PositionPlayhead();
    }

    private FrameworkElement CreateClipLabel(int index, ClipSegment clip, double width)
    {
        var panel = new StackPanel
        {
            Margin = new Thickness(12, 8, 8, 6),
            Spacing = 2,
            IsHitTestVisible = false
        };

        panel.Children.Add(new TextBlock
        {
            Text = $"Clip {index + 1}",
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 238, 241, 245)),
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        if (width >= 72)
        {
            panel.Children.Add(new TextBlock
            {
                Text = FormatTime(clip.Length),
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 161, 168, 179))
            });
        }

        return panel;
    }

    private void AddTrimHandle(double x, double height)
    {
        var handle = new Border
        {
            Width = 7,
            Height = Math.Max(18, height - 28),
            CornerRadius = new CornerRadius(3.5),
            Background = new SolidColorBrush(Color.FromArgb(255, 187, 222, 255)),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(handle, x - 3.5);
        Canvas.SetTop(handle, (height - handle.Height) / 2);
        Canvas.SetZIndex(handle, 10);
        Surface.Children.Add(handle);
    }

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

        const int divisions = 5;
        for (var i = 0; i <= divisions; i++)
        {
            var time = _session.TimelineDuration * i / divisions;
            var x = TimelineToX(time);

            var tick = new Rectangle
            {
                Width = 1,
                Height = 6,
                Fill = new SolidColorBrush(Color.FromArgb(255, 76, 82, 92)),
                IsHitTestVisible = false
            };
            Canvas.SetLeft(tick, x);
            Canvas.SetTop(tick, 18);
            Ruler.Children.Add(tick);

            var label = new TextBlock
            {
                Text = FormatTime(time),
                FontSize = 9,
                FontFamily = new FontFamily("Cascadia Mono"),
                Foreground = new SolidColorBrush(Color.FromArgb(255, 119, 125, 136)),
                IsHitTestVisible = false
            };
            Canvas.SetLeft(label, Math.Max(0, x - 18));
            Canvas.SetTop(label, 1);
            Ruler.Children.Add(label);
        }
    }

    private ClipLayout? HitLayout(double x) =>
        _layouts.FirstOrDefault(layout => x >= layout.X && x <= layout.X + layout.Width);

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
        Canvas.SetLeft(_playheadMarker, x - 1);
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

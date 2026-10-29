using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Slice.Models;
using Windows.Foundation;
using Windows.UI;

namespace Slice.Controls;

public sealed partial class TimelineControl : UserControl
{
    private const double HandleHitWidth = 14;
    private EditSession? _session;
    private double _playhead;
    private DragMode _dragMode;
    private uint _pointerId;

    public TimelineControl()
    {
        InitializeComponent();
    }

    public event EventHandler<double>? SeekRequested;
    public event EventHandler? EditStarted;
    public event EventHandler? TrimChanged;

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
            Redraw();
        }
    }

    public void Refresh() => Redraw();

    private void Surface_SizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    private void Surface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_session is null || _session.Duration <= 0 || Surface.ActualWidth <= 0)
            return;

        var point = e.GetCurrentPoint(Surface);
        var x = Math.Clamp(point.Position.X, 0, Surface.ActualWidth);
        var leftX = ToX(_session.TrimStart);
        var rightX = ToX(_session.TrimEnd);

        _pointerId = point.PointerId;
        if (Math.Abs(x - leftX) <= HandleHitWidth)
        {
            _dragMode = DragMode.TrimStart;
            EditStarted?.Invoke(this, EventArgs.Empty);
        }
        else if (Math.Abs(x - rightX) <= HandleHitWidth)
        {
            _dragMode = DragMode.TrimEnd;
            EditStarted?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _dragMode = DragMode.Seek;
            SeekRequested?.Invoke(this, ToTime(x));
        }

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

        var time = ToTime(Math.Clamp(point.Position.X, 0, Surface.ActualWidth));
        switch (_dragMode)
        {
            case DragMode.TrimStart:
                _session.TrimStart = Math.Clamp(time, 0, Math.Max(0, _session.TrimEnd - 0.05));
                TrimChanged?.Invoke(this, EventArgs.Empty);
                Redraw();
                break;

            case DragMode.TrimEnd:
                _session.TrimEnd = Math.Clamp(time, Math.Min(_session.Duration, _session.TrimStart + 0.05), _session.Duration);
                TrimChanged?.Invoke(this, EventArgs.Empty);
                Redraw();
                break;

            case DragMode.Seek:
                if (point.Properties.IsLeftButtonPressed)
                    SeekRequested?.Invoke(this, time);
                break;
        }
    }

    private void Surface_PointerReleased(object sender, PointerRoutedEventArgs e) => EndPointer(e.Pointer);
    private void Surface_PointerCanceled(object sender, PointerRoutedEventArgs e) => EndPointer(e.Pointer);

    private void EndPointer(Pointer pointer)
    {
        Surface.ReleasePointerCapture(pointer);
        _dragMode = DragMode.None;
        _pointerId = 0;
    }

    private void Redraw()
    {
        Surface.Children.Clear();
        if (_session is null || _session.Duration <= 0 || Surface.ActualWidth <= 1 || Surface.ActualHeight <= 1)
            return;

        var height = Surface.ActualHeight;
        var left = ToX(_session.TrimStart);
        var right = ToX(_session.TrimEnd);

        AddRect(0, 0, left, height, Color.FromArgb(185, 8, 9, 11), 0);
        AddRect(right, 0, Math.Max(0, Surface.ActualWidth - right), height, Color.FromArgb(185, 8, 9, 11), 0);

        foreach (var cut in _session.Cuts)
        {
            var x1 = ToX(Math.Max(cut.Start, _session.TrimStart));
            var x2 = ToX(Math.Min(cut.End, _session.TrimEnd));
            if (x2 > x1)
                AddRect(x1, 5, x2 - x1, height - 10, Color.FromArgb(115, 217, 79, 92), 5);
        }

        if (_session.MarkIn is double markIn)
            AddMarker(ToX(markIn), height, Color.FromArgb(255, 142, 204, 255), 2);
        if (_session.MarkOut is double markOut)
            AddMarker(ToX(markOut), height, Color.FromArgb(255, 142, 204, 255), 2);

        if (_session.MarkIn is double a && _session.MarkOut is double b)
        {
            var x1 = ToX(Math.Min(a, b));
            var x2 = ToX(Math.Max(a, b));
            AddRect(x1, 3, Math.Max(0, x2 - x1), height - 6, Color.FromArgb(35, 142, 204, 255), 4);
        }

        AddHandle(left, height, true);
        AddHandle(right, height, false);
        AddMarker(ToX(Math.Clamp(_playhead, 0, _session.Duration)), height, Colors.White, 2);
    }

    private void AddHandle(double x, double height, bool left)
    {
        var line = new Rectangle
        {
            Width = 4,
            Height = height,
            RadiusX = 2,
            RadiusY = 2,
            Fill = new SolidColorBrush(Color.FromArgb(255, 142, 204, 255))
        };
        Canvas.SetLeft(line, x - 2);
        Canvas.SetTop(line, 0);
        Surface.Children.Add(line);

        var grip = new Border
        {
            Width = 12,
            Height = 22,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(255, 142, 204, 255))
        };
        Canvas.SetLeft(grip, x - 6);
        Canvas.SetTop(grip, (height - 22) / 2);
        Surface.Children.Add(grip);
    }

    private void AddMarker(double x, double height, Color color, double width)
    {
        var marker = new Rectangle
        {
            Width = width,
            Height = height,
            Fill = new SolidColorBrush(color)
        };
        Canvas.SetLeft(marker, x - width / 2);
        Canvas.SetTop(marker, 0);
        Surface.Children.Add(marker);
    }

    private void AddRect(double x, double y, double width, double height, Color color, double radius)
    {
        if (width <= 0 || height <= 0)
            return;

        var rect = new Rectangle
        {
            Width = width,
            Height = height,
            RadiusX = radius,
            RadiusY = radius,
            Fill = new SolidColorBrush(color)
        };
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        Surface.Children.Add(rect);
    }

    private double ToX(double seconds) => _session is null || _session.Duration <= 0
        ? 0
        : Math.Clamp(seconds / _session.Duration, 0, 1) * Surface.ActualWidth;

    private double ToTime(double x) => _session is null || Surface.ActualWidth <= 0
        ? 0
        : Math.Clamp(x / Surface.ActualWidth, 0, 1) * _session.Duration;

    private enum DragMode
    {
        None,
        Seek,
        TrimStart,
        TrimEnd
    }
}

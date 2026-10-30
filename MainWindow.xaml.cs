using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Slice.Dialogs;
using Slice.Models;
using Slice.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace Slice;

public sealed partial class MainWindow : Window
{
    private readonly EditSession _session = new();
    private readonly Stack<EditSessionSnapshot> _undo = new();
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly ExportService _exportService = new();
    private StorageFile? _sourceFile;
    private MediaInfo? _mediaInfo;
    private bool _isExporting;

    public MainWindow()
    {
        InitializeComponent();

        // Wire UI events in code. This avoids WinUI runtime XAML event-binding failures
        // in unpackaged/self-contained builds while keeping the XAML purely declarative.
        RootGrid.DragOver += RootGrid_DragOver;
        RootGrid.Drop += RootGrid_Drop;
        RootGrid.KeyDown += RootGrid_KeyDown;
        OpenButton.Click += OpenButton_Click;
        EmptyOpenButton.Click += OpenButton_Click;
        ExportButton.Click += ExportButton_Click;
        FrameBackButton.Click += FrameBackButton_Click;
        PlayButton.Click += PlayButton_Click;
        FrameForwardButton.Click += FrameForwardButton_Click;
        VolumeSlider.ValueChanged += VolumeSlider_ValueChanged;
        MarkInButton.Click += MarkInButton_Click;
        MarkOutButton.Click += MarkOutButton_Click;
        CutButton.Click += CutButton_Click;
        UndoButton.Click += UndoButton_Click;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1200, 760));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

        PlayerElement.SetMediaPlayer(new MediaPlayer
        {
            AutoPlay = false,
            IsLoopingEnabled = false,
            Volume = 1
        });
        PlayerElement.MediaPlayer.CommandManager.IsEnabled = false;
        PlayerElement.MediaPlayer.MediaOpened += MediaPlayer_MediaOpened;
        PlayerElement.MediaPlayer.PlaybackSession.PlaybackStateChanged += PlaybackSession_PlaybackStateChanged;

        Timeline.Session = _session;
        Timeline.SeekRequested += (_, seconds) => Seek(seconds);
        Timeline.EditStarted += (_, _) => BeginTimelineEdit();
        Timeline.TrimChanged += (_, _) => OnTimelineTrimChanged();

        _uiTimer.Tick += UiTimer_Tick;
        _uiTimer.Start();
        Closed += (_, _) => PlayerElement.MediaPlayer.Dispose();
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e) => await OpenFileAsync();

    private async Task OpenFileAsync()
    {
        if (_isExporting)
            return;

        var picker = new FileOpenPicker();
        InitializePicker(picker);
        picker.SuggestedStartLocation = PickerLocationId.VideosLibrary;
        picker.FileTypeFilter.Add(".mp4");
        picker.FileTypeFilter.Add(".mkv");
        picker.FileTypeFilter.Add(".mov");
        picker.FileTypeFilter.Add(".webm");
        picker.FileTypeFilter.Add(".avi");
        picker.FileTypeFilter.Add(".m4v");

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
            await LoadMediaAsync(file);
    }

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        if (_isExporting || !e.DataView.Contains(StandardDataFormats.StorageItems))
            return;

        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Open in Slice";
        e.DragUIOverride.IsContentVisible = true;
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        if (_isExporting || !e.DataView.Contains(StandardDataFormats.StorageItems))
            return;

        var items = await e.DataView.GetStorageItemsAsync();
        var file = items.OfType<StorageFile>().FirstOrDefault();
        if (file is not null)
            await LoadMediaAsync(file);
    }

    private async Task LoadMediaAsync(StorageFile file)
    {
        try
        {
            StatusText.Text = "Opening…";
            _sourceFile = file;
            _mediaInfo = await MediaProbeService.ProbeAsync(file.Path);
            _undo.Clear();
            UndoButton.IsEnabled = false;

            if (_mediaInfo is not null && _mediaInfo.Duration > 0)
                _session.Reset(_mediaInfo.Duration);
            else
                _session.Reset(0);

            Timeline.Refresh();
            PlayerElement.MediaPlayer.Source = MediaSource.CreateFromStorageFile(file);
            FileNameText.Text = file.Name;
            EmptyState.Visibility = Visibility.Collapsed;
            Timeline.IsEnabled = true;
            ExportButton.IsEnabled = true;
            StatusText.Text = _mediaInfo is null ? "Preview ready · FFprobe not found" : "Ready";
            RefreshUi();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Couldn’t open this file", ex.Message);
        }
    }

    private void MediaPlayer_MediaOpened(MediaPlayer sender, object args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var natural = sender.PlaybackSession.NaturalDuration.TotalSeconds;
            if (_session.Duration <= 0 && natural > 0)
            {
                _session.Reset(natural);
                _mediaInfo ??= new MediaInfo(natural, true, true, 0, 0, 0);
                Timeline.Refresh();
                RefreshUi();
            }
        });
    }

    private void PlaybackSession_PlaybackStateChanged(MediaPlaybackSession sender, object args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var playing = sender.PlaybackState == MediaPlaybackState.Playing;
            PlayButtonIcon.Source = new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource(
                new Uri(playing ? "ms-appx:///Assets/Icons/pause.svg" : "ms-appx:///Assets/Icons/play.svg"));
        });
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e) => TogglePlayback();

    private void TogglePlayback()
    {
        if (_sourceFile is null)
            return;

        var player = PlayerElement.MediaPlayer;
        if (player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
        {
            player.Pause();
            return;
        }

        var position = player.PlaybackSession.Position.TotalSeconds;
        if (position < _session.TrimStart || position >= _session.TrimEnd - 0.01)
            Seek(_session.TrimStart);
        else
            SeekPastCutsIfNeeded(position);

        player.Play();
    }

    private void UiTimer_Tick(object? sender, object e)
    {
        if (_sourceFile is null)
            return;

        var player = PlayerElement.MediaPlayer;
        var position = player.PlaybackSession.Position.TotalSeconds;

        if (player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
        {
            if (position >= _session.TrimEnd)
            {
                player.Pause();
                Seek(_session.TrimStart);
                position = _session.TrimStart;
            }
            else
            {
                var jumped = SeekPastCutsIfNeeded(position);
                if (jumped is double newPosition)
                    position = newPosition;
            }
        }

        Timeline.Playhead = position;
        TimeText.Text = $"{FormatTime(position)}  /  {FormatTime(_session.Duration)}";
    }

    private double? SeekPastCutsIfNeeded(double position)
    {
        foreach (var cut in _session.Cuts)
        {
            if (position >= cut.Start - 0.01 && position < cut.End - 0.01)
            {
                Seek(Math.Min(cut.End, _session.TrimEnd));
                return cut.End;
            }
        }

        return null;
    }

    private void Seek(double seconds)
    {
        if (_sourceFile is null)
            return;

        var clamped = Math.Clamp(seconds, 0, Math.Max(_session.Duration, 0));
        PlayerElement.MediaPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(clamped);
        Timeline.Playhead = clamped;
        TimeText.Text = $"{FormatTime(clamped)}  /  {FormatTime(_session.Duration)}";
    }

    private void FrameBackButton_Click(object sender, RoutedEventArgs e) => StepFrame(-1);
    private void FrameForwardButton_Click(object sender, RoutedEventArgs e) => StepFrame(1);

    private void StepFrame(int direction)
    {
        if (_sourceFile is null)
            return;

        PlayerElement.MediaPlayer.Pause();
        var fps = _mediaInfo?.FrameRate > 0 ? _mediaInfo.FrameRate : 30;
        var position = PlayerElement.MediaPlayer.PlaybackSession.Position.TotalSeconds;
        Seek(position + direction / fps);
    }

    private void MarkInButton_Click(object sender, RoutedEventArgs e) => MarkIn();
    private void MarkOutButton_Click(object sender, RoutedEventArgs e) => MarkOut();

    private void MarkIn()
    {
        if (_sourceFile is null)
            return;
        _session.MarkIn = Math.Clamp(PlayerElement.MediaPlayer.PlaybackSession.Position.TotalSeconds, _session.TrimStart, _session.TrimEnd);
        Timeline.Refresh();
        RefreshCutState();
    }

    private void MarkOut()
    {
        if (_sourceFile is null)
            return;
        _session.MarkOut = Math.Clamp(PlayerElement.MediaPlayer.PlaybackSession.Position.TotalSeconds, _session.TrimStart, _session.TrimEnd);
        Timeline.Refresh();
        RefreshCutState();
    }

    private void CutButton_Click(object sender, RoutedEventArgs e) => CutMarkedRange();

    private void CutMarkedRange()
    {
        if (_session.MarkIn is not double markIn || _session.MarkOut is not double markOut)
            return;

        PushUndo();
        _session.AddCut(markIn, markOut);
        Timeline.Refresh();
        RefreshCutState();
        StatusText.Text = $"{_session.Cuts.Count} cut{(_session.Cuts.Count == 1 ? string.Empty : "s")}";
    }

    private void UndoButton_Click(object sender, RoutedEventArgs e) => Undo();

    private void Undo()
    {
        if (_undo.Count == 0)
            return;

        _session.Restore(_undo.Pop());
        UndoButton.IsEnabled = _undo.Count > 0;
        Timeline.Refresh();
        RefreshUi();
        StatusText.Text = "Undo";
    }

    private void BeginTimelineEdit() => PushUndo();

    private void OnTimelineTrimChanged()
    {
        if (PlayerElement.MediaPlayer.PlaybackSession.Position.TotalSeconds < _session.TrimStart)
            Seek(_session.TrimStart);
        else if (PlayerElement.MediaPlayer.PlaybackSession.Position.TotalSeconds > _session.TrimEnd)
            Seek(_session.TrimEnd);

        _session.MarkIn = _session.MarkIn is double i ? Math.Clamp(i, _session.TrimStart, _session.TrimEnd) : null;
        _session.MarkOut = _session.MarkOut is double o ? Math.Clamp(o, _session.TrimStart, _session.TrimEnd) : null;
        RefreshUi();
    }

    private void PushUndo()
    {
        _undo.Push(_session.Snapshot());
        if (_undo.Count > 50)
        {
            var newest = _undo.Take(50).Reverse().ToArray();
            _undo.Clear();
            foreach (var item in newest)
                _undo.Push(item);
        }
        UndoButton.IsEnabled = true;
    }

    private void VolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        var value = Math.Clamp(e.NewValue / 100d, 0, 1);
        if (PlayerElement.MediaPlayer is not null)
            PlayerElement.MediaPlayer.Volume = value;
        VolumeText.Text = $"{Math.Round(e.NewValue):0}%";
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sourceFile is null || _isExporting)
            return;

        if (_mediaInfo is null)
        {
            _mediaInfo = await MediaProbeService.ProbeAsync(_sourceFile.Path);
            if (_mediaInfo is null)
            {
                await ShowErrorAsync("FFmpeg tools are missing", "Put ffmpeg.exe and ffprobe.exe in the Tools folder, or add them to PATH.");
                return;
            }
        }

        var dialog = new ExportDialog { XamlRoot = RootGrid.XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var options = dialog.GetOptions();
        var picker = new FileSavePicker();
        InitializePicker(picker);
        picker.SuggestedStartLocation = PickerLocationId.VideosLibrary;
        picker.SuggestedFileName = Path.GetFileNameWithoutExtension(_sourceFile.Name) + "-trimmed";
        AddFileTypeChoice(picker, options.Format);

        var target = await picker.PickSaveFileAsync();
        if (target is null)
            return;

        _isExporting = true;
        ExportButton.IsEnabled = false;
        ExportProgress.Visibility = Visibility.Visible;
        ExportProgress.Value = 0;
        StatusText.Text = "Exporting…";

        try
        {
            var progress = new Progress<double>(value => ExportProgress.Value = value);
            await _exportService.ExportAsync(
                _sourceFile.Path,
                target.Path,
                _session,
                _mediaInfo,
                VolumeSlider.Value / 100d,
                options,
                progress);
            StatusText.Text = "Export complete";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Export failed";
            await ShowErrorAsync("Export failed", ex.Message);
        }
        finally
        {
            _isExporting = false;
            ExportButton.IsEnabled = _sourceFile is not null;
            ExportProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void RefreshUi()
    {
        TrimStartText.Text = $"IN  {FormatTime(_session.TrimStart)}";
        TrimEndText.Text = $"OUT  {FormatTime(_session.TrimEnd)}";
        RefreshCutState();
        Timeline.Refresh();
    }

    private void RefreshCutState()
    {
        CutButton.IsEnabled = _session.MarkIn is not null &&
                              _session.MarkOut is not null &&
                              Math.Abs(_session.MarkIn.Value - _session.MarkOut.Value) >= 0.01;
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (ctrl && e.Key == VirtualKey.O)
        {
            _ = OpenFileAsync();
            e.Handled = true;
        }
        else if (ctrl && e.Key == VirtualKey.Z)
        {
            Undo();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Space)
        {
            TogglePlayback();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.I)
        {
            MarkIn();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.O)
        {
            MarkOut();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Delete)
        {
            CutMarkedRange();
            e.Handled = true;
        }
    }

    private void InitializePicker(object picker)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
    }

    private static void AddFileTypeChoice(FileSavePicker picker, ExportFormat format)
    {
        switch (format)
        {
            case ExportFormat.WebM:
                picker.FileTypeChoices.Add("WebM video", [".webm"]);
                break;
            case ExportFormat.Mkv:
                picker.FileTypeChoices.Add("Matroska video", [".mkv"]);
                break;
            case ExportFormat.Gif:
                picker.FileTypeChoices.Add("Animated GIF", [".gif"]);
                break;
            case ExportFormat.Mp3:
                picker.FileTypeChoices.Add("MP3 audio", [".mp3"]);
                break;
            case ExportFormat.Wav:
                picker.FileTypeChoices.Add("WAV audio", [".wav"]);
                break;
            default:
                picker.FileTypeChoices.Add("MP4 video", [".mp4"]);
                break;
        }
    }

    private async Task ShowErrorAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = title,
            Content = new ScrollViewer
            {
                MaxHeight = 260,
                Content = new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true
                }
            },
            CloseButtonText = "Close"
        };
        await dialog.ShowAsync();
    }

    private static string FormatTime(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
            seconds = 0;
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}"
            : $"{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}";
    }
}

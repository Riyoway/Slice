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
    private bool _isLightTheme;
    private double _timelinePosition;
    private int _activeClipIndex = -1;

    public MainWindow()
    {
        InitializeComponent();

        RootGrid.DragOver += RootGrid_DragOver;
        RootGrid.Drop += RootGrid_Drop;
        RootGrid.KeyDown += RootGrid_KeyDown;
        OpenButton.Click += OpenButton_Click;
        EmptyOpenButton.Click += OpenButton_Click;
        ImportModeButton.Click += OpenButton_Click;
        EditModeButton.Click += EditModeButton_Click;
        ExportModeButton.Click += ExportButton_Click;
        ThemeToggleButton.Click += ThemeToggleButton_Click;
        ExportButton.Click += ExportButton_Click;
        FrameBackButton.Click += FrameBackButton_Click;
        PlayButton.Click += PlayButton_Click;
        FrameForwardButton.Click += FrameForwardButton_Click;
        VolumeSlider.ValueChanged += VolumeSlider_ValueChanged;
        SplitButton.Click += SplitButton_Click;
        DeleteButton.Click += DeleteButton_Click;
        UndoButton.Click += UndoButton_Click;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 900));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        ApplyTheme(light: false);

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
        Timeline.SeekRequested += (_, seconds) => SeekTimeline(seconds);
        Timeline.EditStarted += (_, _) =>
        {
            PlayerElement.MediaPlayer.Pause();
            PushUndo();
        };
        Timeline.EditCompleted += (_, _) => OnTimelineEditCompleted();
        Timeline.SelectionChanged += (_, _) => RefreshSelectionUi();

        _uiTimer.Tick += UiTimer_Tick;
        _uiTimer.Start();
        Closed += (_, _) => PlayerElement.MediaPlayer.Dispose();
    }

    private void ThemeToggleButton_Click(object sender, RoutedEventArgs e) => ApplyTheme(!_isLightTheme);

    private void ApplyTheme(bool light)
    {
        _isLightTheme = light;
        RootGrid.RequestedTheme = light ? ElementTheme.Light : ElementTheme.Dark;
        ThemeToggleIcon.Source = new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource(
            new Uri(light ? "ms-appx:///Assets/Icons/moon.svg" : "ms-appx:///Assets/Icons/sun.svg"));

        var titleBar = AppWindow.TitleBar;
        titleBar.ButtonForegroundColor = light
            ? Windows.UI.Color.FromArgb(255, 34, 34, 34)
            : Windows.UI.Color.FromArgb(255, 232, 232, 232);
        titleBar.ButtonHoverForegroundColor = titleBar.ButtonForegroundColor;
        titleBar.ButtonPressedForegroundColor = titleBar.ButtonForegroundColor;
        titleBar.ButtonBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        titleBar.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        titleBar.ButtonHoverBackgroundColor = light
            ? Windows.UI.Color.FromArgb(255, 222, 222, 222)
            : Windows.UI.Color.FromArgb(255, 52, 52, 52);
        titleBar.ButtonPressedBackgroundColor = light
            ? Windows.UI.Color.FromArgb(255, 207, 207, 207)
            : Windows.UI.Color.FromArgb(255, 66, 66, 66);

        Timeline.Refresh();
    }

    private void EditModeButton_Click(object sender, RoutedEventArgs e)
    {
        Timeline.Focus(FocusState.Programmatic);
        StatusText.Text = "Edit";
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
            _timelinePosition = 0;
            _activeClipIndex = -1;

            _session.Reset(_mediaInfo is { Duration: > 0 } ? _mediaInfo.Duration : 0);
            Timeline.MediaName = file.Name;
            Timeline.Refresh();
            Timeline.Playhead = 0;

            PlayerElement.MediaPlayer.Source = MediaSource.CreateFromStorageFile(file);
            FileNameText.Text = file.Name;
            ProjectClipNameText.Text = file.Name;
            ProjectDurationText.Text = FormatTime(_session.Duration);
            ProjectClipRow.Visibility = Visibility.Visible;
            ProjectEmptyState.Visibility = Visibility.Collapsed;
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
            }

            _timelinePosition = 0;
            _activeClipIndex = _session.Clips.Count > 0 ? 0 : -1;
            Timeline.Playhead = 0;
            ProjectDurationText.Text = FormatTime(_session.Duration);
            Timeline.Refresh();
            RefreshUi();
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
        if (_sourceFile is null || _session.Clips.Count == 0)
            return;

        var player = PlayerElement.MediaPlayer;
        if (player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
        {
            player.Pause();
            return;
        }

        if (_timelinePosition >= _session.TimelineDuration - 0.001)
            SeekTimeline(0);
        else
            SeekTimeline(_timelinePosition);

        player.Play();
    }

    private void UiTimer_Tick(object? sender, object e)
    {
        if (_sourceFile is null || _session.Clips.Count == 0)
            return;

        var player = PlayerElement.MediaPlayer;
        if (player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
        {
            if (_activeClipIndex < 0 || _activeClipIndex >= _session.Clips.Count)
            {
                if (_session.TryGetClipAtTimeline(_timelinePosition, out var mappedIndex, out _, out _, out _))
                    _activeClipIndex = mappedIndex;
            }

            if (_activeClipIndex >= 0 && _activeClipIndex < _session.Clips.Count)
            {
                var clip = _session.Clips[_activeClipIndex];
                var sourcePosition = player.PlaybackSession.Position.TotalSeconds;

                if (sourcePosition >= clip.SourceEnd - 0.012)
                {
                    if (_activeClipIndex + 1 < _session.Clips.Count)
                    {
                        _activeClipIndex++;
                        var next = _session.Clips[_activeClipIndex];
                        _timelinePosition = _session.GetClipTimelineStart(_activeClipIndex);
                        player.PlaybackSession.Position = TimeSpan.FromSeconds(next.SourceStart);
                    }
                    else
                    {
                        _timelinePosition = _session.TimelineDuration;
                        player.Pause();
                    }
                }
                else
                {
                    var local = Math.Clamp(sourcePosition - clip.SourceStart, 0, clip.Length);
                    _timelinePosition = _session.GetClipTimelineStart(_activeClipIndex) + local;
                }
            }
        }

        Timeline.Playhead = _timelinePosition;
        UpdateTimeText();
        UpdateActionState();
    }

    private void SeekTimeline(double timelineSeconds)
    {
        if (_sourceFile is null || _session.Clips.Count == 0)
            return;

        var total = _session.TimelineDuration;
        var clamped = Math.Clamp(timelineSeconds, 0, total);
        double sourceTime;

        if (clamped >= total - 0.0001)
        {
            _activeClipIndex = _session.Clips.Count - 1;
            sourceTime = _session.Clips[^1].SourceEnd;
        }
        else if (_session.TryGetClipAtTimeline(clamped, out var index, out var clip, out var localTime, out _))
        {
            _activeClipIndex = index;
            sourceTime = clip.SourceStart + localTime;
        }
        else
        {
            return;
        }

        _timelinePosition = clamped;
        PlayerElement.MediaPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(sourceTime);
        Timeline.Playhead = clamped;
        UpdateTimeText();
        UpdateActionState();
    }

    private void FrameBackButton_Click(object sender, RoutedEventArgs e) => StepFrame(-1);
    private void FrameForwardButton_Click(object sender, RoutedEventArgs e) => StepFrame(1);

    private void StepFrame(int direction)
    {
        if (_sourceFile is null || _session.Clips.Count == 0)
            return;

        PlayerElement.MediaPlayer.Pause();
        var fps = _mediaInfo?.FrameRate > 0 ? _mediaInfo.FrameRate : 30;
        SeekTimeline(_timelinePosition + direction / fps);
    }

    private void SplitButton_Click(object sender, RoutedEventArgs e) => SplitAtPlayhead();

    private void SplitAtPlayhead()
    {
        if (_sourceFile is null || !_session.CanSplitAtTimeline(_timelinePosition))
            return;

        PushUndo();
        if (!_session.SplitAtTimeline(_timelinePosition))
            return;

        Timeline.Refresh();
        SeekTimeline(_timelinePosition);
        StatusText.Text = "Clip split";
        RefreshUi();
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e) => DeleteSelectedClip();

    private void DeleteSelectedClip()
    {
        if (_sourceFile is null || _session.SelectedClip is null)
            return;

        var selectedStart = _session.GetClipTimelineStart(_session.SelectedIndex);
        PushUndo();
        if (!_session.DeleteSelected())
            return;

        PlayerElement.MediaPlayer.Pause();
        Timeline.Refresh();

        if (_session.Clips.Count > 0)
            SeekTimeline(Math.Min(selectedStart, _session.TimelineDuration));
        else
        {
            _activeClipIndex = -1;
            _timelinePosition = 0;
            Timeline.Playhead = 0;
            UpdateTimeText();
        }

        StatusText.Text = "Clip deleted";
        RefreshUi();
    }

    private void UndoButton_Click(object sender, RoutedEventArgs e) => Undo();

    private void Undo()
    {
        if (_undo.Count == 0)
            return;

        PlayerElement.MediaPlayer.Pause();
        _session.Restore(_undo.Pop());
        UndoButton.IsEnabled = _undo.Count > 0;
        Timeline.Refresh();

        if (_session.Clips.Count > 0)
            SeekTimeline(Math.Min(_timelinePosition, _session.TimelineDuration));
        else
        {
            _activeClipIndex = -1;
            _timelinePosition = 0;
            Timeline.Playhead = 0;
        }

        StatusText.Text = "Undo";
        RefreshUi();
    }

    private void OnTimelineEditCompleted()
    {
        PlayerElement.MediaPlayer.Pause();

        if (_session.Clips.Count > 0)
            SeekTimeline(Math.Min(_timelinePosition, _session.TimelineDuration));
        else
        {
            _activeClipIndex = -1;
            _timelinePosition = 0;
            Timeline.Playhead = 0;
        }

        StatusText.Text = "Edited";
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
        if (_isExporting)
            return;

        if (_sourceFile is null)
        {
            await ShowErrorAsync("Import media first", "Open or drop a video before exporting.");
            return;
        }

        if (_session.Clips.Count == 0)
        {
            await ShowErrorAsync("Nothing to export", "The timeline is empty. Undo a deletion or open another video.");
            return;
        }

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
        ExportModeButton.IsEnabled = false;
        ImportModeButton.IsEnabled = false;
        OpenButton.IsEnabled = false;
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
            ExportButton.IsEnabled = true;
            ExportModeButton.IsEnabled = true;
            ImportModeButton.IsEnabled = true;
            OpenButton.IsEnabled = true;
            ExportProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void RefreshSelectionUi()
    {
        UpdateSelectionDetails();
        UpdateActionState();
    }

    private void UpdateSelectionDetails()
    {
        ClipCountText.Text = $"{_session.Clips.Count} clip{(_session.Clips.Count == 1 ? string.Empty : "s")}";

        if (_session.SelectedClip is { } clip && _session.SelectedIndex >= 0)
        {
            SelectedClipText.Text = $"Clip {_session.SelectedIndex + 1}";
            PropertyInText.Text = FormatTimecode(clip.SourceStart);
            PropertyOutText.Text = FormatTimecode(clip.SourceEnd);
        }
        else
        {
            SelectedClipText.Text = "No clip selected";
            PropertyInText.Text = "--:--:--:--";
            PropertyOutText.Text = "--:--:--:--";
        }
    }

    private void RefreshUi()
    {
        UpdateSelectionDetails();
        Timeline.Refresh();
        UpdateTimeText();
        UpdateActionState();
    }

    private void UpdateTimeText()
    {
        TimeText.Text = FormatTimecode(_timelinePosition);
        DurationText.Text = FormatTimecode(_session.TimelineDuration);
    }

    private void UpdateActionState()
    {
        SplitButton.IsEnabled = _sourceFile is not null && _session.CanSplitAtTimeline(_timelinePosition);
        DeleteButton.IsEnabled = _sourceFile is not null && _session.SelectedClip is not null;
        ExportButton.IsEnabled = !_isExporting;
        ExportModeButton.IsEnabled = !_isExporting;
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (ctrl && shift && e.Key == VirtualKey.L)
        {
            ApplyTheme(!_isLightTheme);
            e.Handled = true;
        }
        else if (ctrl && e.Key == VirtualKey.O)
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
        else if (e.Key == VirtualKey.S)
        {
            SplitAtPlayhead();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Delete)
        {
            DeleteSelectedClip();
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

    private string FormatTimecode(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
            seconds = 0;

        var fps = _mediaInfo?.FrameRate is > 0 ? _mediaInfo.FrameRate : 30d;
        var nominalFps = Math.Max(1, (int)Math.Round(fps));
        var wholeSeconds = (long)Math.Floor(seconds);
        var frames = Math.Clamp((int)Math.Floor((seconds - wholeSeconds) * fps), 0, nominalFps - 1);
        var time = TimeSpan.FromSeconds(wholeSeconds);
        return $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}:{frames:00}";
    }

    private static string FormatTime(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
            seconds = 0;
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes:00}:{time.Seconds:00}";
    }
}

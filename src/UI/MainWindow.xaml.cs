using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Cut.Core;

namespace Cut.UI;

public partial class MainWindow : Window
{
    private readonly Config _config;
    private readonly SegmentModel _segments = new();
    private List<TrackVm> _tracks = new();

    private MpvPlayer? _player;
    private MpvHost? _host;
    private MediaInfo? _media;
    private double _duration;
    private double _playhead;
    private bool _paused = true;
    private bool _pendingDurationReset;

    public MainWindow()
    {
        InitializeComponent();
        _config = Config.Load();
        Width = Math.Max(_config.WindowWidth, MinWidth);
        Height = Math.Max(_config.WindowHeight, MinHeight);
        PreviewVolumeSlider.Value = Math.Clamp(_config.PreviewVolume, 0, 100);

        Timeline.Model = _segments;
        Timeline.SeekRequested += OnTimelineSeek;
        _segments.Changed += () => Timeline.InvalidateVisual();
    }

    // ---- opening files ----

    public async void OpenFile(string path)
    {
        try
        {
            SetStatus("opening");
            await EnsurePlayerAsync();

            _media = null;
            _duration = 0;
            _playhead = 0;
            _pendingDurationReset = true;
            Timeline.Playhead = 0;

            _player!.Load(path);
            _player.SetVolume(PreviewVolumeSlider.Value);

            Title = "cut · " + Path.GetFileName(path);
            _config.LastDir = Path.GetDirectoryName(path);

            _media = await Probe.RunAsync(path);
            PopulateTracks();

            if (!_media.HasVideo)
                SetStatus("no video stream in this file");
            else
                SetStatus("");
        }
        catch (DllNotFoundException ex)
        {
            Logger.Error("libmpv load failed", ex);
            MessageBox.Show("libmpv-2.dll could not be loaded. reinstall cut to fix this.",
                "cut", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            Logger.Error($"open failed: {path}", ex);
            SetStatus("couldn't open this file");
        }
    }

    private async Task EnsurePlayerAsync()
    {
        if (_player != null) return;

        _host = new MpvHost { Focusable = false };
        VideoArea.Children.Add(_host);
        Placeholder.Visibility = Visibility.Collapsed;
        var wid = await _host.HwndReady;

        _player = new MpvPlayer(wid, Dispatcher);
        _player.TimePosChanged += t =>
        {
            _playhead = t;
            Timeline.Playhead = t;
            UpdateTimeText();
        };
        _player.DurationChanged += d =>
        {
            _duration = d;
            if (_pendingDurationReset)
            {
                _pendingDurationReset = false;
                _segments.Reset(d);
            }
            UpdateTimeText();
        };
        _player.PauseChanged += p =>
        {
            _paused = p;
            PlayBtn.Content = p ? "\uE768" : "\uE769"; // mdl2 play / pause glyphs // play / pause glyphs
        };
    }

    private void PopulateTracks()
    {
        _tracks = (_media?.AudioTracks ?? new List<AudioTrackInfo>())
            .Select(t => new TrackVm(t)).ToList();
        if (_tracks.Count > 0) _tracks[0].IsPreview = true;
        TracksList.ItemsSource = _tracks;
        // auto-open the panel when there is actually something to mix
        TracksPanel.Visibility = _tracks.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        TracksBtn.Visibility = _tracks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- playback ----

    private void TogglePause() => _player?.SetPause(!_paused);

    private void SeekRelative(double delta)
    {
        if (_player == null || _duration <= 0) return;
        var t = Math.Clamp(_playhead + delta, 0, _duration);
        _player.Seek(t, exact: true);
    }

    private void OnTimelineSeek(double t, bool final)
    {
        _player?.Seek(t, exact: final);
        _playhead = t;
        Timeline.Playhead = t;
        UpdateTimeText();
    }

    private void UpdateTimeText()
        => TimeText.Text = $"{TimeFmt.Clock(_playhead)} / {TimeFmt.Clock(_duration)}";

    private void SetStatus(string text) => StatusText.Text = text;

    // ---- editing ----

    private void SplitAtPlayhead()
    {
        if (_duration <= 0) return;
        if (!_segments.SplitAt(_playhead)) SetStatus("nothing to split here");
        else SetStatus("");
    }

    private void DeleteSelected()
    {
        if (Timeline.SelectedIndex < 0) return;
        if (!_segments.Delete(Timeline.SelectedIndex)) SetStatus("can't delete the last segment");
        else SetStatus("");
    }

    // ---- ui events ----

    private void OnOpenClick(object sender, RoutedEventArgs e) => OpenViaDialog();

    private void OpenViaDialog()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "video|*.mp4;*.mkv;*.mov;*.webm;*.avi;*.ts;*.m2ts;*.flv;*.wmv;*.m4v;*.mpg;*.mpeg|all files|*.*",
            InitialDirectory = _config.LastDir ?? "",
        };
        if (dlg.ShowDialog(this) == true) OpenFile(dlg.FileName);
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e) => TogglePause();
    private void OnFrameBackClick(object sender, RoutedEventArgs e) => _player?.FrameBack();
    private void OnFrameForwardClick(object sender, RoutedEventArgs e) => _player?.FrameStep();
    private void OnSplitClick(object sender, RoutedEventArgs e) => SplitAtPlayhead();

    private void OnTracksClick(object sender, RoutedEventArgs e)
        => TracksPanel.Visibility = TracksPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;

    private void OnPreviewVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => _player?.SetVolume(e.NewValue);

    private void OnPreviewChecked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TrackVm vm)
            _player?.SetAudioTrack(vm.Info.RelIndex + 1); // mpv aid is 1-based
    }

    private void OnMuteClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TrackVm vm) vm.ToggleMute();
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (_media == null || _duration <= 0)
        {
            SetStatus("open a video first");
            return;
        }
        if (!_media.HasVideo)
        {
            SetStatus("no video stream in this file");
            return;
        }
        var dlg = new ExportDialog(_media, _segments, _tracks.Select(t => t.ToSetting()).ToList())
        {
            Owner = this,
        };
        dlg.ShowDialog();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        switch (e.Key)
        {
            case Key.O when ctrl: OpenViaDialog(); break;
            case Key.E when ctrl: OnExportClick(this, new RoutedEventArgs()); break;
            case Key.Z when ctrl: _segments.Undo(); break;
            case Key.Y when ctrl: _segments.Redo(); break;
            case Key.Space: TogglePause(); break;
            case Key.Left when shift: SeekRelative(-1); break;
            case Key.Right when shift: SeekRelative(1); break;
            case Key.Left: _player?.FrameBack(); break;
            case Key.Right: _player?.FrameStep(); break;
            case Key.S: SplitAtPlayhead(); break;
            case Key.Delete: DeleteSelected(); break;
            case Key.Home: OnTimelineSeek(0, true); break;
            case Key.End when _duration > 0: OnTimelineSeek(_duration, true); break;
            default: return;
        }
        e.Handled = true;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            OpenFile(files[0]);
    }

    private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (WindowState == WindowState.Normal)
        {
            _config.WindowWidth = ActualWidth;
            _config.WindowHeight = ActualHeight;
        }
        _config.PreviewVolume = PreviewVolumeSlider.Value;
        _config.Save();
        _player?.Dispose();
    }
}

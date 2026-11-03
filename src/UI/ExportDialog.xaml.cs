using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Cut.Core;

namespace Cut.UI;

public partial class ExportDialog : Window
{
    private readonly MediaInfo _media;
    private readonly ProjectState _state;
    private readonly Keyframes? _keys;
    private readonly Config _config;
    private readonly bool _layers;
    private readonly int _sources;

    private bool _ready;
    private bool _outEdited;
    private bool _suppressOutEdited;
    private CancellationTokenSource? _cts;
    private string? _finishedPath;
    private VideoEncoder _encoder = VideoEncoder.X264;
    private double _fastDuration;
    private int _fastSkipped;

    // resolution choices: (label, value) where 0 = auto, -1 = source, else short side
    private readonly List<(string Label, int Value)> _res = new();
    private readonly List<(string Label, double Value)> _fps = new();

    public ExportDialog(MediaInfo media, ProjectState state, Keyframes? keys, Config config, bool fast)
    {
        InitializeComponent();
        _media = media;
        _state = state;
        _keys = keys;
        _config = config;
        _layers = state.HasVisualClips;
        _sources = state.AudioLanes.Count(l => l.Enabled) + (state.SoundClips.Any() ? 1 : 0);
        SourceInitialized += (_, _) => Native.DarkTitleBar(this);

        var shortSide = Math.Min(media.Width, media.Height);
        _res.Add(("auto", 0));
        _res.Add(($"source · {media.Width}×{media.Height}", -1));
        foreach (var h in Quality.Heights.Where(h => h < shortSide))
        {
            var (w, hh) = Quality.Dimensions(media, h);
            _res.Add(($"{h}p · {w}×{hh}", h));
        }
        foreach (var r in _res) ResBox.Items.Add(new ComboBoxItem { Content = r.Label });
        var srcFps = media.Fps > 0 ? media.Fps : 30;
        _fps.Add(("auto", 0));
        _fps.Add(($"source · {Math.Round(srcFps, 2):0.##}", -1));
        foreach (var f in new double[] { 60, 30, 24 }.Where(f => f < srcFps - 0.5)) _fps.Add(($"{f:0} fps", f));
        foreach (var f in _fps) FpsBox.Items.Add(new ComboBoxItem { Content = f.Label });

        ResBox.SelectedIndex = Math.Max(0, _res.FindIndex(r => r.Value == config.ExportHeight));
        FpsBox.SelectedIndex = Math.Max(0, _fps.FindIndex(f => Math.Abs(f.Value - config.ExportFps) < 0.01));
        SizeSlider.Value = Math.Clamp(config.ExportSizeSlider, 0, 1);
        if (config.ExportAudio == "separate") SeparateRadio.IsChecked = true;
        if (_sources < 2)
        {
            AudioLabel.Visibility = Visibility.Collapsed;
            AudioBox.Visibility = Visibility.Collapsed;
        }
        if (fast && !_layers) FastRadio.IsChecked = true;
        FastRadio.IsEnabled = !_layers;
        if (_layers) FastRadio.ToolTip = "layers need exact mode";

        var cuts = Math.Max(0, state.Segments.Length - 1) + (state.Segments[0].Start > 0.001 ? 1 : 0)
                   + (state.Segments[^1].End < media.Duration - 0.01 ? 1 : 0);
        var bits = new List<string> { $"keeps {TimeFmt.Clock(state.KeptDuration)} of {TimeFmt.Clock(media.Duration)}" };
        bits.Add(cuts == 0 ? "no cuts" : cuts == 1 ? "1 cut" : $"{cuts} cuts");
        var mutes = state.AudioLanes.Sum(l => l.Mutes.Length);
        if (mutes > 0) bits.Add(mutes == 1 ? "1 muted part" : $"{mutes} muted parts");
        var clips = state.AllClips.Count();
        if (clips > 0) bits.Add(clips == 1 ? "1 layer clip" : $"{clips} layer clips");
        SummaryText.Text = string.Join(" · ", bits);

        _ready = true;
        FillFormats();
        SetDefaultOutputPath();
        Refresh();
        _ = LoadEncoderAsync();
        _ = MeasureFastAsync();
    }

    private ExportMode Mode => FastRadio.IsChecked == true ? ExportMode.Fast : ExportMode.Exact;
    private AudioMode Audio => SeparateRadio.IsChecked == true ? AudioMode.Separate : AudioMode.Mix;
    private int AudioStreams => ExportPlan.OutputAudioStreams(_state, new ExportOptions { Mode = Mode, Audio = Audio });

    private string Ext => FormatBox.SelectedItem is ComboBoxItem { Tag: string t } ? (t == "same" ? _media.ContainerExt : t) : "mp4";

    private void FillFormats()
    {
        var keep = (FormatBox.SelectedItem as ComboBoxItem)?.Tag as string ?? _config.ExportFormat;
        FormatBox.Items.Clear();
        FormatBox.Items.Add(new ComboBoxItem { Content = "mp4", Tag = "mp4" });
        FormatBox.Items.Add(new ComboBoxItem { Content = "mkv", Tag = "mkv" });
        if (Mode == ExportMode.Fast && _media.ContainerExt is not ("mp4" or "mkv"))
            FormatBox.Items.Add(new ComboBoxItem { Content = $"same as input · {_media.ContainerExt}", Tag = "same" });
        var idx = 0;
        for (int i = 0; i < FormatBox.Items.Count; i++)
            if ((FormatBox.Items[i] as ComboBoxItem)?.Tag as string == keep) idx = i;
        FormatBox.SelectedIndex = idx;
    }

    private async Task LoadEncoderAsync()
    {
        _encoder = await Encoders.Best;
        Refresh();
    }

    // fast mode's real output length depends on where each cut lands; ask once
    private async Task MeasureFastAsync()
    {
        try
        {
            var o = new ExportOptions { Mode = ExportMode.Fast, Keys = _keys };
            await ExportJob.MeasureFastAsync(_media, _state, o, CancellationToken.None);
            var (eff, skipped) = Keyframes.Effective(_state.Segments, o.Start!);
            _fastDuration = eff.Sum(e => e.Effective.Duration);
            _fastSkipped = skipped;
            Refresh();
        }
        catch (Exception ex)
        {
            Logger.Warn($"fast measure: {ex.Message}");
        }
    }

    private double TargetBytes(double slider)
    {
        var original = Quality.OriginalBytes(_media, _state.KeptDuration, AudioStreams);
        return Quality.SliderToFraction(slider) * original;
    }

    private QualityPlan PlanFor(double slider)
        => Quality.Plan(_media, _state.KeptDuration, TargetBytes(slider), AudioStreams,
            _res[Math.Max(0, ResBox.SelectedIndex)].Value, _fps[Math.Max(0, FpsBox.SelectedIndex)].Value);

    private string Percent(double bytes)
    {
        if (_media.FileSize <= 0) return "";
        var pct = (1 - bytes / _media.FileSize) * 100;
        var orig = SizeFmt.Bytes(_media.FileSize);
        if (pct >= 1) return $"{pct:0}% smaller than the original ({orig})";
        if (pct <= -1) return $"{-pct:0}% bigger than the original ({orig})";
        return $"about the size of the original ({orig})";
    }

    private void Refresh()
    {
        if (!_ready) return;
        var exact = Mode == ExportMode.Exact;
        ExactPanel.Visibility = exact ? Visibility.Visible : Visibility.Collapsed;
        FastPanel.Visibility = exact ? Visibility.Collapsed : Visibility.Visible;
        ModeNote.Text = exact
            ? "re-encodes: every cut lands on the exact frame, layers are drawn in, and you choose the size."
            : "copies the original video, no quality loss and close to instant. each cut starts on the keyframe at or before it (amber on the timeline).";

        if (exact)
        {
            var plan = PlanFor(SizeSlider.Value);
            SizeText.Text = "≈ " + SizeFmt.Bytes(plan.EstimatedBytes);
            PercentText.Text = Percent(plan.EstimatedBytes);
            var fpsText = Math.Abs(plan.Fps - _media.Fps) < 0.01 ? $"{Math.Round(plan.Fps, 2):0.##} fps" : $"{plan.Fps:0} fps";
            var bpp = plan.VideoBitrate / (double)(plan.Width * plan.Height * Math.Max(1, plan.Fps));
            var feel = bpp >= 0.09 ? "looks like the original" : bpp >= 0.05 ? "good" : bpp >= 0.03 ? "fine for sharing" : "rough, small file";
            QualityText.Text = $"{plan.Width}×{plan.Height} · {fpsText} · video {SizeFmt.Bitrate(plan.VideoBitrate)} · {feel}";
            EncoderText.Text = $"encoder: {Encoders.Label(_encoder)}" + (Encoders.IsHardware(_encoder) ? " · fast" : "");
        }
        else
        {
            var dur = _fastDuration > 0 ? _fastDuration : _state.KeptDuration;
            var est = _media.Duration > 0 ? _media.FileSize * dur / _media.Duration : 0;
            var audioWork = _state.AudioLanes.Any(l => l.Enabled && l.NeedsAudioWork) || _state.SoundClips.Any()
                            || (Audio == AudioMode.Mix && _state.AudioLanes.Count(l => l.Enabled) > 1);
            FastSize.Text = "≈ " + SizeFmt.Bytes(est);
            FastPercent.Text = Percent(est);
            FastNote.Text = (_fastDuration > 0 ? $"output {TimeFmt.Clock(_fastDuration)} long once cuts snap to keyframes. " : "measuring where cuts land… ")
                            + (audioWork ? "audio gets re-encoded for the mutes and mixing, video stays untouched." : "nothing is re-encoded.");
            EncoderText.Text = "";
        }

        var warn = new List<string>();
        if (!exact && _fastSkipped > 0)
            warn.Add(_fastSkipped == 1
                ? "1 cut is shorter than the keyframe gap and will be skipped in fast mode. exact mode keeps it."
                : $"{_fastSkipped} cuts are shorter than the keyframe gap and will be skipped in fast mode. exact mode keeps them.");
        if (Audio == AudioMode.Separate && _sources >= 2 && Ext == "mp4")
            warn.Add("separate tracks: discord and most phones only play the first one.");
        WarnText.Text = string.Join("\n", warn);
        WarnText.Visibility = warn.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetDefaultOutputPath()
    {
        var dir = Path.GetDirectoryName(_media.FilePath) ?? "";
        var name = Path.GetFileNameWithoutExtension(_media.FilePath);
        _suppressOutEdited = true;
        OutBox.Text = Path.Combine(dir, $"{name}_cut.{Ext}");
        _suppressOutEdited = false;
    }

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        FillFormats();
        Refresh();
    }

    private void OnAudioChanged(object sender, RoutedEventArgs e) => Refresh();
    private void OnSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => Refresh();
    private void OnResChanged(object sender, SelectionChangedEventArgs e) => Refresh();

    private void OnPreset(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string tag) return;
        double frac;
        if (tag == "discord")
        {
            var original = Quality.OriginalBytes(_media, _state.KeptDuration, AudioStreams);
            frac = Quality.DiscordBytes / Math.Max(1, original);
        }
        else frac = double.Parse(tag, System.Globalization.CultureInfo.InvariantCulture);
        ResBox.SelectedIndex = 0;
        FpsBox.SelectedIndex = 0;
        SizeSlider.Value = Quality.FractionToSlider(Math.Min(1, frac));
        if (tag == "discord" && frac < 0.01)
            WarnText.Text = "this clip is long for 10 mb: it'll look rough. cut it shorter for a better result.";
    }

    private void OnFormatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || OutBox == null) return;
        if (!_outEdited) SetDefaultOutputPath();
        else if (Path.GetExtension(OutBox.Text) is { Length: > 1 })
        {
            _suppressOutEdited = true;
            OutBox.Text = Path.ChangeExtension(OutBox.Text, Ext);
            _suppressOutEdited = false;
        }
        Refresh();
    }

    private void OnOutEdited(object sender, TextChangedEventArgs e)
    {
        if (!_suppressOutEdited) _outEdited = true;
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = Path.GetFileName(OutBox.Text),
            InitialDirectory = Path.GetDirectoryName(OutBox.Text) ?? "",
            Filter = $"{Ext}|*.{Ext}|all files|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        _suppressOutEdited = true;
        OutBox.Text = dlg.FileName;
        _suppressOutEdited = false;
        _outEdited = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    public async Task<bool> RunExportAsync()
    {
        var outPath = OutBox.Text.Trim();
        if (string.IsNullOrEmpty(outPath))
        {
            ShowWarn("pick where to save it first");
            return false;
        }
        if (string.Equals(Path.GetFullPath(outPath), Path.GetFullPath(_media.FilePath), StringComparison.OrdinalIgnoreCase))
        {
            ShowWarn("the export can't overwrite the video it comes from");
            return false;
        }

        _config.ExportMode = Mode == ExportMode.Fast ? "fast" : "exact";
        _config.ExportAudio = Audio == AudioMode.Separate ? "separate" : "mix";
        _config.ExportSizeSlider = SizeSlider.Value;
        _config.ExportHeight = _res[Math.Max(0, ResBox.SelectedIndex)].Value;
        _config.ExportFps = _fps[Math.Max(0, FpsBox.SelectedIndex)].Value;
        _config.ExportFormat = (FormatBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "mp4";
        _config.Save();

        var options = new ExportOptions
        {
            Mode = Mode,
            Audio = Audio,
            ContainerExt = Ext,
            OutputPath = outPath,
            TargetBytes = SizeSlider.Value >= 0.999 ? 0 : TargetBytes(SizeSlider.Value),
            Height = _res[Math.Max(0, ResBox.SelectedIndex)].Value,
            Fps = _fps[Math.Max(0, FpsBox.SelectedIndex)].Value,
            Encoder = await Encoders.Best,
            Keys = _keys,
        };

        SetupPanel.Visibility = Visibility.Collapsed;
        RunPanel.Visibility = Visibility.Visible;
        RunStatus.Text = Mode == ExportMode.Fast ? "cutting" : "encoding";
        _cts = new CancellationTokenSource();
        var clock = Stopwatch.StartNew();

        var job = new ExportJob();
        job.Progress += p => Dispatcher.BeginInvoke(() =>
        {
            RunProgress.Value = p.Fraction * 100;
            var parts = new List<string> { $"{(int)(p.Fraction * 100)}%" };
            if (p.Speed > 0.05) parts.Add($"{p.Speed:0.0}× realtime");
            if (p.Eta is { } eta && p.Fraction < 0.999) parts.Add(eta.TotalSeconds < 60 ? $"{Math.Ceiling(eta.TotalSeconds)}s left" : $"{eta.Minutes}m {eta.Seconds:00}s left");
            RunDetail.Text = string.Join(" · ", parts);
        });
        job.Status += s => Dispatcher.BeginInvoke(() => RunStatus.Text = s);

        var (ok, error) = await job.RunAsync(_media, _state, options, _cts.Token);
        _cts = null;

        if (ok)
        {
            _finishedPath = outPath;
            var size = new FileInfo(outPath).Length;
            RunStatus.Text = "done";
            RunProgress.Value = 100;
            RunDetail.Text = $"{SizeFmt.Bytes(size)} · {Percent(size)} · took {TimeFmt.Dur(clock.Elapsed.TotalSeconds)}";
            ShowBtn.Visibility = Visibility.Visible;
            PlayBtn.Visibility = Visibility.Visible;
            AbortBtn.Content = "close";
            return true;
        }
        if (error == "cancelled")
        {
            RunPanel.Visibility = Visibility.Collapsed;
            SetupPanel.Visibility = Visibility.Visible;
            return false;
        }
        RunStatus.Text = "export failed";
        ErrorBox.Text = error ?? "unknown error";
        ErrorBox.Visibility = Visibility.Visible;
        AbortBtn.Content = "close";
        return false;
    }

    private void ShowWarn(string text)
    {
        WarnText.Text = text;
        WarnText.Visibility = Visibility.Visible;
    }

    private async void OnExportClick(object sender, RoutedEventArgs e) => await RunExportAsync();

    private void OnAbortClick(object sender, RoutedEventArgs e)
    {
        if (_cts != null) _cts.Cancel();
        else Close();
    }

    private void OnShowClick(object sender, RoutedEventArgs e)
    {
        if (_finishedPath != null && File.Exists(_finishedPath))
            Process.Start("explorer.exe", $"/select,\"{_finishedPath}\"");
    }

    private void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (_finishedPath != null && File.Exists(_finishedPath))
            Process.Start(new ProcessStartInfo(_finishedPath) { UseShellExecute = true });
    }

    // demo/test hooks
    public void SetSizeSlider(double v) => SizeSlider.Value = v;
    public void ClickPreset(string tag)
    {
        foreach (var child in Presets.Children)
            if (child is Button b && b.Tag as string == tag) OnPreset(b, new RoutedEventArgs());
    }
}

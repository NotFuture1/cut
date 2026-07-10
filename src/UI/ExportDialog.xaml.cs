using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Cut.Core;

namespace Cut.UI;

public partial class ExportDialog : Window
{
    private readonly MediaInfo _media;
    private readonly SegmentModel _segments;
    private readonly List<TrackSetting> _tracks;

    private bool _outEdited;
    private bool _suppressOutEdited;
    private CancellationTokenSource? _cts;
    private string? _finishedPath;

    public ExportDialog(MediaInfo media, SegmentModel segments, List<TrackSetting> tracks)
    {
        InitializeComponent();
        _media = media;
        _segments = segments;
        _tracks = tracks;

        SetDefaultOutputPath();
        UpdateNotes();

        var cuts = Math.Max(0, _segments.Segments.Count - 1);
        var cutsLabel = _segments.IsTrimmed
            ? (cuts == 0 ? "trimmed" : $"{cuts} cut{(cuts == 1 ? "" : "s")}")
            : "no cuts";
        SummaryText.Text =
            $"{cutsLabel} · keeps {TimeFmt.Clock(_segments.KeptDuration)} of {TimeFmt.Clock(_segments.TotalDuration)}";
    }

    private ExportMode Mode => ModeBox.SelectedIndex == 1 ? ExportMode.Precise : ExportMode.Lossless;

    private string? ContainerExt => FormatBox.SelectedIndex switch
    {
        1 => "mp4",
        2 => "mkv",
        _ => null, // same as input
    };

    private string ResolvedExt => ContainerExt ?? _media.ContainerExt;

    private void SetDefaultOutputPath()
    {
        var dir = Path.GetDirectoryName(_media.FilePath) ?? "";
        var name = Path.GetFileNameWithoutExtension(_media.FilePath);
        _suppressOutEdited = true;
        OutBox.Text = Path.Combine(dir, $"{name}_cut.{ResolvedExt}");
        _suppressOutEdited = false;
    }

    private void UpdateNotes()
    {
        if (NoteText == null) return; // xaml still initializing
        var anyVolumeChange = _tracks.Any(t => t.NeedsReencode);
        if (Mode == ExportMode.Lossless)
        {
            NoteText.Text = "copies the original streams, no quality loss. cuts snap to keyframes, so they can land up to a couple seconds early. use precise if the exact frame matters.";
            CrfLabel.Visibility = Visibility.Collapsed;
            CrfRow.Visibility = Visibility.Collapsed;
            WarnText.Visibility = anyVolumeChange ? Visibility.Visible : Visibility.Collapsed;
            WarnText.Text = "tracks with changed volume are re-encoded to aac, video stays untouched";
        }
        else
        {
            NoteText.Text = "re-encodes with x264, cuts are frame accurate. takes longer and is slightly lossy.";
            CrfLabel.Visibility = Visibility.Visible;
            CrfRow.Visibility = Visibility.Visible;
            WarnText.Visibility = Visibility.Collapsed;
        }
    }

    private void OnModeChanged(object sender, SelectionChangedEventArgs e) => UpdateNotes();

    private void OnFormatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OutBox == null) return;
        if (!_outEdited)
        {
            SetDefaultOutputPath();
        }
        else if (Path.GetExtension(OutBox.Text) is { Length: > 1 })
        {
            _suppressOutEdited = true;
            OutBox.Text = Path.ChangeExtension(OutBox.Text, ResolvedExt);
            _suppressOutEdited = false;
        }
    }

    private void OnCrfChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (CrfValue != null) CrfValue.Text = $"crf {(int)e.NewValue}";
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
            Filter = $"{ResolvedExt}|*.{ResolvedExt}|all files|*.*",
        };
        if (dlg.ShowDialog(this) == true)
        {
            _suppressOutEdited = true;
            OutBox.Text = dlg.FileName;
            _suppressOutEdited = false;
            _outEdited = true;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        var outPath = OutBox.Text.Trim();
        if (string.IsNullOrEmpty(outPath))
        {
            NoteText.Text = "pick an output path first";
            return;
        }
        if (string.Equals(Path.GetFullPath(outPath), Path.GetFullPath(_media.FilePath),
                StringComparison.OrdinalIgnoreCase))
        {
            NoteText.Text = "output can't overwrite the input file";
            return;
        }

        var settings = new ExportSettings
        {
            Mode = Mode,
            ContainerExt = ContainerExt,
            Crf = (int)CrfSlider.Value,
            OutputPath = outPath,
            Tracks = _tracks,
        };

        SetupPanel.Visibility = Visibility.Collapsed;
        RunPanel.Visibility = Visibility.Visible;
        _cts = new CancellationTokenSource();

        var job = new ExportJob();
        job.Progress += p => Dispatcher.BeginInvoke(() =>
        {
            RunProgress.Value = p * 100;
            RunPercent.Text = $"{(int)(p * 100)}%";
        });
        job.Status += s => Dispatcher.BeginInvoke(() => RunStatus.Text = s);

        var (ok, error) = await job.RunAsync(_media, _segments, settings, _cts.Token);

        if (ok)
        {
            _finishedPath = outPath;
            RunStatus.Text = "done";
            RunProgress.Value = 100;
            RunPercent.Text = "100%";
            ShowBtn.Visibility = Visibility.Visible;
            AbortBtn.Content = "close";
        }
        else if (error == "cancelled")
        {
            RunPanel.Visibility = Visibility.Collapsed;
            SetupPanel.Visibility = Visibility.Visible;
        }
        else
        {
            RunStatus.Text = "export failed";
            ErrorBox.Text = error ?? "unknown error";
            ErrorBox.Visibility = Visibility.Visible;
            AbortBtn.Content = "close";
        }
        _cts = null;
    }

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
}

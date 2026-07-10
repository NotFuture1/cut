using System.ComponentModel;
using Cut.Core;

namespace Cut.UI;

// row state for one audio track in the side panel
public sealed class TrackVm : INotifyPropertyChanged
{
    public AudioTrackInfo Info { get; }
    private double _volume = 100;
    private double _preMuteVolume = 100;
    private bool _include = true;
    private bool _isPreview;

    public TrackVm(AudioTrackInfo info) => Info = info;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(params string[] names)
    {
        foreach (var n in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public string Label => Info.Label;

    public bool Include
    {
        get => _include;
        set { if (_include != value) { _include = value; Notify(nameof(Include)); } }
    }

    // percent, 0..200
    public double Volume
    {
        get => _volume;
        set
        {
            value = Math.Clamp(value, 0, 200);
            if (Math.Abs(_volume - value) < 0.01) return;
            _volume = value;
            Notify(nameof(Volume), nameof(VolumeText), nameof(MuteGlyph));
        }
    }

    public bool IsPreview
    {
        get => _isPreview;
        set { if (_isPreview != value) { _isPreview = value; Notify(nameof(IsPreview)); } }
    }

    public string VolumeText => $"{(int)Math.Round(_volume)}%";
    public string MuteGlyph => _volume <= 0 ? "\uE74F" : "\uE767"; // mdl2 mute / volume glyphs

    public void ToggleMute()
    {
        if (_volume > 0)
        {
            _preMuteVolume = _volume;
            Volume = 0;
        }
        else
        {
            Volume = _preMuteVolume > 0 ? _preMuteVolume : 100;
        }
    }

    public TrackSetting ToSetting() => new()
    {
        RelIndex = Info.RelIndex,
        Include = Include,
        Volume = Volume / 100.0,
    };
}

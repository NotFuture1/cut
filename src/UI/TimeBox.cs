using System.Windows.Controls;
using System.Windows.Input;
using Cut.Core;

namespace Cut.UI;

// editable time field: shows m:ss.mmm, accepts 12.5 / 1:02.3 / 1:02:03, enter or blur
// commits, up/down nudge one frame (shift: one second), escape reverts.
public sealed class TimeBox : TextBox
{
    private double _value;
    public double Step { get; set; } = 1.0 / 30;
    public event Action<double>? Committed;

    public TimeBox()
    {
        Style = (System.Windows.Style)System.Windows.Application.Current.FindResource("DarkTextBox");
        Width = 104;
    }

    public double Value
    {
        get => _value;
        set
        {
            _value = value;
            if (!IsKeyboardFocused) Text = TimeFmt.Precise(value);
        }
    }

    private void Commit()
    {
        if (TimeFmt.Parse(Text) is double v)
        {
            if (Math.Abs(v - _value) > 1e-6) Committed?.Invoke(v);
        }
        Text = TimeFmt.Precise(_value);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                Commit();
                SelectAll();
                e.Handled = true;
                return;
            case Key.Escape:
                Text = TimeFmt.Precise(_value);
                Keyboard.ClearFocus();
                e.Handled = true;
                return;
            case Key.Up:
            case Key.Down:
            {
                var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 1 : Step;
                var v = Math.Max(0, (TimeFmt.Parse(Text) ?? _value) + (e.Key == Key.Up ? step : -step));
                Text = TimeFmt.Precise(v);
                Committed?.Invoke(v);
                SelectAll();
                e.Handled = true;
                return;
            }
        }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        Commit();
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        SelectAll();
    }
}

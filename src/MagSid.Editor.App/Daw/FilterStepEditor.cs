using MagSid.Core.Model;

namespace MagSid.Editor.App.Daw;

/// <summary>
/// The selected filter step, in full.
///
/// A sweep step only moves the cutoff, so that is all it shows. A setup step also writes $D418 -
/// filter mode and master volume - and $D417 - resonance and which voices pass through - plus the
/// sweep rate, and those are the four values the retro editor kept in hex fields and this view had
/// no way to reach at all.
///
/// The SETUP toggle converts between the two, which is a real change to the byte stream: a setup
/// record costs five bytes against a sweep step's one, and opens with $FF.
/// </summary>
internal sealed class FilterStepEditor : Panel
{
    private readonly Label _caption = new();
    private readonly ToggleButton _setup = new("SETUP");

    private readonly KnobControl _cutoff = new();
    private readonly KnobControl _volume = new();
    private readonly KnobControl _resonance = new();
    private readonly KnobControl _sweep = new();

    private readonly ToggleButton _lowPass = new("LP");
    private readonly ToggleButton _bandPass = new("BP");
    private readonly ToggleButton _highPass = new("HP");

    private readonly ToggleButton _voice1 = new("V1");
    private readonly ToggleButton _voice2 = new("V2");
    private readonly ToggleButton _voice3 = new("V3");

    private readonly CutoffView _cutoffGraph = new();

    private Instrument? _instrument;
    private int _step = -1;
    private bool _loading;

    public FilterStepEditor()
    {
        BackColor = DawPalette.Panel;
        Height = 132;
        DoubleBuffered = true;

        BuildLayout();
        Wire();
        ShowStep(null, -1);
    }

    public event Action? Edited;

    public void ShowStep(Instrument? instrument, int step)
    {
        _instrument = instrument;
        _step = step;
        _loading = true;

        try
        {
            bool has = instrument is not null && step >= 0 && step < instrument.Filter.Count;
            foreach (Control control in Controls)
                control.Visible = has || control == _caption;

            // The graph is of the whole program, so it stays while no step is picked. Decided in
            // a local: Visible reads false while the page is hidden, whatever it was set to.
            bool graph = instrument is not null && instrument.Filter.Count > 0;
            _cutoffGraph.Visible = graph;
            if (graph)
                _cutoffGraph.SetProgram(instrument!.Filter, has ? step : -1);

            if (!has)
            {
                _caption.Text = instrument is null || instrument.Filter.Count == 0
                    ? "No filter program — use + on the lane to start one"
                    : "Pick a filter step to edit it";
                _caption.ForeColor = DawPalette.TextFaint;
                return;
            }

            var current = instrument!.Filter[step];
            _caption.Text = current.IsSetup ? $"Step {step:00} — setup record" : $"Step {step:00} — cutoff only";
            _caption.ForeColor = DawPalette.HeaderInk(DawPalette.Filter);

            _setup.Checked = current.IsSetup;
            _cutoff.SetValueSilently(current.Cutoff);

            byte modeVolume = current.ModeVolume ?? 0x0F;
            byte resonanceRouting = current.ResonanceRouting ?? 0xF1;

            _volume.SetValueSilently(modeVolume & 0x0F);
            _lowPass.Checked = (modeVolume & 0x10) != 0;
            _bandPass.Checked = (modeVolume & 0x20) != 0;
            _highPass.Checked = (modeVolume & 0x40) != 0;

            _resonance.SetValueSilently(resonanceRouting >> 4);
            _voice1.Checked = (resonanceRouting & 0x01) != 0;
            _voice2.Checked = (resonanceRouting & 0x02) != 0;
            _voice3.Checked = (resonanceRouting & 0x04) != 0;

            _sweep.SetValueSilently(current.SweepRate);

            // A sweep step carries none of the rest, so showing its controls would invite edits
            // that go nowhere.
            foreach (var control in SetupOnlyControls())
                control.Visible = current.IsSetup;
        }
        finally
        {
            _loading = false;
        }
    }

    private IEnumerable<Control> SetupOnlyControls() =>
        [_volume, _resonance, _sweep, _lowPass, _bandPass, _highPass, _voice1, _voice2, _voice3];

    private void Commit()
    {
        if (_loading || _instrument is not { } instrument || _step < 0 || _step >= instrument.Filter.Count)
            return;

        byte cutoff = (byte)Math.Clamp(_cutoff.Value, FilterStep.MinCutoff, FilterStep.MaxCutoff);

        if (!_setup.Checked)
        {
            instrument.Filter[_step] = FilterStep.Sweep(cutoff);
        }
        else
        {
            int modeVolume = (_volume.Value & 0x0F)
                             | (_lowPass.Checked ? 0x10 : 0)
                             | (_bandPass.Checked ? 0x20 : 0)
                             | (_highPass.Checked ? 0x40 : 0);

            int resonanceRouting = ((_resonance.Value & 0x0F) << 4)
                                   | (_voice1.Checked ? 0x01 : 0)
                                   | (_voice2.Checked ? 0x02 : 0)
                                   | (_voice3.Checked ? 0x04 : 0);

            instrument.Filter[_step] = FilterStep.Setup(
                (byte)modeVolume, (byte)resonanceRouting, cutoff, _sweep.Value);
        }

        ShowStep(instrument, _step);
        Edited?.Invoke();
    }

    private void Wire()
    {
        foreach (var knob in new[] { _cutoff, _volume, _resonance, _sweep })
            knob.ValueChanged += _ => Commit();

        foreach (var toggle in new[] { _setup, _lowPass, _bandPass, _highPass, _voice1, _voice2, _voice3 })
            toggle.CheckedChanged += _ => Commit();
    }

    private void BuildLayout()
    {
        _caption.Font = new Font("Segoe UI", 7.5f, FontStyle.Bold);
        _caption.AutoSize = true;
        _caption.Location = new Point(12, 8);

        Knob(_cutoff, "Cutoff", FilterStep.MinCutoff, FilterStep.MaxCutoff, v => $"${v:X2}", 0x40, 12);
        Knob(_volume, "Vol", 0, 15, v => $"${v:X}", 15, 78);
        Knob(_resonance, "Res", 0, 15, v => $"${v:X}", 15, 144);
        Knob(_sweep, "Sweep", -128, 127, v => v.ToString("+#;-#;0"), 0, 210);

        // The three filter modes are independent bits in $D418 and really can be combined - a
        // notch is LP and HP together - so these latch rather than choosing one.
        Row([_lowPass, _bandPass, _highPass], 290, 26, "MODE");
        Row([_voice1, _voice2, _voice3], 290, 76, "ROUTING");

        _setup.Accent = DawPalette.Filter;
        _setup.Size = new Size(84, 26);
        _setup.Location = new Point(410, 26);

        Controls.Add(_caption);
        Controls.Add(_setup);
        Controls.Add(_cutoffGraph);
    }

    /// <summary>The cutoff graph takes everything right of SETUP.</summary>
    protected override void OnLayout(LayoutEventArgs levent)
    {
        const int left = 512;
        _cutoffGraph.Bounds = new Rectangle(left, 8, Math.Max(0, Width - left - 12), Math.Max(0, Height - 16));
        base.OnLayout(levent);
    }

    private void Knob(KnobControl knob, string caption, int minimum, int maximum,
                      Func<int, string> format, int defaultValue, int x)
    {
        knob.Caption = caption;
        knob.BindingKey = "filter." + caption.ToLowerInvariant();
        knob.Accent = DawPalette.Filter;
        knob.Minimum = minimum;
        knob.Maximum = maximum;
        knob.Format = format;
        knob.DefaultValue = defaultValue;
        knob.Location = new Point(x, 26);
        Controls.Add(knob);
    }

    private void Row(ToggleButton[] buttons, int x, int y, string caption)
    {
        var label = new Label
        {
            Text = caption,
            Font = new Font("Segoe UI", 6.5f, FontStyle.Bold),
            ForeColor = DawPalette.TextFaint,
            AutoSize = true,
            Location = new Point(x, y - 13),
        };
        Controls.Add(label);

        for (int i = 0; i < buttons.Length; i++)
        {
            buttons[i].Accent = DawPalette.Filter;
            buttons[i].Size = new Size(34, 24);
            buttons[i].Location = new Point(x + (i * 38), y);
            Controls.Add(buttons[i]);
        }
    }
}

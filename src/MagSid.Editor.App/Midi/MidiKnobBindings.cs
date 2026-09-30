using MagSid.Core.Model;
using MagSid.Editor.App.Daw;

namespace MagSid.Editor.App.Midi;

/// <summary>
/// Which controller turns which knob.
///
/// Held by binding key rather than by control, so a knob that is rebuilt when a different
/// instrument is selected keeps its controller, and so the bindings mean something after a restart.
///
/// One controller drives one knob. Binding a controller that is already spoken for moves it rather
/// than doubling it up: a surface where one encoder quietly changed two parameters would be a
/// puzzle to diagnose from the sound alone.
/// </summary>
internal sealed class MidiKnobBindings
{
    private readonly Dictionary<int, string> _byController = [];
    private readonly List<KnobControl> _knobs = [];

    private KnobControl? _learning;

    public MidiKnobBindings(string saved) => Load(saved);

    /// <summary>Raised whenever the bindings change, so the host can save them.</summary>
    public event Action? Changed;

    /// <summary>Takes a knob under management. Safe to call again for the same knob - the DAW
    /// rebuilds nothing, but a caller should not have to know that.</summary>
    public void Register(KnobControl knob)
    {
        if (knob.BindingKey.Length == 0 || _knobs.Contains(knob))
            return;

        _knobs.Add(knob);
        knob.MidiLearnRequested += StartLearning;
        knob.MidiForgetRequested += Forget;

        Apply(knob);
    }

    private void StartLearning(KnobControl knob)
    {
        bool again = ReferenceEquals(_learning, knob);
        StopLearning();

        if (again)
            return;

        _learning = knob;
        knob.Learning = true;
        knob.Invalidate();
    }

    private void StopLearning()
    {
        if (_learning is not { } knob)
            return;

        _learning = null;
        knob.Learning = false;
        knob.Invalidate();
    }

    private void Forget(KnobControl knob)
    {
        foreach (int controller in _byController
                     .Where(pair => pair.Value == knob.BindingKey)
                     .Select(pair => pair.Key)
                     .ToList())
        {
            _byController.Remove(controller);
        }

        knob.BoundController = null;
        knob.Invalidate();
        Changed?.Invoke();
    }

    /// <summary>
    /// A controller moved. Either it is the answer to a knob that is listening, or it drives
    /// whichever knob it is already bound to.
    /// </summary>
    public void HandleControlChange(int controller, int value)
    {
        if (_learning is { } learning)
        {
            // Whatever held this controller before loses it, so one encoder never drives two knobs.
            foreach (var other in _knobs.Where(k => k.BoundController == controller))
            {
                other.BoundController = null;
                other.Invalidate();
            }

            _byController.Remove(controller);
            foreach (int existing in _byController
                         .Where(pair => pair.Value == learning.BindingKey)
                         .Select(pair => pair.Key)
                         .ToList())
            {
                _byController.Remove(existing);
            }

            _byController[controller] = learning.BindingKey;
            learning.BoundController = controller;

            StopLearning();
            Changed?.Invoke();
            return;
        }

        if (!_byController.TryGetValue(controller, out string? key))
            return;

        foreach (var knob in _knobs.Where(k => k.BindingKey == key))
            knob.Value = MidiMapping.ScaleController(value, knob.Minimum, knob.Maximum);
    }

    /// <summary>Shows a knob whatever controller it already owns - called when it joins, since the
    /// bindings outlive any one instrument being on screen.</summary>
    private void Apply(KnobControl knob)
    {
        foreach (var (controller, key) in _byController)
        {
            if (key != knob.BindingKey)
                continue;

            knob.BoundController = controller;
            knob.Invalidate();
            return;
        }
    }

    /// <summary>"74=instrument.cutoff;71=instrument.attack" - plain enough to read in the settings
    /// file and to fix by hand when a surface is replaced.</summary>
    public string Save() =>
        string.Join(";", _byController.Select(pair => $"{pair.Key}={pair.Value}"));

    private void Load(string saved)
    {
        foreach (string entry in (saved ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = entry.Split('=', 2);
            if (parts.Length == 2 && int.TryParse(parts[0], out int controller) && parts[1].Length > 0)
                _byController[controller] = parts[1];
        }
    }
}

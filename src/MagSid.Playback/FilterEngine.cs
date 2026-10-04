using MagSid.Core.Model;

namespace MagSid.Playback;

/// <summary>
/// The SID's one shared filter program - whichever instrument last claimed it, across every
/// voice, owns it until another instrument with a filter program claims it in turn, exactly as
/// real hardware has one filter, not one per voice. Extracted out of <see cref="SongSequencer"/>
/// for the same reason <see cref="VoiceEngine"/> was: <see cref="JamEngine"/>'s chords claim the
/// filter through live NoteOn calls instead of pattern rows, but the stepping and sweep math
/// must be identical either way.
/// </summary>
public sealed class FilterEngine
{
    private IReadOnlyList<FilterStep>? _program;
    private int _stepIndex;

    /// <summary>The 0-based step last played, and the instrument whose program it belongs to -
    /// the SID has one filter, so whichever instrument claimed it last owns these.</summary>
    public int LastStep { get; private set; } = -1;
    public int OwnerInstrumentNumber { get; private set; } = -1;

    /// <summary>Frames stepped since the owner claimed the filter - 1 on the frame its first step
    /// plays - and the cutoff last written to $D416, by a step or by the sweep. For showing where
    /// the filter is, not used by the stepping itself.</summary>
    public int FramesSinceClaim { get; private set; }
    public int Cutoff { get; private set; } = -1;

    // The sweep outlives the program that started it, so it lives here rather than being reset
    // by Claim. A free-running phase, not a cutoff with bounds - see StepSweep.
    private int _phase;
    private int _sweepRate;

    /// <summary>Hands this instrument the shared filter, restarting its program from the top.
    /// Call only when the instrument actually has one (Filter.Count &gt; 0) - a note with no
    /// filter program of its own should leave whichever voice already owns the filter
    /// alone.</summary>
    public void Claim(IReadOnlyList<FilterStep> program, int instrumentNumber = -1)
    {
        _program = program;
        _stepIndex = 0;
        LastStep = -1;
        OwnerInstrumentNumber = instrumentNumber;
        FramesSinceClaim = 0;
    }

    /// <summary>One frame: the program step, then the sweep - the same order SongSequencer.Frame
    /// itself calls them in, so on a frame where both write $D416, the sweep is what
    /// stands.</summary>
    public void StepFrame(Action<int, int> write)
    {
        if (_program is not null)
            FramesSinceClaim++;

        StepProgram(write);
        StepSweep(write);
    }

    /// <summary>One filter step, once per frame. One-shot: when the program runs out the cutoff
    /// simply stays where it landed, which is what the assembly's $00 end marker does.</summary>
    private void StepProgram(Action<int, int> write)
    {
        if (_program is null || _stepIndex >= _program.Count)
        {
            return;
        }

        LastStep = _stepIndex;
        var step = _program[_stepIndex++];

        if (step.IsSetup)
        {
            write(0x18, step.ModeVolume!.Value);       // filter mode + master volume
            write(0x17, step.ResonanceRouting!.Value); // resonance + which voices are routed
            _sweepRate = step.SweepRate;                // 0 stops a sweep already running
        }

        write(0x16, step.Cutoff);
        Cutoff = step.Cutoff;
    }

    /// <summary>
    /// A free-running triangle rather than a cutoff walking between two bounds: the phase
    /// advances by the rate and wraps, and the cutoff is read off it by folding the top half
    /// back down and doubling. The fold is what makes it ping-pong, and it is why the player
    /// needs no bound bytes and none of the compares they would cost.
    /// </summary>
    private void StepSweep(Action<int, int> write)
    {
        if (_sweepRate == 0)
        {
            return;
        }

        _phase = (_phase + _sweepRate) & 0xFF;

        int folded = _phase >= 0x80 ? _phase ^ 0xFF : _phase;
        write(0x16, folded << 1);
        Cutoff = folded << 1;
    }
}

namespace MagSid.Playback;

/// <summary>
/// Which steps of one instrument's programs playback has actually reached. Answers the question
/// a program grid cannot on its own: a wavetable can carry eight steps and a song only ever play
/// the first three, and the other five are bytes in the exported .sid doing nothing.
///
/// Accumulated on the audio thread, one frame at a time, because that is the only place that sees
/// every frame - a UI poll at any interval would miss steps a short program passes through between
/// two ticks, and a step wrongly shown as unreached is the one mistake that matters here, since it
/// invites trimming something the song needs.
///
/// Plain bool arrays, written only ever from false to true and read without a lock: the audio
/// thread must not block, a torn read of a bool is not a thing, and a reader that catches a step
/// one frame late simply sees it on the next repaint.
/// </summary>
public sealed class ProgramCoverage
{
    /// <summary>Longer than any program the format can hold: a wavetable or note program is
    /// addressed by a single byte, and a filter program by a pool offset of the same width.</summary>
    public const int MaxSteps = 256;

    public bool[] Wavetable { get; } = new bool[MaxSteps];
    public bool[] Arpeggio { get; } = new bool[MaxSteps];
    public bool[] Filter { get; } = new bool[MaxSteps];

    /// <summary>The step each program last played, as the sequencer sees it. This is the render
    /// clock, which runs ahead of the sound by however deep the output buffer is - the editor's
    /// highlight uses SongPlaybackEngine.PlayheadAt instead, which is the same information tagged
    /// with the sample it belongs to. Kept because it is what the coverage itself is built from.
    /// </summary>
    public volatile int CurrentWavetableStep = -1;
    public volatile int CurrentArpeggioStep = -1;
    public volatile int CurrentFilterStep = -1;

    public bool IsCurrent(int row) =>
        row >= 0 && (row == CurrentWavetableStep || row == CurrentArpeggioStep || row == CurrentFilterStep);

    /// <summary>Bumped only when a step is reached for the first time, so a reader can tell
    /// whether the trail actually grew instead of repainting on the chance that it did.</summary>
    public volatile int Version;

    public void Record(bool[] target, int step)
    {
        if (step >= 0 && step < MaxSteps && !target[step])
        {
            target[step] = true;
            Version++;
        }
    }

    public void Clear()
    {
        CurrentWavetableStep = CurrentArpeggioStep = CurrentFilterStep = -1;
        Array.Clear(Wavetable);
        Array.Clear(Arpeggio);
        Array.Clear(Filter);
    }

    /// <summary>True when any of the three programs reached this row of the editor's grid, which
    /// shows one frame per row with the wavetable, note and filter steps side by side.</summary>
    public bool AnyReached(int row) =>
        row >= 0 && row < MaxSteps && (Wavetable[row] || Arpeggio[row] || Filter[row]);
}

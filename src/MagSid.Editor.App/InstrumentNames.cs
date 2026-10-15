using MagSid.Core.Model;

namespace MagSid.Editor.App;

/// <summary>
/// What an instrument is called on screen. An instrument nobody has named shows a name rather
/// than a blank - the way every tracker since the format's C64 ancestors greets one - and every
/// view shows the same one, so Retro's list and the DAW's never disagree about what it is called.
/// Purely a display default: the stored Name stays empty until something is typed over it.
/// </summary>
public static class InstrumentNames
{
    public const string Unnamed = "Flute";

    public static string Display(Instrument instrument) =>
        string.IsNullOrWhiteSpace(instrument.Name) ? Unnamed : instrument.Name;
}

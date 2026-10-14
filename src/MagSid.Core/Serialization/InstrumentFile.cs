using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MagSid.Core.Model;
using MagSid.Core.Validation;

namespace MagSid.Core.Serialization;

/// <summary>
/// One instrument as a JSON file of its own, so a sound can move between songs.
///
/// Bytes are written the way the editor shows them - <c>"$41"</c> - and read back from that, a
/// plain number, or <c>0x41</c>. Every field is written, so a file says exactly what it holds;
/// a missing one reads as what a bare instrument in a song file would have. The number is not
/// stored: it belongs to the song the instrument lands in.
/// </summary>
public static class InstrumentFile
{
    public const string FormatName = "MagSID instrument";
    public const int FormatVersion = 1;

    public static string Write(Instrument instrument)
    {
        var json = new JsonObject
        {
            ["format"] = FormatName,
            ["version"] = FormatVersion,
            ["name"] = instrument.Name,
            ["waveform"] = Hex(instrument.Waveform),
            ["attackDecay"] = Hex(instrument.AttackDecay),
            ["sustainRelease"] = Hex(instrument.SustainRelease),
            ["pulse"] = new JsonObject
            {
                ["width"] = instrument.PulseWidth,
                ["sweep"] = instrument.PulseSweep,
                ["continuous"] = instrument.PulseContinuous,
            },
            ["hardRestart"] = new JsonObject
            {
                ["off"] = instrument.NoHardRestart,
                ["soft"] = instrument.SoftHardRestart,
            },
            ["vibrato"] = new JsonObject
            {
                ["delaySpeed"] = Hex(instrument.VibratoDelaySpeed),
                ["depth"] = Hex(instrument.VibratoDepth),
            },
            ["wavetable"] = new JsonArray(instrument.Wavetable.Select(b => (JsonNode)Hex(b)).ToArray()),
            ["noteProgram"] = new JsonObject
            {
                ["steps"] = new JsonArray(instrument.Arpeggio.Select(n => (JsonNode)n.ToString()).ToArray()),
                ["ending"] = instrument.ArpeggioEnding.ToString().ToLowerInvariant(),
                ["loopPoint"] = instrument.ArpeggioLoopPoint,
            },
            ["filterProgram"] = new JsonArray(instrument.Filter.Select(FilterStepNode).ToArray()),
        };

        return json.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Reads an instrument file. Throws <see cref="InstrumentFileException"/> for anything
    /// that is not one, or that holds a program a song could not take.</summary>
    public static Instrument Read(string text)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(text) as JsonObject
                   ?? throw new InstrumentFileException("The file is not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InstrumentFileException($"The file is not valid JSON: {ex.Message}");
        }

        if (Str(root, "format") != FormatName)
            throw new InstrumentFileException($"Not a MagSID instrument - \"format\" must be \"{FormatName}\".");

        int version = Int(root, "version", 0);
        if (version > FormatVersion)
            throw new InstrumentFileException(
                $"The file is version {version}, newer than this editor reads ({FormatVersion}).");

        var instrument = new Instrument
        {
            Name = Str(root, "name") ?? string.Empty,
            Waveform = Byte(root, "waveform"),
            AttackDecay = Byte(root, "attackDecay"),
            SustainRelease = Byte(root, "sustainRelease"),
        };

        if (root["pulse"] is JsonObject pulse)
        {
            instrument.PulseWidth = Int(pulse, "width", Instrument.DefaultPulseWidth);
            instrument.PulseSweep = Int(pulse, "sweep", 0);
            instrument.PulseContinuous = Bool(pulse, "continuous");
        }

        if (root["hardRestart"] is JsonObject hardRestart)
        {
            instrument.NoHardRestart = Bool(hardRestart, "off");
            instrument.SoftHardRestart = Bool(hardRestart, "soft");
        }

        if (root["vibrato"] is JsonObject vibrato)
        {
            instrument.VibratoDelaySpeed = Byte(vibrato, "delaySpeed");
            instrument.VibratoDepth = Byte(vibrato, "depth");
        }

        foreach (var step in Array(root, "wavetable"))
            instrument.Wavetable.Add(ToByte(step, "wavetable"));

        if (root["noteProgram"] is JsonObject program)
        {
            foreach (var step in Array(program, "steps"))
            {
                string noteText = step is JsonValue v && v.GetValueKind() == JsonValueKind.String
                    ? v.GetValue<string>() : "";
                if (!NoteName.TryParse(noteText, out var note))
                    throw new InstrumentFileException($"noteProgram: \"{noteText}\" is not a note like \"C-4\" or \"C#4\".");
                instrument.Arpeggio.Add(note);
            }

            string ending = Str(program, "ending") ?? "loop";
            if (!Enum.TryParse<ArpeggioEnding>(ending, ignoreCase: true, out var parsed)
                || !Enum.IsDefined(parsed))
                throw new InstrumentFileException($"noteProgram: ending must be loop, hold or root, got \"{ending}\".");
            instrument.ArpeggioEnding = parsed;
            instrument.ArpeggioLoopPoint = Int(program, "loopPoint", 0);
        }

        foreach (var node in Array(root, "filterProgram"))
        {
            if (node is not JsonObject step)
                throw new InstrumentFileException("filterProgram: every step must be an object.");

            byte cutoff = Byte(step, "cutoff");
            instrument.Filter.Add(step["modeVolume"] is null
                ? FilterStep.Sweep(cutoff)
                : FilterStep.Setup(Byte(step, "modeVolume"), Byte(step, "resonanceRouting"), cutoff,
                                   Int(step, "sweep", 0)));
        }

        var issues = SongValidator.ValidateInstrument(instrument, "Instrument");
        if (issues.Count > 0)
            throw new InstrumentFileException(string.Join(Environment.NewLine, issues));

        return instrument;
    }

    private static JsonNode FilterStepNode(FilterStep step)
    {
        var node = new JsonObject();
        if (step.IsSetup)
        {
            node["modeVolume"] = Hex(step.ModeVolume!.Value);
            node["resonanceRouting"] = Hex(step.ResonanceRouting!.Value);
        }
        node["cutoff"] = Hex(step.Cutoff);
        if (step.IsSetup)
            node["sweep"] = step.SweepRate;
        return node;
    }

    private static string Hex(byte value) => "$" + value.ToString("X2", CultureInfo.InvariantCulture);

    private static IEnumerable<JsonNode?> Array(JsonObject parent, string name) => parent[name] switch
    {
        null => [],
        JsonArray array => array,
        _ => throw new InstrumentFileException($"{name} must be a list."),
    };

    private static byte Byte(JsonObject parent, string name) =>
        parent[name] is { } node ? ToByte(node, name) : (byte)0;

    private static byte ToByte(JsonNode? node, string name)
    {
        int value = ToInt(node, name);
        if (value is < 0 or > 0xFF)
            throw new InstrumentFileException($"{name} must be a byte, $00..$FF, got {value}.");
        return (byte)value;
    }

    private static int Int(JsonObject parent, string name, int fallback) =>
        parent[name] is { } node ? ToInt(node, name) : fallback;

    private static string? Str(JsonObject parent, string name) => parent[name] switch
    {
        null => null,
        JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
        _ => throw new InstrumentFileException($"{name} must be text."),
    };

    private static bool Bool(JsonObject parent, string name)
    {
        if (parent[name] is not { } node)
            return false;
        if (node.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
            return node.GetValue<bool>();
        throw new InstrumentFileException($"{name} must be true or false.");
    }

    /// <summary>A number, "$41", or "0x41".</summary>
    private static int ToInt(JsonNode? node, string name)
    {
        if (node is JsonValue value)
        {
            if (value.GetValueKind() == JsonValueKind.Number && value.TryGetValue(out int number))
                return number;

            if (value.GetValueKind() == JsonValueKind.String)
            {
                string text = value.GetValue<string>().Trim();
                string digits = text.StartsWith('$') ? text[1..]
                    : text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..]
                    : "";
                if (digits.Length > 0 && int.TryParse(digits, NumberStyles.AllowHexSpecifier,
                        CultureInfo.InvariantCulture, out int hex))
                    return hex;
                if (int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int plain))
                    return plain;
            }
        }

        throw new InstrumentFileException($"{name} must be a number or a hex value like \"$41\".");
    }
}

/// <summary>An instrument file that cannot be read, with a message fit to show as it is.</summary>
public sealed class InstrumentFileException(string message) : Exception(message);

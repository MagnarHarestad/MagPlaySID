namespace MagSid.Assembler.Tests;

/// <summary>
/// Pins the emitter's opcode table against Goldens\opcodes.txt.
///
/// That file is not a second copy of my own reading of the 6502 table. Every one of the 107 entries
/// was checked against the real c6510 instruction by instruction - each assembled into its own
/// four-byte slot so a disagreement about one length could not cascade - and the list was frozen
/// once that passed. The assembler is no longer in this repository, so freezing was the way to keep
/// the result.
///
/// This matters because the table is the one part of the emitter that cannot be reasoned about: it is
/// 106 magic numbers, and the asymmetric ones are not guessable. LDX indexes by Y and LDY by X; STX
/// has a zp,y form but no abs,y; STY has zp,x but no abs,x. A single wrong byte here produces a .sid
/// that plays and sounds wrong.
/// </summary>
public class OpcodeTableTests
{
    private static string GoldenPath =>
        Path.Combine(AppContext.BaseDirectory, "Goldens", "opcodes.txt");

    [Fact]
    public void Every_opcode_still_matches_what_c6510_assembled()
    {
        Assert.True(File.Exists(GoldenPath),
            "Missing Goldens\\opcodes.txt - see the README beside it.");

        var expected = File.ReadAllLines(GoldenPath)
            .Where(line => line.Trim().Length > 0)
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToDictionary(
                parts => (parts[0], Enum.Parse<AddressMode>(parts[1])),
                parts => Convert.ToByte(parts[2], 16));

        var actual = Opcodes.All.ToDictionary(e => (e.Mnemonic, e.Mode), e => e.Opcode);

        var problems = new List<string>();

        foreach (var (key, opcode) in expected.OrderBy(e => e.Key.Item1).ThenBy(e => e.Key.Item2))
        {
            if (!actual.TryGetValue(key, out byte mine))
                problems.Add($"{key.Item1} {key.Item2}: gone from the table, was ${opcode:X2}");
            else if (mine != opcode)
                problems.Add($"{key.Item1} {key.Item2}: table says ${mine:X2}, c6510 said ${opcode:X2}");
        }

        foreach (var (key, opcode) in actual.Where(e => !expected.ContainsKey(e.Key)))
        {
            problems.Add(
                $"{key.Item1} {key.Item2} (${opcode:X2}): added since the table was verified, and " +
                "nothing has checked it against an assembler");
        }

        Assert.True(problems.Count == 0,
            $"{problems.Count} of {expected.Count} opcodes no longer match:" + Environment.NewLine +
            string.Join(Environment.NewLine, problems));
    }

    /// <summary>The emitter must refuse a mode the 6502 does not have rather than emit some near-miss
    /// opcode - STY has no abs,x form, and a lookup that quietly fell back to another one would be
    /// silent corruption.</summary>
    [Fact]
    public void Refuses_a_mode_the_instruction_does_not_have()
    {
        var emitter = new Emitter6502(0x1000);
        var exception = Assert.Throws<InvalidOperationException>(
            () => emitter.Sty(Operand.MemX(0x1234)));
        Assert.Contains("STY", exception.Message);
    }

    /// <summary>JMP has no zero-page form, so a low target has to widen to absolute instead of
    /// failing. The reverse must never happen: demoting absolute to zero page would shorten an
    /// instruction and move everything after it.</summary>
    [Fact]
    public void Widens_to_absolute_when_there_is_no_zero_page_form()
    {
        var emitter = new Emitter6502(0x1000);
        var target = emitter.NewLabel("low");
        emitter.Jmp(target);
        target.Address = 0x0050;

        Assert.Equal(new byte[] { 0x4C, 0x50, 0x00 }, emitter.Assemble());
    }
}

using System.Diagnostics;

namespace MagSid.Assembler.Tests;

/// <summary>
/// Runs the real c6510 over a snippet of source, so these tests compare the emitter against the
/// assembler it has to replace rather than against a second copy of my own assumptions.
///
/// Deliberately throws when c6510 cannot be found instead of letting the tests skip. A silently
/// skipped differential test is worse than no test: it reports green while checking nothing, which
/// is exactly how a broken export driver and a renamed song file both got past this suite before.
/// </summary>
internal static class C6510
{
    /// <summary>The demos root that holds bin\ and include\ - eight levels up from the test
    /// binary: net8.0\Debug\bin\MagSid.Assembler.Tests\tests\MagSID_Editor\Magnar\z-archive.</summary>
    private static readonly string DemosRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "..", ".."));

    private static string ExecutablePath => Path.Combine(DemosRoot, "bin", "win_x86", "c6510.exe");
    private static string IncludePath => Path.Combine(DemosRoot, "include") + Path.DirectorySeparatorChar;

    public static string PlayerRoot => Path.Combine(DemosRoot, "z-archive", "Magnar", "MAGSIDv7_LowMemory");

    /// <summary>One label from the assembler's VICE-format symbol file.</summary>
    public readonly record struct Symbol(string Name, int Address);

    /// <summary>
    /// Builds one of the checked-in export_&lt;song&gt;.s drivers and returns the image together with
    /// its symbols.
    ///
    /// Those drivers name their song through v_songfile, so nothing in the working tree is touched -
    /// unlike SidExporter, which overwrites SongData\Patterns.s and restores it. A test that mutated
    /// shared source would be a poor neighbour to an editor session running at the same time.
    /// </summary>
    public static (byte[] Image, List<Symbol> Symbols) AssembleDriver(string driverFile, int origin = 0x1000)
    {
        RequireExecutable();

        string playerRoot = PlayerRoot;
        if (!File.Exists(Path.Combine(playerRoot, driverFile)))
        {
            throw new InvalidOperationException(
                $"{driverFile} is missing from {playerRoot} - renamed or deleted? Update this test " +
                "and whichever song it names.");
        }

        string tag = Guid.NewGuid().ToString("N");
        string prgPath = Path.Combine(playerRoot, $"_asmtest_{tag}.prg");
        string symPath = Path.Combine(playerRoot, $"_asmtest_{tag}.sym");
        try
        {
            Run(playerRoot, args =>
            {
                args.Add("-p");
                args.Add("../../../include/");
                // Both halves are needed when the origin moves: the declaration moves what the
                // driver assembles, and -s says which block to save. Passing only the second saves
                // from an address the assembler put nothing at, which comes back as filler. Drivers
                // that do not read v_export_loc ignore the declaration.
                args.Add("-d");
                args.Add($"v_export_loc=${origin:X4}");
                args.Add("-s");
                args.Add(prgPath);
                args.Add($"${origin:X4}");
                args.Add("+");
                args.Add(driverFile);
                args.Add("-y");
                args.Add(symPath);
            }, prgPath, driverFile);

            byte[] image = File.ReadAllBytes(prgPath)[2..];
            return (image, ParseSymbols(File.ReadAllText(symPath)));
        }
        finally
        {
            TryDelete(prgPath);
            TryDelete(symPath);
        }
    }

    /// <summary>Lines look like "add_label 123e .tbl_rows_pat01"; everything else in the file is
    /// VICE monitor commands this does not need.</summary>
    private static List<Symbol> ParseSymbols(string text)
    {
        var symbols = new List<Symbol>();
        foreach (string line in text.Split('\n'))
        {
            string[] parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || parts[0] != "add_label")
                continue;

            if (int.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out int address))
            {
                symbols.Add(new Symbol(parts[2].TrimStart('.'), address));
            }
        }

        return symbols;
    }

    /// <summary>Assembles <paramref name="source"/> and returns the memory image starting at
    /// <paramref name="origin"/>, with the .prg load address stripped.</summary>
    public static byte[] Assemble(string source, int origin)
    {
        RequireExecutable();

        string workDirectory = Path.Combine(Path.GetTempPath(), "magsid-asmtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDirectory);
        try
        {
            string sourcePath = Path.Combine(workDirectory, "snippet.s");
            string prgPath = Path.Combine(workDirectory, "snippet.prg");
            File.WriteAllText(sourcePath, source);

            Run(workDirectory, args =>
            {
                args.Add("-p");
                args.Add(IncludePath);
                args.Add("-s");
                args.Add(prgPath);
                args.Add($"${origin:X4}");
                args.Add("+");
                args.Add(sourcePath);
            }, prgPath, source);

            return File.ReadAllBytes(prgPath)[2..];
        }
        finally
        {
            try
            {
                Directory.Delete(workDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Scratch directory under %TEMP% - leaving it is harmless.
            }
        }
    }

    private static void RequireExecutable()
    {
        if (File.Exists(ExecutablePath))
            return;

        throw new InvalidOperationException(
            $"c6510 was not found at {ExecutablePath}. These tests compare the emitter against " +
            "the real assembler and cannot verify anything without it - the working copy needs " +
            "the top-level bin\\win_x86\\ and include\\ folders checked out alongside z-archive\\.");
    }

    private static void Run(
        string workingDirectory, Action<List<string>> addArguments, string expectedOutput, string what)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        var arguments = new List<string>();
        addArguments(arguments);
        foreach (string argument in arguments)
            psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start c6510.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0 || !File.Exists(expectedOutput))
        {
            throw new InvalidOperationException(
                $"c6510 failed (exit {process.ExitCode}) on:{Environment.NewLine}{what}" +
                $"{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // Scratch file beside the player source - leaving it is harmless.
        }
    }
}

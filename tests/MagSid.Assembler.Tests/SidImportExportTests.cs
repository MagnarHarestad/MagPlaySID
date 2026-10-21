using MagSid.Core.Serialization;
using MagSid.Core.SidImport;
using MagSid.Playback;

namespace MagSid.Assembler.Tests;

/// <summary>
/// Import SID against the 6502: a demo song built into a real .sid image imports through the same
/// path any downloaded tune takes, and a song Import SID made exports and plays on the 6502 as it
/// does in the editor - the same check every song in SongData gets.
/// </summary>
public class SidImportExportTests
{
    private static string SongDataDirectory => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "SongData"));

    /// <summary>A song as a minimal PSID: the player image at $1000, init $1000, play $1003.</summary>
    private static SidFile AsSid(MagSid.Core.Model.Song song)
    {
        var image = SongImageBuilder.Build(song);
        var header = new byte[0x7C];
        "PSID"u8.CopyTo(header);
        header[0x05] = 2;              // version
        header[0x07] = 0x7C;           // data offset
        header[0x0A] = 0x10;           // init $1000
        header[0x0C] = 0x10;           // play $1003
        header[0x0D] = 0x03;
        header[0x0F] = 1;              // one song
        header[0x11] = 1;              // starting with it
        "Test"u8.CopyTo(header.AsSpan(0x16));
        return SidFile.Parse([.. header, 0x00, 0x10, .. image]);
    }

    private static SongImport Import(SidTrace trace)
    {
        var original = SidTranscriber.Analyze(trace);
        var options = new SidImportOptions(SidTranscriber.SuggestFramesPerRow(original), 0, original.FrameCount);
        var result = SidTranscriber.Import(original, options);
        var replayed = SidTranscriber.Analyze(SidImportTrace.Replay(result.Song, options.FrameCount));
        return new SongImport(result, SidTranscriber.Agreement(original, options, replayed));
    }

    private sealed record SongImport(SidImportResult Result, double Agreement);

    [Fact]
    public void A_demo_song_built_as_a_sid_imports_through_the_6502()
    {
        var song = PatternsFileReader.Read(Path.Combine(SongDataDirectory, "Outlaws.s"));
        var file = AsSid(song);

        var import = Import(SidImportTrace.Record(file, file.StartSong, 50 * 36));

        Assert.True(import.Agreement >= 0.9, $"{import.Agreement:P1} of the notes came back");
        Assert.Equal(song.Speed, import.Result.Song.Speed);
    }

    [Theory]
    [InlineData("Lightforce.s")]
    [InlineData("Magnificent.s")]
    [InlineData("Breakdance.s")]
    public void An_imported_song_exports_and_plays_on_the_6502_as_in_the_editor(string file)
    {
        var song = PatternsFileReader.Read(Path.Combine(SongDataDirectory, file));
        var imported = Import(SidImportTrace.Replay(song, 50 * 30)).Result.Song;

        var image = SongImageBuilder.Build(imported);
        var fromCpu = new Dictionary<int, int>();
        var cpu = new PlayerCpu((register, value) => fromCpu[register] = value);
        Array.Copy(image, 0, cpu.Memory, 0x1000, image.Length);
        cpu.Call(0x1000, a: 0);

        var fromSequencer = new Dictionary<int, int>();
        var sequencer = new SongSequencer(imported, (register, value) => fromSequencer[register] = value & 0xFF);

        for (int frame = 0; frame < 50 * 30; frame++)
        {
            fromCpu.Clear();
            fromSequencer.Clear();
            cpu.Call(0x1003);
            sequencer.Frame();

            Assert.True(fromCpu.Count == fromSequencer.Count
                        && fromCpu.All(w => fromSequencer.TryGetValue(w.Key, out int v) && v == w.Value),
                $"{file}, frame {frame}: the 6502 and the sequencer disagree");
        }
    }
}

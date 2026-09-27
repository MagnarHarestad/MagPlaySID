using MagSid.Core.Model;
using Xunit;

namespace MagSid.Core.Tests;

public class NoteNameTests
{
    [Theory]
    [InlineData('C', false, 0, 0x00)]
    [InlineData('C', true, 0, 0x01)]
    [InlineData('B', false, 0, 0x0b)]
    [InlineData('C', false, 1, 0x0c)]
    [InlineData('B', false, 7, 0x5f)]
    public void TableIndex_matches_the_note_s_position_in_AllNotes(char letter, bool isSharp, int octave, int expected)
    {
        var note = new NoteName(letter, isSharp, octave);
        Assert.Equal(expected, note.TableIndex);
    }

    [Fact]
    public void TableIndex_is_the_note_s_own_position_within_AllNotes()
    {
        for (int i = 0; i < NoteName.AllNotes.Count; i++)
            Assert.Equal(i, NoteName.AllNotes[i].TableIndex);
    }
}

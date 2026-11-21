using SimpleMusicPro.Glyphs;

namespace SimpleMusicPro.Core.Tests;

public class GlyphTests
{
    [Fact]
    public void The_symbols_the_program_draws_are_all_there_with_sensible_outlines()
    {
        foreach (var glyph in Bravura.Every)
        {
            Assert.True(glyph.Steps.Count >= 3, $"{glyph.Name} has an outline");
            Assert.Equal('M', glyph.Steps[0].Kind);
            Assert.True(glyph.Width > 0 && glyph.Height > 0, $"{glyph.Name} has a size");
            Assert.True(glyph.Width < 12 && glyph.Height < 12, $"{glyph.Name} is not larger than a few staves");
        }
    }

    [Fact]
    public void A_black_notehead_is_the_size_and_shape_the_font_gives_it()
    {
        var head = Bravura.Get("noteheadBlack");
        Assert.Equal(1.18, head.Width, 0.02);      // a little wider than a staff space
        Assert.Equal(1.0, head.Height, 0.02);       // a staff space tall, as the font draws it
        Assert.Equal(0, (head.Top + head.Bottom) / 2, 0.02); // centred on the line or space it stands on
    }

    [Fact]
    public void A_treble_clef_reaches_above_and_below_the_staff_and_a_whole_note_has_a_hole()
    {
        var clef = Bravura.Get("gClef");
        Assert.True(clef.Top > 2.5 && clef.Bottom < -2, "the clef curls round its line and runs beyond the staff");
        // A whole note is a ring: two outlines (the second, wound the other way, is the hole).
        Assert.Equal(2, Bravura.Get("noteheadWhole").Steps.Count(s => s.Kind == 'M'));
    }
}

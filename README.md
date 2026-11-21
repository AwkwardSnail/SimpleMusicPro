# SimpleMusic Pro

A score maker by [Awkward Snail](https://awkwardsnail.com.au), a sister of [Simple Music Melody](https://github.com/AwkwardSnail/SimpleMusic) and [Simple Music Rhythm](https://github.com/AwkwardSnail/SimpleMusicRhythm). It turns a MIDI file into a printed score with a stack of staves, the way guitar and lead-sheet books are set, and writes it as a PDF.

**Status: early work.** What exists so far is the foundation: the music symbols (taken from the open music font Bravura) and the way they are drawn into a PDF. The MIDI import, the staves and the window are being built next.

## What it is for

A page of a score is a stack of lines, and each line is one of these kinds of staff:

1. **Rhythm staff**: slashes with stems, flags and beams, for strumming patterns.
2. **Chord diagrams**: the guitar chord shapes (Am, Am/G and so on) above the bars.
3. **Voice staff**: the notes on five lines, with clef, key and time signature.
4. **Lyrics**: words under the notes.
5. **Guitar tablature**: the six-line staff with fret numbers.

The window has a **control panel on the left** and a **preview on the right**. In the panel you choose which kinds of staff make up the stack, and, for a MIDI file you have opened, which track goes to which staff. The preview is the PDF as it will be printed.

Version 1 reads a MIDI file; chords are typed per bar (the diagram comes from a built-in set of shapes) and lyrics come from the MIDI file or are typed. Entering and editing notes by hand, and detecting chords, are left for later.

## Layout

- `engine/` - the PDF engine, [PdfEngine](https://github.com/AwkwardSnail/PdfEngine), a git submodule. It draws the page. Clone with `git clone --recurse-submodules` (or run `git submodule update --init` afterwards).
- `src/SimpleMusicPro.Core` - everything that is not the window: the music symbols (`Glyphs/`), and, as they are built, the MIDI reader, the score model and the engraving.
- `src/SimpleMusicPro.App` - the Windows program (WPF, .NET 10).
- `tests/SimpleMusicPro.Core.Tests` - xUnit tests.
- `tools/GlyphExtractor` - reads the outlines of the music symbols out of `Bravura.otf` and writes `BravuraData.g.cs`. Run it only to change which symbols are carried; the font is not in this repository (download it from [steinbergmedia/bravura](https://github.com/steinbergmedia/bravura), `redist/otf/Bravura.otf`):

  ```
  dotnet run --project tools/GlyphExtractor -- path/to/Bravura.otf src/SimpleMusicPro.Core/Glyphs/BravuraData.g.cs
  ```

## Building

Needs the .NET 10 SDK.

```
git clone --recurse-submodules https://github.com/AwkwardSnail/SimpleMusicPro.git
dotnet test SimpleMusicPro.sln
```

## Privacy

Like the other Awkward Snail programs, it collects no data and never goes online.

## Licence

MIT (see `LICENSE`). Forks are welcome but may not use the Awkward Snail name. The music symbols come from Bravura (SIL Open Font Licence 1.1) and the PDF engine builds on PDFium; see `THIRD-PARTY-NOTICES.txt`.

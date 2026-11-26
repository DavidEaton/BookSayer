# BookSayer
An open-source .NET application that transforms written content into natural-sounding, chapter-organized audiobooks using interchangeable speech synthesis providers, functional programming, and domain-driven design.

## Generate an audiobook

Install the .NET 10 SDK and put FFmpeg with the libmp3lame encoder on your PATH.
From the repository root, build the solution with `dotnet build BookSayer.slnx`.

For OpenAI narration, set `OPENAI_API_KEY` in your environment. The manuscript
segments and narration instructions are sent to the speech API and API usage is billed
to that account.

```sh
dotnet run --project src/BookSayer -- essay.md essay.mp3 --voice cedar --title "Functional Programming" --author "David Eaton" --instructions "Speak warmly, pause between ideas, and pronounce C# as C Sharp."
```

For offline narration, use an FFmpeg build with the Flite filter.

```sh
dotnet run --project src/BookSayer -- essay.txt essay.mp3 --provider flite --voice slt
```

Single- and double-hash Markdown headings and numbered chapter lines such as
“Chapter 1” or “Chapter IV” start chapters. BookSayer narrates their titles and
uses them as MP3 chapter markers. It groups paragraphs into bounded requests,
splitting long paragraphs at word boundaries where possible. Use a fresh MP3
filename for each export. Run with `--help` for all options.

Completed segments are stored as uncompressed audio under your local application
data directory in `BookSayer`. Repeating a run with the same text and narration
settings reuses them, including after an interruption. Use `--cache DIR` to
choose another location. You can delete that directory to reclaim disk space.

## Implementation

`Manuscript.Parse` turns text into immutable chapters and segments or an explicit
validation error. It runs without network, disk, or audio dependencies.
The console entry point passes those segments to a cached `ISpeechProvider`.
Both adapters return 24 kHz mono PCM, so the assembler can calculate chapter
offsets from sample counts and encode the whole audiobook in one FFmpeg pass.

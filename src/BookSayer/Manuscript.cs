using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;

namespace BookSayer;

internal sealed record Chapter(string Title, ImmutableArray<string> Segments);

internal abstract record ManuscriptResult
{
    internal sealed record Parsed(ImmutableArray<Chapter> Chapters) : ManuscriptResult;
    internal sealed record Invalid(string Reason) : ManuscriptResult;
}

internal static class Manuscript
{
    // UTF-8 bytes also bound the worst-case input token count for the speech model.
    private const int SegmentBytes = 1600;

    public static ManuscriptResult Parse(string document, string defaultTitle, bool markdown)
    {
        var chapters = ImmutableArray.CreateBuilder<Chapter>();
        var paragraphs = new List<string>();
        var lines = new List<string>();
        var title = defaultTitle;
        string? fence = null;

        void EndParagraph()
        {
            if (lines.Count == 0) return;
            paragraphs.Add(string.Join(" ", lines));
            lines.Clear();
        }

        void EndChapter()
        {
            EndParagraph();
            if (paragraphs.Count == 0) return;
            chapters.Add(new Chapter(title, Segment(paragraphs).ToImmutableArray()));
            paragraphs.Clear();
        }

        foreach (var raw in document.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.Trim();
            if (markdown && Regex.Match(line, @"^(`{3,}|~{3,})") is { Success: true } delimiter)
            {
                if (fence is null) { fence = delimiter.Value; continue; }
                if (line.StartsWith(fence, StringComparison.Ordinal) && line.All(c => c == fence[0]))
                { fence = null; continue; }
            }
            if (line.Length == 0) { EndParagraph(); continue; }
            var heading = markdown && fence is null ? Regex.Match(line, @"^#{1,2}\s+(.+)$") : Match.Empty;
            if (heading.Success || (fence is null && Regex.IsMatch(line, @"^chapter\s+(?:\d+|[ivxlcdm]+)\b", RegexOptions.IgnoreCase)))
            {
                EndChapter();
                title = heading.Success ? Regex.Replace(heading.Groups[1].Value, @"\s+#+$", "") : line;
                paragraphs.Add(title);
            }
            else
            {
                lines.Add(markdown && fence is null ? Regex.Replace(line, @"^#{3,6}\s+", "") : line);
            }
        }
        EndChapter();
        return chapters.Count == 0
            ? new ManuscriptResult.Invalid("The manuscript contains no text to narrate.")
            : new ManuscriptResult.Parsed(chapters.ToImmutable());
    }

    private static IEnumerable<string> Segment(IEnumerable<string> paragraphs)
    {
        var current = new StringBuilder();
        var bytes = 0;
        foreach (var paragraph in paragraphs)
        {
            if (bytes > 0 && bytes + 2 + Encoding.UTF8.GetByteCount(paragraph) > SegmentBytes)
            {
                yield return current.ToString();
                current.Clear();
                bytes = 0;
            }
            var separator = "\n\n";
            foreach (var word in paragraph.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (bytes + separator.Length + Encoding.UTF8.GetByteCount(word) > SegmentBytes && bytes > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                    bytes = 0;
                }
                if (bytes > 0) { current.Append(separator); bytes += separator.Length; }
                foreach (var rune in word.EnumerateRunes())
                {
                    if (bytes + rune.Utf8SequenceLength > SegmentBytes)
                    {
                        yield return current.ToString();
                        current.Clear();
                        bytes = 0;
                    }
                    current.Append(rune.ToString());
                    bytes += rune.Utf8SequenceLength;
                }
                separator = " ";
            }
        }
        if (bytes > 0) yield return current.ToString();
    }
}

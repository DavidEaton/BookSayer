using BookSayer;

const string usage = """
    BookSayer <input.md|input.txt> <output.mp3> [options]
      --provider openai|flite   Speech provider (default: openai)
      --voice NAME              OpenAI voice (default: cedar) or Flite voice (default: slt)
      --model NAME              OpenAI speech model (default: gpt-4o-mini-tts)
      --instructions TEXT       OpenAI narration direction
      --title TEXT              Album title (default: input filename without extension)
      --author TEXT             Artist metadata
      --cache DIR               PCM segment cache (default: local application data/BookSayer)
    """;

if (args is ["--help"] or ["-h"]) { Console.WriteLine(usage); return 0; }
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
var token = cancellation.Token;
try
{
    if (args.Length < 2 || args.Length % 2 != 0) throw new ArgumentException(usage);
    var options = new Dictionary<string, string>();
    for (int i = 2; i < args.Length; i += 2)
    {
        if (args[i] is not ("--provider" or "--voice" or "--model" or "--instructions" or "--title" or "--author" or "--cache")
            || string.IsNullOrWhiteSpace(args[i + 1]) || !options.TryAdd(args[i], args[i + 1]))
            throw new ArgumentException($"Invalid or duplicate option {args[i]}.\n{usage}");
    }
    var input = Path.GetFullPath(args[0]);
    var output = Path.GetFullPath(args[1]);
    var extension = Path.GetExtension(input).ToLowerInvariant();
    if (extension is not (".md" or ".markdown" or ".txt") || !output.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException("Expected a Markdown or text input and an MP3 output.");
    if (File.Exists(output)) throw new IOException($"Output already exists: {output}");
    var provider = options.GetValueOrDefault("--provider", "openai");
    var local = provider == "flite";
    if (!local && provider != "openai") throw new ArgumentException("Choose openai or flite.");
    var voice = options.GetValueOrDefault("--voice", local ? "slt" : "cedar");
    if (local && (options.ContainsKey("--instructions") || options.ContainsKey("--model")))
        throw new ArgumentException("Model and instructions apply only to OpenAI narration.");
    if (local && voice is not ("slt" or "kal" or "awb" or "rms" or "kal16"))
        throw new ArgumentException("Choose a Flite voice: slt, kal, awb, rms or kal16.");
    var narrator = new Narrator(provider, voice, local ? "flite" : options.GetValueOrDefault("--model", "gpt-4o-mini-tts"),
        local ? "" : options.GetValueOrDefault("--instructions", "Narrate warmly and conversationally. Preserve every word. Pronounce C# as C Sharp."));
    var title = options.GetValueOrDefault("--title", Path.GetFileNameWithoutExtension(input));
    var result = Manuscript.Parse(await File.ReadAllTextAsync(input, token), title, extension != ".txt");
    if (result is ManuscriptResult.Invalid invalid) throw new InvalidDataException(invalid.Reason);
    var chapters = ((ManuscriptResult.Parsed)result).Chapters;

    var cacheDir = Path.GetFullPath(options.GetValueOrDefault("--cache",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BookSayer")));
    Directory.CreateDirectory(cacheDir);
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    var work = Directory.CreateTempSubdirectory("booksayer-").FullName;
    var pending = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
    try
    {
        using var http = new HttpClient();
        ISpeechProvider speech = local ? new FliteSpeech(voice, work) : new OpenAiSpeech(http, narrator,
            Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? throw new ArgumentException("Set OPENAI_API_KEY for OpenAI narration."));
        var cache = new SpeechCache(cacheDir, narrator, speech);
        var recordings = new List<Recording>();
        foreach (var chapter in chapters)
        {
            Console.Error.WriteLine($"[{recordings.Count + 1}/{chapters.Length}] {chapter.Title}");
            var files = new List<string>();
            foreach (var segment in chapter.Segments) files.Add(await cache.Get(segment, token));
            recordings.Add(new Recording(chapter.Title, files));
        }
        await Audio.Assemble(work, pending, title, options.GetValueOrDefault("--author", ""), recordings, token);
        File.Move(pending, output);
        Console.WriteLine(output);
    }
    finally { File.Delete(pending); Directory.Delete(work, recursive: true); }
    return 0;
}
catch (OperationCanceledException) when (token.IsCancellationRequested) { Console.Error.WriteLine("Cancelled."); return 130; }
catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }

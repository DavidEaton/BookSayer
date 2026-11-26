using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace BookSayer;

internal sealed record Narrator(string Provider, string Voice, string Model, string Instructions);

internal interface ISpeechProvider
{
    // All adapters produce mono 24 kHz signed little-endian 16-bit PCM.
    Task Synthesize(string text, string destination, CancellationToken token);
}

internal sealed class OpenAiSpeech(HttpClient http, Narrator narrator, string key) : ISpeechProvider
{
    public async Task Synthesize(string text, string destination, CancellationToken token)
    {
        for (int attempt = 0; ; attempt++)
        {
            var delay = TimeSpan.FromSeconds(1 << attempt);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/audio/speech");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                request.Content = JsonContent.Create(new { model = narrator.Model, voice = narrator.Voice,
                    instructions = narrator.Instructions, input = text, response_format = "pcm" });
                using var response = await http.SendAsync(request, token);
                if (response.IsSuccessStatusCode)
                {
                    await using var output = File.Create(destination);
                    await response.Content.CopyToAsync(output, token);
                    return;
                }
                if (attempt >= 3 || ((int)response.StatusCode < 500 &&
                    response.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout)))
                    throw new InvalidOperationException($"OpenAI speech request failed ({(int)response.StatusCode} {response.ReasonPhrase}).");
                var retry = response.Headers.RetryAfter;
                delay = retry?.Delta ?? (retry?.Date - DateTimeOffset.UtcNow) ?? delay;
            }
            catch (HttpRequestException) when (attempt < 3) { }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && attempt < 3) { }
            Console.Error.WriteLine($"Retrying speech request ({attempt + 1}/3).");
            await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.Zero, token);
        }
    }
}

internal sealed class FliteSpeech(string voice, string work) : ISpeechProvider
{
    public async Task Synthesize(string text, string destination, CancellationToken token)
    {
        await File.WriteAllTextAsync(Path.Combine(work, "speech.txt"), text, token);
        await Audio.Run(work, ["-f", "lavfi", "-i", $"flite=textfile=speech.txt:voice={voice}",
            "-ac", "1", "-ar", "24000", "-f", "s16le", "-y", destination], null, token);
    }
}

internal sealed class SpeechCache(string directory, Narrator narrator, ISpeechProvider provider)
{
    public async Task<string> Get(string text, CancellationToken token)
    {
        var identity = JsonSerializer.SerializeToUtf8Bytes(new { format = "pcm-s16le-24000-mono-v1", narrator, text });
        var path = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(identity)) + ".pcm");
        if (File.Exists(path) && Valid(path)) return path;

        var temp = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            await provider.Synthesize(text, temp, token);
            if (!Valid(temp)) throw new InvalidDataException("Speech provider returned incomplete PCM audio.");
            File.Move(temp, path, overwrite: true);
            return path;
        }
        finally { File.Delete(temp); }
    }

    private static bool Valid(string path)
    {
        long size = new FileInfo(path).Length;
        return size > 0 && size % 2 == 0;
    }
}

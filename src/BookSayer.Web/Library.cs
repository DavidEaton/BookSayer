using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace BookSayer.Web;

public record Book(string Id, string Title, string State);
public sealed class Library(BlobContainerClient manuscripts, BlobContainerClient audio)
{
    public const int MaxBytes = 128 * 1024;
    const int DailyLimit = 5;
    internal BlobContainerClient Manuscripts => manuscripts;
    internal BlobContainerClient Audio => audio;

    public async Task<bool> Submit(string owner, string name, string extension, byte[] text, CancellationToken token)
    {
        var day = DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        for (int slot = 1; slot <= DailyLimit; slot++)
        {
            var blob = manuscripts.GetBlobClient($"{owner}{day}-{slot}");
            try
            {
                await blob.UploadAsync(BinaryData.FromBytes(text), new BlobUploadOptions
                {
                    Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                    Metadata = new Dictionary<string, string> {
                        ["title"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(name)),
                        ["extension"] = extension, ["state"] = "Queued"
                    }
                }, token);
                return true;
            }
            catch (RequestFailedException ex) when (ex.ErrorCode is "BlobAlreadyExists" or "ConditionNotMet") { }
        }
        return false;
    }

    public async Task<List<Book>> List(string owner, CancellationToken token)
    {
        var books = new List<Book>();
        await foreach (var item in manuscripts.GetBlobsAsync(BlobTraits.Metadata, prefix: owner, cancellationToken: token))
            books.Add(new Book(item.Name[owner.Length..], Title(item.Metadata), item.Metadata["state"]));
        books.Reverse();
        return books;
    }

    internal static string Title(IDictionary<string, string> metadata) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(metadata["title"]));

    public async Task<Stream?> OpenAudio(string owner, string id, CancellationToken token)
    {
        if (!Regex.IsMatch(id, @"\A[0-9]{8}-[1-5]\z")) return null;
        var name = owner + id;
        try
        {
            var properties = await manuscripts.GetBlobClient(name).GetPropertiesAsync(cancellationToken: token);
            if (properties.Value.Metadata["state"] != "Ready") return null;
            return await audio.GetBlobClient(name).OpenReadAsync(cancellationToken: token);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { return null; }
    }
}

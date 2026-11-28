using System.Diagnostics;
using Azure;
using Azure.Storage.Blobs.Models;

namespace BookSayer.Web;

public sealed class NarrationWorker(Library library, string cliPath)
{
    // A bounded execution lets the next scheduled job reclaim a crashed worker's item.
    static readonly TimeSpan RenderLimit = TimeSpan.FromMinutes(45);
    public async Task Run(CancellationToken token)
    {
        await foreach (var item in library.Manuscripts.GetBlobsAsync(BlobTraits.Metadata, cancellationToken: token))
        {
            string state = item.Metadata["state"];
            if (state != "Queued" && !(state == "Processing" &&
                item.Properties.LastModified < DateTimeOffset.UtcNow - RenderLimit - TimeSpan.FromMinutes(5))) continue;

            var source = library.Manuscripts.GetBlobClient(item.Name);
            var metadata = item.Metadata;
            metadata["state"] = "Processing";
            var condition = new BlobRequestConditions { IfMatch = item.Properties.ETag };
            try
            {
                var claim = await source.SetMetadataAsync(metadata, condition, token);
                condition.IfMatch = claim.Value.ETag;
            }
            catch (RequestFailedException ex) when (ex.Status == 412) { continue; }

            var work = Directory.CreateTempSubdirectory("booksayer-job-").FullName;
            try
            {
                var input = Path.Combine(work, "manuscript" + metadata["extension"]);
                var output = Path.Combine(work, "audio.mp3");
                var target = library.Audio.GetBlobClient(item.Name);
                // A previous execution may have published the MP3 before its final status write.
                if (!(await target.ExistsAsync(token)).Value)
                {
                    await source.DownloadToAsync(input, token);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(RenderLimit);
                    bool success;
                    try { success = await Render(input, output, Library.Title(metadata), work, deadline.Token); }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested) { success = false; }
                    if (!success)
                    {
                        metadata["state"] = "Failed";
                        await source.SetMetadataAsync(metadata, condition, token);
                        return;
                    }
                    try { await target.UploadAsync(output, overwrite: false, cancellationToken: token); }
                    catch (RequestFailedException ex) when (ex.ErrorCode == "BlobAlreadyExists") { }
                }
                metadata["state"] = "Ready";
                await source.SetMetadataAsync(metadata, condition, token);
            }
            finally { Directory.Delete(work, true); }
            return;
        }
    }

    private async Task<bool> Render(string input, string output, string title, string work, CancellationToken token)
    {
        var info = new ProcessStartInfo("dotnet") { WorkingDirectory = work, RedirectStandardError = true,
            RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var arg in new[] { cliPath, input, output, "--provider", "flite", "--title", title,
                     "--cache", Path.Combine(work, "cache") }) info.ArgumentList.Add(arg);
        using var child = Process.Start(info) ?? throw new IOException("Failed to start narrator.");
        // CLI progress contains manuscript headings. Keep them out of shared worker logs.
        var drain = Task.WhenAll(child.StandardOutput.BaseStream.CopyToAsync(Stream.Null),
                                 child.StandardError.BaseStream.CopyToAsync(Stream.Null));
        try { await child.WaitForExitAsync(token); }
        catch (OperationCanceledException)
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync();
            throw;
        }
        finally { await drain; }
        return child.ExitCode == 0;
    }
}

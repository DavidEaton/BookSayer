using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Azure.Storage.Blobs;
using BookSayer.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace BookSayer.Web.Tests;

public class ReaderFlow
{
    internal const string Tenant = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    const string Reader = "11111111-2222-3333-4444-555555555555";
    const string Other = "21111111-2222-3333-4444-555555555555";

    [Fact]
    public async Task OnlyInvitedOwnerCanUploadListAndDownload()
    {
        var service = new BlobServiceClient("UseDevelopmentStorage=true");
        var run = Guid.NewGuid().ToString("N");
        var sources = service.GetBlobContainerClient("source-" + run);
        var audio = service.GetBlobContainerClient("audio-" + run);
        await sources.CreateAsync();
        await audio.CreateAsync();
        try
        {
            var store = new Library(sources, audio);
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
            {
                web.UseSetting("StorageUri", "https://example.blob.core.windows.net");
                web.UseSetting("PublicOrigin", "https://localhost");
                web.UseSetting("Entra:TenantId", Tenant);
                web.UseSetting("Entra:ClientId", "bbbbbbbb-1111-2222-3333-444444444444");
                web.UseSetting("Entra:ClientSecret", "integration-test");
                web.UseSetting("ProtectionKeyUri", "https://example.vault.azure.net/keys/test");
                web.ConfigureTestServices(services =>
                {
                    services.AddSingleton(store);
                    services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
                    services.AddAuthentication(options =>
                    {
                        options.DefaultScheme = options.DefaultChallengeScheme = "Fixture";
                    }).AddScheme<AuthenticationSchemeOptions, FixtureIdentity>("Fixture", _ => { });
                });
            });
            using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/")).StatusCode);
            client.DefaultRequestHeaders.Add("Reader", Reader);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/")).StatusCode);
            client.DefaultRequestHeaders.Add("Invited", "true");
            var page = await client.GetStringAsync("/");
            var csrf = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
            Assert.NotEmpty(csrf);
            using var form = new MultipartFormDataContent { { new StringContent("# Opening\nHello from BookSayer."), "manuscript", "my-story.md" } };
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/", form)).StatusCode);
            form.Add(new StringContent(csrf), "__RequestVerificationToken");
            Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync("/", form)).StatusCode);
            Assert.Contains("Queued", await client.GetStringAsync("/"));
            var cli = Environment.GetEnvironmentVariable("BOOKSAYER_TEST_CLI") ??
                throw new InvalidOperationException("Set BOOKSAYER_TEST_CLI to the built CLI DLL.");
            await new NarrationWorker(store, cli).Run(CancellationToken.None);
            page = await client.GetStringAsync("/");
            var url = Regex.Match(page, "href=\"(/audio/[^\"]+)\"").Groups[1].Value;
            Assert.NotEmpty(url);
            Assert.NotEmpty(await client.GetByteArrayAsync(url));
            client.DefaultRequestHeaders.Remove("Reader");
            client.DefaultRequestHeaders.Add("Reader", Other);
            Assert.DoesNotContain("my-story.md", await client.GetStringAsync("/"));
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(url)).StatusCode);
        }
        finally { await audio.DeleteAsync(); await sources.DeleteAsync(); }
    }
}

internal class FixtureIdentity(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Reader", out var reader)) return Task.FromResult(AuthenticateResult.NoResult());
        var claims = new List<Claim> { new("tid", ReaderFlow.Tenant), new("oid", reader.ToString()) };
        if (Request.Headers.ContainsKey("Invited")) claims.Add(new Claim(ClaimTypes.Role, "BookSayer.User"));
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}

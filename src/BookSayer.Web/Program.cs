using Azure.Identity;
using Azure.Storage.Blobs;
using BookSayer.Web;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);
string Required(string name) => builder.Configuration[name] ?? throw new InvalidOperationException($"Missing {name}.");
var credential = new DefaultAzureCredential();
var blobs = new BlobServiceClient(new Uri(Required("StorageUri")), credential);
var library = new Library(blobs.GetBlobContainerClient("manuscripts"), blobs.GetBlobContainerClient("audio"));
if (args.Contains("--worker"))
{
    using var host = Host.CreateDefaultBuilder().Build();
    await host.StartAsync();
    try { await new NarrationWorker(library, Required("CliPath")).Run(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping); }
    finally { await host.StopAsync(); }
    return;
}

var tenant = Guid.Parse(Required("Entra:TenantId")).ToString();
var origin = new Uri(Required("PublicOrigin"));
if (origin.Scheme != "https") throw new InvalidOperationException("PublicOrigin must use HTTPS.");
builder.Services.AddSingleton(library);
builder.Services.AddDataProtection().SetApplicationName("BookSayer")
    .PersistKeysToAzureBlobStorage(blobs.GetBlobContainerClient("keys").GetBlobClient("cookies.xml"))
    .ProtectKeysWithAzureKeyVault(new Uri(Required("ProtectionKeyUri")), credential);
builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
}).AddCookie(options =>
{
    options.Cookie.Name = "__Host-BookSayer";
    options.Cookie.Path = "/";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.ExpireTimeSpan = TimeSpan.FromHours(1);
    options.SlidingExpiration = false;
    options.AccessDeniedPath = "/denied";
}).AddOpenIdConnect(options =>
{
    options.Authority = $"https://login.microsoftonline.com/{tenant}/v2.0";
    options.ClientId = Guid.Parse(Required("Entra:ClientId")).ToString();
    options.ClientSecret = Required("Entra:ClientSecret");
    options.ResponseType = "code";
    options.UsePkce = true;
    options.MapInboundClaims = false;
    options.TokenValidationParameters.RoleClaimType = "roles";
    options.TokenValidationParameters.ValidIssuer = options.Authority;
    options.TokenValidationParameters.ValidAudience = options.ClientId;
    options.TokenValidationParameters.ValidateIssuer = true;
    options.TokenValidationParameters.ValidateAudience = true;
    options.AccessDeniedPath = "/denied";
});
builder.Services.AddAuthorization(options =>
    options.DefaultPolicy = options.FallbackPolicy = Readers.Invited(tenant));
builder.Services.AddRazorPages();
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = Library.MaxBytes + 65536);
var app = builder.Build();
// Container Apps terminates TLS. Use the configured public origin for callbacks.
app.Use(async (ctx, next) =>
{
    ctx.Request.Scheme = origin.Scheme;
    ctx.Request.Host = HostString.FromUriComponent(origin);
    ctx.Response.Headers.CacheControl = "no-store";
    await next();
});
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/denied", () => Results.Text("Ask the BookSayer administrator for reader access.", statusCode: 403)).AllowAnonymous();
app.MapGet("/signed-out", () => Results.Text("You have signed out of BookSayer.")).AllowAnonymous();
app.MapRazorPages();
app.MapGet("/audio/{id}", async (string id, HttpContext ctx, Library store, CancellationToken token) =>
{
    var audio = await store.OpenAudio(Readers.Owner(ctx.User), id, token);
    return audio is null ? Results.NotFound() : Results.Stream(audio, "audio/mpeg", "audiobook.mp3", enableRangeProcessing: true);
});
await app.RunAsync();

public partial class Program;

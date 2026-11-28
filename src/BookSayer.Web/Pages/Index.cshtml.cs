using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BookSayer.Web.Pages;

public class IndexModel(Library library) : PageModel
{
    public List<Book> Books { get; private set; } = [];
    public async Task OnGetAsync(CancellationToken token) => Books = await library.List(Readers.Owner(User), token);

    public IActionResult OnPostSignOut() => new SignOutResult(
        [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme],
        new AuthenticationProperties { RedirectUri = "/signed-out" });

    public async Task<IActionResult> OnPostAsync(IFormFile? manuscript, CancellationToken token)
    {
        var name = Path.GetFileName(manuscript?.FileName.Replace('\\', '/')) ?? "";
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (manuscript is null || manuscript.Length is 0 or > Library.MaxBytes || name.Length > 200 ||
            ext is not (".md" or ".markdown" or ".txt"))
            return BadRequest("Choose a Markdown or text file of up to 128 KiB with a filename of up to 200 characters.");

        using var data = new MemoryStream();
        await manuscript.CopyToAsync(data, token);
        var bytes = data.ToArray();
        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
            if (string.IsNullOrWhiteSpace(text) || text.Contains('\0')) return BadRequest("The file must contain text.");
        }
        catch (DecoderFallbackException) { return BadRequest("Save the manuscript as UTF-8 and try again."); }
        if (!await library.Submit(Readers.Owner(User), name, ext, bytes, token))
            return StatusCode(429, "Daily limit reached. Try again after midnight UTC.");
        return RedirectToPage();
    }
}

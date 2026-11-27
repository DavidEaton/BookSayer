using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace BookSayer.Web;

public static class Readers
{
    public static AuthorizationPolicy Invited(string tenant) => new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireClaim("tid", tenant)
        .RequireRole("BookSayer.User")
        .RequireAssertion(ctx => Guid.TryParse(ctx.User.FindFirstValue("oid"), out _))
        .Build();

    public static string Owner(ClaimsPrincipal user) =>
        $"{Guid.Parse(user.FindFirstValue("tid")!):D}/{Guid.Parse(user.FindFirstValue("oid")!):D}/";
}

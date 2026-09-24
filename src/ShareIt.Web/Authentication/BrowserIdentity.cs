using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using ShareIt.Core.DTOs;

namespace ShareIt.Web.Authentication;

public static class BrowserIdentity
{
    public const string CookieScheme = "ShareIt.Cookie";
    public const string BasicScheme = "ShareIt.Basic";
    public static Caller Caller(ClaimsPrincipal user) => new(
        user.FindFirstValue(ClaimTypes.NameIdentifier),
        Guid.TryParse(user.FindFirstValue("read_session"), out var id) ? id : null);
    public static string GetOrCreate(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    public static Task SignInAsync(HttpContext context, string id) => context.SignInAsync(CookieScheme,
        new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], CookieScheme)),
        new AuthenticationProperties { IsPersistent = false, AllowRefresh = true });
}

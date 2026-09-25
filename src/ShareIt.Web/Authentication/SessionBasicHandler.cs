using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using ShareIt.Web.WebDav;

namespace ShareIt.Web.Authentication;

public sealed class SessionBasicHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, CredentialGuard guard)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!WebDavProtocol.IsWebDav(Context) &&
            (!(Request.Path.StartsWithSegments("/api/v1") || TerminalDownloadMetadata.IsEndpoint(Context)) ||
             !HttpMethods.IsGet(Request.Method))) return AuthenticateResult.NoResult();
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();
        string credentials;
        try { credentials = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..])); }
        catch (FormatException) { return AuthenticateResult.Fail("Invalid authorization header."); }
        var separator = credentials.IndexOf(':');
        if (separator < 0 || credentials.Length > 64) return AuthenticateResult.Fail("Invalid authorization header.");
        var id = await guard.VerifyAsync(credentials[..separator], credentials[(separator + 1)..],
            Context.Connection.RemoteIpAddress?.ToString() ?? "unknown", Context.RequestAborted);
        var identity = new ClaimsIdentity([new Claim("read_session", id.ToString())], Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 401;
        Response.Headers.WWWAuthenticate = "Basic realm=\"Share-It\", charset=\"UTF-8\"";
        return Task.CompletedTask;
    }
}

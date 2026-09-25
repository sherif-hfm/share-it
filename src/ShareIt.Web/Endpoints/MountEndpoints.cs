using System.Text;
using ShareIt.Core.DTOs;
using ShareIt.Web.Services;

namespace ShareIt.Web.Endpoints;

public static class MountEndpoints
{
    public static void MapMountEndpoints(this WebApplication app)
    {
        app.MapGet("/{code}/mount.sh", (string code, HttpContext ctx) => Script(code, ctx, null));
        app.MapGet("/{code}/mount.ps1", (string code, HttpContext ctx) => Script(code, ctx, MountPlatform.Windows));
    }

    private static IResult Script(string code, HttpContext context, MountPlatform? platform)
    {
        if (SessionCode.Normalize(code).Length == 0)
            throw new ShareItException("invalid_code", "Enter a valid session code.");
        var request = context.Request;
        var baseUri = $"{request.Scheme}://{request.Host}{request.PathBase.ToUriComponent()}";
        // This is a public client helper. Session data and credentials are never read here.
        return Results.Text(MountCommands.Script(baseUri, code, platform), "text/plain", Encoding.UTF8);
    }
}

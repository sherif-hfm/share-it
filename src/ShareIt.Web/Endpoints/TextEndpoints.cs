using System.Text;
using ShareIt.Core.DTOs;
using ShareIt.Core.Services;
using ShareIt.Web.Authentication;

namespace ShareIt.Web.Endpoints;

public static class TextEndpoints
{
    public static void MapTextEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/sessions/{code}/texts/{number:int}/raw", async (string code, int number, HttpContext ctx, SessionService sessions) =>
        {
            SessionEndpoints.RequireIdentity(ctx);
            var snapshot = await sessions.GetAsync(code, BrowserIdentity.Caller(ctx.User), ctx.RequestAborted);
            return RawText(snapshot, number);
        });
        app.MapGet("/t/{number:int}", async (int number, HttpContext ctx, SessionService sessions) =>
        {
            SessionEndpoints.RequireIdentity(ctx, readerOnly: true);
            var caller = BrowserIdentity.Caller(ctx.User);
            var snapshot = await sessions.GetAsync(caller.ReadSessionId!.Value, caller, ctx.RequestAborted);
            return RawText(snapshot, number);
        }).WithMetadata(new TerminalDownloadMetadata());
    }

    private static IResult RawText(SessionSnapshot snapshot, int number)
    {
        var text = snapshot.Texts.SingleOrDefault(x => x.Number == number)
            ?? throw new ShareItException("not_found", "This text is no longer available.", 404);
        return Results.Text(text.Content, "text/plain", Encoding.UTF8);
    }
}

using Microsoft.AspNetCore.Antiforgery;
using ShareIt.Core.DTOs;
using ShareIt.Core.Services;
using ShareIt.Web.Authentication;

namespace ShareIt.Web.Endpoints;

public static class SessionEndpoints
{
    public sealed record CreateRequest(int Minutes = 60);
    public sealed record JoinRequest(string Code, string Pin);

    public static void MapSessionEndpoints(this WebApplication app)
    {
        app.MapGet("/api/antiforgery", (HttpContext ctx, IAntiforgery anti) =>
            Results.Ok(new { token = anti.GetAndStoreTokens(ctx).RequestToken }));

        app.MapPost("/api/v1/sessions", async (HttpContext ctx, IAntiforgery anti, SessionService sessions, CreateRequest request) =>
        {
            await anti.ValidateRequestAsync(ctx);
            var browserId = BrowserIdentity.GetOrCreate(ctx.User);
            var result = await sessions.CreateAsync(browserId, request.Minutes, ctx.RequestAborted);
            await BrowserIdentity.SignInAsync(ctx, browserId);
            return Results.Ok(result);
        }).RequireRateLimiting("create");

        app.MapPost("/api/v1/sessions/{id:guid}/cancel", async (Guid id, HttpContext ctx, IAntiforgery anti, SessionService sessions) =>
        {
            RequireIdentity(ctx);
            await anti.ValidateRequestAsync(ctx);
            // Use the fresh browser cookie; the home page's circuit can predate sign-in.
            try { await sessions.EndAsync(id, BrowserIdentity.Caller(ctx.User), true, ctx.RequestAborted); }
            catch (ShareItException ex) when (ex.Status == 404) { /* A previous cancellation may already have purged it. */ }
            return Results.Ok(new { cancelled = true });
        });

        app.MapPost("/api/v1/join", async (HttpContext ctx, IAntiforgery anti, CredentialGuard guard, SessionService sessions, JoinRequest request) =>
        {
            await anti.ValidateRequestAsync(ctx);
            var id = await guard.VerifyAsync(request.Code, request.Pin, ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown", ctx.RequestAborted);
            var browserId = BrowserIdentity.GetOrCreate(ctx.User);
            await sessions.JoinVerifiedAsync(id, browserId, ctx.RequestAborted);
            await BrowserIdentity.SignInAsync(ctx, browserId);
            return Results.Ok(new { code = SessionCode.Format(SessionCode.Normalize(request.Code)) });
        });

        app.MapGet("/api/v1/sessions/{code}", async (string code, HttpContext ctx, SessionService sessions) =>
        {
            RequireIdentity(ctx);
            var snapshot = await sessions.GetAsync(code, BrowserIdentity.Caller(ctx.User), ctx.RequestAborted);
            return Results.Ok(new { snapshot.Code, snapshot.ExpiresAtUtc,
                texts = snapshot.Texts.Select(x => new { x.Number, x.Title, x.Language, x.UpdatedAtUtc }), snapshot.Files });
        });
    }

    public static void RequireIdentity(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            context.Response.Headers.WWWAuthenticate = "Basic realm=\"Share-It\", charset=\"UTF-8\"";
            throw new ShareItException("unauthorized", "Enter the session code and PIN to continue.", 401);
        }
    }
}

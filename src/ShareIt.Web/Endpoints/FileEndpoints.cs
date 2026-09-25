using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using ShareIt.Core.Configuration;
using ShareIt.Core.DTOs;
using ShareIt.Core.Services;
using ShareIt.Web.Authentication;

namespace ShareIt.Web.Endpoints;

public static class FileEndpoints
{
    public static void MapFileEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/sessions/{code}/files", async (string code, HttpContext ctx, IAntiforgery anti,
            SessionService sessions, FileService files, ShareItLimits limits) =>
        {
            SessionEndpoints.RequireIdentity(ctx);
            await anti.ValidateRequestAsync(ctx);
            var caller = BrowserIdentity.Caller(ctx.User);
            if (caller.IsReader) throw new ShareItException("forbidden", "Terminal access is read-only.", 403);
            var snapshot = await sessions.GetAsync(code, caller, ctx.RequestAborted);
            if (!long.TryParse(ctx.Request.Headers["X-File-Size"], out var size) || size < 0 || size > limits.MaxFileBytes)
                throw new ShareItException("file_limit", "Choose a file of 25 MB or less.", 413);
            if (!MediaTypeHeaderValue.TryParse(ctx.Request.ContentType, out var type) || !type.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
                throw new ShareItException("content_type", "Send a multipart file upload.");
            var boundary = HeaderUtilities.RemoveQuotes(type.Boundary).Value;
            if (string.IsNullOrEmpty(boundary) || boundary.Length > 128) throw new ShareItException("boundary", "Invalid upload boundary.");
            var reader = new MultipartReader(boundary, ctx.Request.Body) { BodyLengthLimit = limits.MaxFileBytes };
            var section = await reader.ReadNextSectionAsync(ctx.RequestAborted);
            if (section == null || !ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) || !disposition.DispositionType.Equals("form-data"))
                throw new ShareItException("file", "Select a file to upload.");
            var name = HeaderUtilities.RemoveQuotes(disposition.FileNameStar.HasValue ? disposition.FileNameStar : disposition.FileName).Value;
            if (string.IsNullOrEmpty(name)) throw new ShareItException("file", "Select a file to upload.");
            var number = await files.UploadAsync(snapshot.Id, caller, name, size, section.Body, ctx.RequestAborted);
            return Results.Ok(new { number });
        });
        app.MapGet("/api/v1/sessions/{code}/files/{number:int}", async (string code, int number, HttpContext ctx, SessionService sessions, FileService files) =>
        {
            SessionEndpoints.RequireIdentity(ctx);
            var caller = BrowserIdentity.Caller(ctx.User);
            var snapshot = await sessions.GetAsync(code, caller, ctx.RequestAborted);
            return await Download(snapshot.Id, caller, number, files, ctx.RequestAborted);
        });
        app.MapGet("/f/{number:int}", async (int number, HttpContext ctx, FileService files) =>
        {
            SessionEndpoints.RequireIdentity(ctx, readerOnly: true);
            var caller = BrowserIdentity.Caller(ctx.User);
            return await Download(caller.ReadSessionId!.Value, caller, number, files, ctx.RequestAborted);
        }).WithMetadata(new TerminalDownloadMetadata());
    }

    private static async Task<IResult> Download(Guid sessionId, Caller caller, int number, FileService files, CancellationToken ct)
    {
        var result = await files.DownloadAsync(sessionId, caller, number, ct);
        return Results.Stream(result.Stream, "application/octet-stream", result.Name, enableRangeProcessing: false);
    }
}

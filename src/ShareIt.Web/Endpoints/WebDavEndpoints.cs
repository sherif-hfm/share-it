using Microsoft.Net.Http.Headers;
using ShareIt.Core.DTOs;
using ShareIt.Core.Services;
using ShareIt.Web.Authentication;
using ShareIt.Web.WebDav;
using System.Xml.Linq;

namespace ShareIt.Web.Endpoints;

public static class WebDavEndpoints
{
    private static readonly HashSet<string> WriteMethods = new(StringComparer.Ordinal)
        { "PUT", "DELETE", "MKCOL", "COPY", "MOVE", "PROPPATCH", "POST", "PATCH" };

    public static void MapWebDavEndpoints(this WebApplication app)
    {
        // All verbs reach the read-only policy, including writes to not-yet-existing
        // paths. Never bind or consume a mutation request body.
        app.Map("/dav/{code}/{**path}", HandleAsync);
    }

    private static async Task HandleAsync(HttpContext context)
    {
        SessionEndpoints.RequireIdentity(context);
        var caller = BrowserIdentity.Caller(context.User);
        if (!caller.IsReader) throw new ShareItException("access_denied", "Use the session code and PIN.", 403);
        var code = (string)context.Request.RouteValues["code"]!;
        var ct = context.RequestAborted;
        var method = context.Request.Method;
        if (WriteMethods.Contains(method))
        {
            // Scope/lifecycle must still be checked before denying a write.
            await context.RequestServices.GetRequiredService<SessionService>().GetAsync(code, caller, ct);
            throw new ShareItException("read_only", "This WebDAV drive is read-only.", 403);
        }
        var path = WebDavProtocol.ParsePath(context);
        var provider = context.RequestServices.GetRequiredService<WebDavResourceProvider>();
        var listing = await provider.ResolveAsync(code, path.Segments, path.TrailingSlash, caller, ct);
        var resource = listing.Resource;
        var allow = resource.IsCollection ? WebDavProtocol.CollectionMethods : WebDavProtocol.ReadMethods;
        if (HttpMethods.IsOptions(method) || method == "PROPFIND" ||
            (!resource.IsCollection && (HttpMethods.IsGet(method) || HttpMethods.IsHead(method))))
            WebDavIfHeader.Check(context, listing, path);

        if (HttpMethods.IsOptions(method))
        {
            context.Response.Headers.Allow = allow;
            context.Response.Headers["DAV"] = "1";
            context.Response.StatusCode = 200;
            context.Response.ContentLength = 0;
            return;
        }
        if (method == "PROPFIND")
        {
            var depth = context.Request.Headers["Depth"].ToString();
            if (depth.Length == 0) depth = "infinity";
            if (depth is not ("0" or "1" or "infinity")) throw new ShareItException("invalid_depth", "Depth must be 0, 1, or infinity.");
            if (depth == "infinity" && resource.IsCollection)
                throw new ShareItException("propfind-finite-depth", "Use Depth 0 or 1 for collections.", 403);
            var properties = await WebDavProtocol.ReadPropertiesAsync(context);
            var nodes = depth == "0" ? new[] { resource } : new[] { resource }.Concat(listing.Children);
            context.Response.StatusCode = 207;
            context.Response.Headers["DAV"] = "1";
            await WebDavProtocol.XmlAsync(context, new XElement(WebDavProtocol.Dav + "multistatus",
                nodes.Select(x => WebDavProtocol.PropertyResponse(x, properties, path.RootHref))));
            return;
        }
        if (!resource.IsCollection && (HttpMethods.IsGet(method) || HttpMethods.IsHead(method)))
        {
            var stream = await provider.OpenReadAsync(resource, caller, ct);
            // ASP.NET supplies matching HEAD, conditional, and single-range behavior
            // and owns/disposes the stream, including on disconnect or a 304 response.
            await Results.Stream(stream, resource.ContentType,
                lastModified: new DateTimeOffset(DateTime.SpecifyKind(resource.ModifiedAtUtc!.Value, DateTimeKind.Utc)),
                entityTag: new EntityTagHeaderValue(resource.EntityTag!), enableRangeProcessing: true).ExecuteAsync(context);
            return;
        }
        context.Response.Headers.Allow = allow;
        await WebDavProtocol.ErrorAsync(context, 405, "method_not_allowed", "This method is not supported for this resource.");
    }
}

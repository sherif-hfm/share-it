using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http.Features;
using ShareIt.Core.DTOs;

namespace ShareIt.Web.WebDav;

public static class WebDavProtocol
{
    public static readonly XNamespace Dav = "DAV:";
    public const string ReadMethods = "OPTIONS, PROPFIND, GET, HEAD";
    public const string CollectionMethods = "OPTIONS, PROPFIND";
    public const string Challenge = "Basic realm=\"Share-It\", charset=\"UTF-8\"";
    public const int MaxPropertyBodyBytes = 64 * 1024;

    public static bool IsWebDav(HttpContext context) => context.Request.Path.StartsWithSegments("/dav");

    public static async Task ErrorAsync(HttpContext context, int status, string code, string detail)
    {
        if (status == 410) { status = 401; code = "invalid_credentials"; }
        if (status == 401)
        {
            context.Response.Headers.WWWAuthenticate = Challenge;
            detail = "The code or PIN is incorrect, or the session has ended.";
        }
        context.Response.StatusCode = status;
        // Application errors use our namespace; protocol preconditions use DAV:.
        var name = code == "propfind-finite-depth" ? Dav + code : XName.Get(code, "urn:share-it");
        await XmlAsync(context, new XElement(Dav + "error", new XElement(name),
            new XElement(Dav + "responsedescription", detail)));
    }

    public static async Task XmlAsync(HttpContext context, XElement document)
    {
        var bytes = Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting));
        context.Response.ContentType = "application/xml; charset=utf-8";
        context.Response.ContentLength = bytes.Length;
        if (!HttpMethods.IsHead(context.Request.Method)) await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
    }

    public sealed record RequestPath(string RootHref, string[] Segments, bool TrailingSlash);

    public static RequestPath ParsePath(HttpContext context)
    {
        // Decode exactly once from the wire. Request.Path has already decoded some
        // escapes, so decoding a route value again would confuse literal percent names.
        var raw = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
        var hasRawTarget = !string.IsNullOrEmpty(raw);
        var path = hasRawTarget ? raw!.Split('?')[0] : context.Request.PathBase.Add(context.Request.Path).Value!;
        return ParsePath(path, context.Request.PathBase.Value, hasRawTarget);
    }

    public static RequestPath ParsePath(string path, string? pathBase, bool encoded = true)
    {
        var parts = path.Split('/');
        var prefixCount = pathBase?.Split('/', StringSplitOptions.RemoveEmptyEntries).Length ?? 0;
        var firstResource = prefixCount + 3; // leading slash, dav, code
        if (!path.StartsWith('/') || parts.Length < firstResource) throw BadPath();
        var trailingSlash = path.EndsWith('/');
        var encodedSegments = parts.Skip(firstResource).ToArray();
        if (trailingSlash && encodedSegments.Length > 0) encodedSegments = encodedSegments[..^1];
        var decoded = encodedSegments.Select(segment => (encoded ? Uri.UnescapeDataString(segment) : segment).Normalize(NormalizationForm.FormC)).ToArray();
        if (decoded.Any(x => x.Length == 0 || x is "." or ".." || x.Contains('/') || x.Contains('\\') || x.Any(char.IsControl))) throw BadPath();
        return new(string.Join('/', parts.Take(firstResource).Select(x => encoded ? x : Uri.EscapeDataString(x))) + "/", decoded, trailingSlash);
    }

    private static ShareItException BadPath() => new("not_found", "This path is not available.", 404);

    public sealed record PropertyRequest(bool NamesOnly, bool All, IReadOnlyList<XName> Names);

    public static async Task<PropertyRequest> ReadPropertiesAsync(HttpContext context)
    {
        if (context.Request.ContentLength > MaxPropertyBodyBytes) throw new ShareItException("property_limit", "The property request is too large.", 413);
        using var body = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted);
            if (read == 0) break;
            if (body.Length + read > MaxPropertyBodyBytes) throw new ShareItException("property_limit", "The property request is too large.", 413);
            body.Write(buffer, 0, read);
        }
        if (body.Length == 0) return new(false, true, []);
        body.Position = 0;
        try
        {
            using var reader = XmlReader.Create(body, new XmlReaderSettings
            {
                Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = MaxPropertyBodyBytes, IgnoreComments = true
            });
            var document = await XDocument.LoadAsync(reader, LoadOptions.None, context.RequestAborted);
            var root = document.Root;
            if (root?.Name != Dav + "propfind") throw new XmlException();
            var children = root.Elements().ToArray();
            var selectors = children.Where(x => x.Name == Dav + "allprop" || x.Name == Dav + "propname" || x.Name == Dav + "prop").ToArray();
            if (selectors.Length != 1) throw new XmlException();
            var selector = selectors[0];
            var includes = selector.Name == Dav + "allprop" ? children.Where(x => x.Name == Dav + "include").ToArray() : [];
            if (includes.Length > 1) throw new XmlException();
            // RFC 4918 section 17: ignore unexpected elements, including their
            // descendants. Only direct children of prop/include name properties.
            return new(selector.Name == Dav + "propname", selector.Name == Dav + "allprop",
                (selector.Name == Dav + "prop" ? selector.Elements() : includes.SelectMany(x => x.Elements()))
                .Select(x => x.Name).Distinct().ToArray());
        }
        catch (XmlException) { throw new ShareItException("invalid_xml", "Send a valid WebDAV property request."); }
    }

    public static XElement PropertyResponse(WebDavResource resource, PropertyRequest request, string rootHref)
    {
        var values = new Dictionary<XName, XElement>
        {
            [Dav + "displayname"] = new(Dav + "displayname", resource.Name),
            [Dav + "resourcetype"] = new(Dav + "resourcetype", resource.IsCollection ? new XElement(Dav + "collection") : null)
        };
        if (!resource.IsCollection)
        {
            values[Dav + "getcontentlength"] = new(Dav + "getcontentlength", resource.Length!.Value.ToString(CultureInfo.InvariantCulture));
            values[Dav + "getcontenttype"] = new(Dav + "getcontenttype", resource.ContentType);
            values[Dav + "getlastmodified"] = new(Dav + "getlastmodified", resource.ModifiedAtUtc!.Value.ToString("R", CultureInfo.InvariantCulture));
            values[Dav + "getetag"] = new(Dav + "getetag", resource.EntityTag);
        }
        var names = request.All || request.NamesOnly ? values.Keys.Concat(request.Names).Distinct() : request.Names;
        var found = new List<XElement>(); var missing = new List<XElement>();
        foreach (var name in names)
        {
            if (values.TryGetValue(name, out var value)) found.Add(request.NamesOnly ? new XElement(name) : value);
            else missing.Add(new XElement(name));
        }
        var href = rootHref + string.Join('/', resource.Path.Split('/').Select(Uri.EscapeDataString));
        var response = new XElement(Dav + "response", new XElement(Dav + "href", href));
        if (found.Count > 0 || missing.Count == 0) response.Add(Propstat(found, "HTTP/1.1 200 OK"));
        if (missing.Count > 0) response.Add(Propstat(missing, "HTTP/1.1 404 Not Found"));
        return response;
    }

    private static XElement Propstat(IEnumerable<XElement> properties, string status) =>
        new(Dav + "propstat", new XElement(Dav + "prop", properties), new XElement(Dav + "status", status));
}

using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Net.Http.Headers;
using ShareIt.Core.DTOs;

namespace ShareIt.Web.WebDav;

public static class WebDavIfHeader
{
    public static void Check(HttpContext context, WebDavListing listing, WebDavProtocol.RequestPath requestPath)
    {
        if (!context.Request.Headers.TryGetValue("If", out var values)) return;
        if (values.Count != 1) throw Invalid();
        var header = values[0] ?? "";
        if (header.Length > 8192) throw new ShareItException("header_limit", "The If header is too large.", 431);
        // Parse the entire header before evaluating, so a true alternative cannot
        // hide invalid syntax later in the header.
        var lists = new Parser(header).Parse();
        var origin = new Uri(context.Request.GetEncodedUrl());
        var resources = listing.SessionResources.ToDictionary(x => x.Path.TrimEnd('/'), StringComparer.Ordinal);
        string? EntityTag(string? tag)
        {
            if (tag == null) return listing.Resource.EntityTag;
            if (!Uri.TryCreate(origin, tag, out var uri) || uri.Scheme != origin.Scheme ||
                uri.IdnHost != origin.IdnHost || uri.Port != origin.Port || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
                return null;
            try
            {
                var path = WebDavProtocol.ParsePath(uri.AbsolutePath, context.Request.PathBase.Value);
                if (!Uri.UnescapeDataString(path.RootHref).Equals(Uri.UnescapeDataString(requestPath.RootHref), StringComparison.OrdinalIgnoreCase))
                    return null;
                if (resources.TryGetValue(string.Join('/', path.Segments), out var resource) &&
                    (!path.TrailingSlash || resource.IsCollection)) return resource.EntityTag;
            }
            catch (ShareItException) { /* An unmapped URL has no matching state. */ }
            return null;
        }
        var matches = false;
        foreach (var list in lists)
        {
            var etag = EntityTag(list.ResourceTag);
            // No locks/state tokens exist in this read-only implementation.
            // Strong ETag comparison is one of the comparisons allowed by RFC 4918.
            matches |= list.Conditions.All(c => c.Negated != (c.EntityTag != null && c.EntityTag == etag));
        }
        if (!matches) throw new ShareItException("precondition_failed", "The WebDAV If condition was not satisfied.", 412);
    }

    private sealed record Condition(bool Negated, string? EntityTag);
    private sealed record StateList(string? ResourceTag, IReadOnlyList<Condition> Conditions);
    private static ShareItException Invalid() => new("invalid_if", "Send a valid WebDAV If header.");

    private sealed class Parser(string text)
    {
        private int position;
        private char Current => position < text.Length ? text[position] : '\0';
        private void Whitespace() { while (Current is ' ' or '\t') position++; }
        private void Expect(char value) { if (Current != value) throw Invalid(); position++; }

        public IReadOnlyList<StateList> Parse()
        {
            Whitespace();
            var tagged = Current == '<';
            var lists = new List<StateList>();
            while (position < text.Length)
            {
                string? resource = null;
                if (tagged)
                {
                    resource = Angle();
                    if (resource.StartsWith("//") || (!resource.StartsWith('/') && !Uri.TryCreate(resource, UriKind.Absolute, out _))) throw Invalid();
                    Whitespace();
                }
                if (Current != '(') throw Invalid();
                do
                {
                    Expect('('); Whitespace();
                    var conditions = new List<Condition>();
                    while (Current != ')')
                    {
                        var negated = text.AsSpan(position).StartsWith("Not", StringComparison.OrdinalIgnoreCase);
                        if (negated) { position += 3; Whitespace(); }
                        string? etag = null;
                        if (Current == '<')
                        {
                            if (!Uri.TryCreate(Angle(), UriKind.Absolute, out _)) throw Invalid();
                        }
                        else if (Current == '[') etag = ETag();
                        else throw Invalid();
                        conditions.Add(new(negated, etag));
                        Whitespace();
                    }
                    if (conditions.Count == 0) throw Invalid();
                    Expect(')'); Whitespace();
                    lists.Add(new(resource, conditions));
                } while (Current == '(');
                if (!tagged && position < text.Length) throw Invalid();
            }
            if (lists.Count == 0) throw Invalid();
            return lists;
        }

        private string Angle()
        {
            Expect('<');
            var start = position;
            while (Current != '>')
            {
                if (Current is '\0' or '<' or '#' or '\\' || char.IsWhiteSpace(Current) || char.IsControl(Current)) throw Invalid();
                position++;
            }
            if (start == position) throw Invalid();
            var value = text[start..position];
            Expect('>');
            return value;
        }

        private string ETag()
        {
            Expect('[');
            var start = position;
            if (text.AsSpan(position).StartsWith("W/", StringComparison.Ordinal)) position += 2;
            Expect('"');
            while (Current != '"')
            {
                if (Current < '!' || Current == '\u007f' || Current > '\u00ff') throw Invalid();
                position++;
            }
            Expect('"');
            var value = text[start..position];
            Expect(']');
            if (!EntityTagHeaderValue.TryParse(value, out _)) throw Invalid();
            return value;
        }
    }
}

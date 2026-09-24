using System.Globalization;
using System.Text;
using System.Xml;
using ShareIt.Core.DTOs;
using ShareIt.Core.Services;

namespace ShareIt.Web.WebDav;

public enum WebDavResourceKind { Session, Texts, Files, Text, File }

// Protocol-independent identity and metadata. Future mutations must use Number/SessionId
// and an explicitly write-capable caller, not turn a virtual path into a storage key.
public sealed record WebDavResource(Guid SessionId, WebDavResourceKind Kind, int? Number,
    string Path, string Name, long? Length = null, string? ContentType = null,
    DateTime? ModifiedAtUtc = null, string? EntityTag = null, string? Text = null)
{
    public bool IsCollection => Kind is WebDavResourceKind.Session or WebDavResourceKind.Texts or WebDavResourceKind.Files;
}

public sealed record WebDavListing(WebDavResource Resource, IReadOnlyList<WebDavResource> Children,
    IReadOnlyList<WebDavResource> SessionResources);

public sealed class WebDavResourceProvider(SessionService sessions, FileService files)
{
    public async Task<WebDavListing> ResolveAsync(string code, IReadOnlyList<string> segments,
        bool trailingSlash, Caller caller, CancellationToken ct)
    {
        var snapshot = await sessions.GetAsync(code, caller, ct);
        var root = new WebDavResource(snapshot.Id, WebDavResourceKind.Session, null, "", snapshot.Code);
        var texts = new WebDavResource(snapshot.Id, WebDavResourceKind.Texts, null, "texts/", "texts");
        var uploads = new WebDavResource(snapshot.Id, WebDavResourceKind.Files, null, "files/", "files");
        if (segments.Count > 2 || (segments.Count > 0 && segments[0] is not ("texts" or "files"))) throw Missing();

        var textResources = snapshot.Texts.Select(text =>
            {
                var name = WebDavNames.Text(text);
                return new WebDavResource(snapshot.Id, WebDavResourceKind.Text, text.Number, "texts/" + name,
                    name, Encoding.UTF8.GetByteCount(text.Content), "text/plain; charset=utf-8",
                    text.UpdatedAtUtc, $"\"text-{text.Version:N}\"", text.Content);
            }).ToArray();
        var fileResources = snapshot.Files.Select(file =>
            {
                var name = WebDavNames.File(file);
                return new WebDavResource(snapshot.Id, WebDavResourceKind.File, file.Number, "files/" + name,
                    name, file.Size, "application/octet-stream", file.CreatedAtUtc, $"\"sha256-{file.Sha256}\"");
            }).ToArray();
        // Tagged If conditions must see the same snapshot as the requested body,
        // including when they refer to another item in this authorized session.
        WebDavResource[] resources = [root, texts, uploads, .. textResources, .. fileResources];
        if (segments.Count == 0) return new(root, [texts, uploads], resources);
        var children = segments[0] == "texts" ? textResources : fileResources;
        if (segments.Count == 1) return new(segments[0] == "texts" ? texts : uploads, children, resources);
        var resource = children.SingleOrDefault(x => x.Name == segments[1]);
        if (resource == null || trailingSlash) throw Missing();
        return new(resource, [], resources);
    }

    public async Task<Stream> OpenReadAsync(WebDavResource resource, Caller caller, CancellationToken ct)
    {
        if (resource.Kind == WebDavResourceKind.Text)
            return new MemoryStream(Encoding.UTF8.GetBytes(resource.Text!), writable: false);
        if (resource.Kind == WebDavResourceKind.File)
            return (await files.DownloadAsync(resource.SessionId, caller, resource.Number!.Value, ct)).Stream;
        throw new InvalidOperationException("Collections have no file contents.");
    }

    private static ShareItException Missing() => new("not_found", "This item is no longer available.", 404);
}

public static class WebDavNames
{
    public static string Text(TextSnapshot text)
    {
        var extension = text.Language switch
        {
            "bash" => ".sh", "powershell" => ".ps1", "json" => ".json", "yaml" => ".yaml", _ => ".txt"
        };
        var title = Clean(text.Title);
        if (title.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) title = title[..^extension.Length];
        return Build(text.Number, title, extension, "text");
    }

    public static string File(FileSnapshot file)
    {
        var name = Clean(file.Name);
        var dot = name.LastIndexOf('.');
        // Preserve ordinary extensions; very long suffixes are treated as part of the name.
        var extension = dot > 0 && Encoding.UTF8.GetByteCount(name[dot..]) <= 32 ? name[dot..] : "";
        return Build(file.Number, extension.Length == 0 ? name : name[..^extension.Length], extension, "file");
    }

    private static string Clean(string name) => string.Concat(name.EnumerateRunes()
        .Select(r => Rune.IsControl(r) || (r.IsBmp && !XmlConvert.IsXmlChar((char)r.Value)) ||
            (r.IsAscii && "<>:\"/\\|?*".Contains((char)r.Value)) ? "_" : r.ToString()))
        .Normalize(NormalizationForm.FormC).Trim().TrimEnd('.', ' ');

    private static string Build(int number, string name, string extension, string fallback)
    {
        if (string.IsNullOrWhiteSpace(name)) name = fallback;
        var prefix = number.ToString(CultureInfo.InvariantCulture) + "-";
        var budget = 240 - Encoding.UTF8.GetByteCount(prefix + extension);
        var result = new StringBuilder(prefix);
        foreach (var rune in name.EnumerateRunes())
        {
            if (rune.Utf8SequenceLength > budget) break;
            result.Append(rune); budget -= rune.Utf8SequenceLength;
        }
        return result.ToString().TrimEnd('.', ' ') + extension;
    }
}

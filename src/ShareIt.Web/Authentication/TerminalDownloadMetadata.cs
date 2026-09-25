namespace ShareIt.Web.Authentication;

// Only explicitly marked endpoints select a session from Basic credentials.
public sealed class TerminalDownloadMetadata
{
    public static bool IsEndpoint(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<TerminalDownloadMetadata>() != null;
}

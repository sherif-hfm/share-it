using System.Text;
using ShareIt.Core.Configuration;
using ShareIt.Core.DTOs;
using ShareIt.Core.Models;

namespace ShareIt.Core.Policies;

public static class QuotaPolicy
{
    public static void Text(SharedSession s, string content, TextCard? existing, ShareItLimits limits)
    {
        var bytes = Encoding.UTF8.GetByteCount(content);
        if (bytes > limits.MaxTextBytes || s.Texts.Where(x => x != existing).Sum(x => Encoding.UTF8.GetByteCount(x.Content)) + bytes > limits.MaxSessionTextBytes)
            throw new ShareItException("text_limit", "This text exceeds the session's text limit.", 413);
        if (existing == null && s.Texts.Count >= limits.MaxTextCards)
            throw new ShareItException("text_count", "This session has reached its text-card limit.", 413);
    }
    public static void File(SharedSession s, long size, long globalBytes, ShareItLimits limits)
    {
        if (size < 0 || size > limits.MaxFileBytes || s.Files.Count >= limits.MaxFiles || s.Files.Sum(x => x.Size) + size > limits.MaxSessionFileBytes)
            throw new ShareItException("file_limit", "The file or session storage limit has been reached.", 413);
        if (globalBytes + size > limits.GlobalFileBytes)
            throw new ShareItException("storage_full", "Storage is temporarily full. Please try again later.", 503);
    }
}

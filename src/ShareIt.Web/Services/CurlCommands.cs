namespace ShareIt.Web.Services;

public enum TerminalShell { Bash, PowerShell, Cmd }

public static class CurlCommands
{
    public static string Create(string baseUri, string code, int number, string? fileName, TerminalShell shell)
    {
        var url = $"{baseUri.TrimEnd('/')}/api/v1/sessions/{code}/{(fileName == null ? $"texts/{number}/raw" : $"files/{number}")}";
        string Quote(string value) => shell switch
        {
            TerminalShell.Cmd => "\"" + value + "\"",
            TerminalShell.PowerShell => "'" + value.Replace("'", "''") + "'",
            _ => "'" + value.Replace("'", "'\"'\"'") + "'"
        };
        // CMD expands %variables% (and !variables! with delayed expansion) even
        // inside double quotes. Use a predictable local name for unsafe names.
        if (fileName != null && shell == TerminalShell.Cmd && !IsCmdFileName(fileName))
        {
            var extension = Path.GetExtension(fileName);
            if (extension.Length > 16 || !extension.All(c => char.IsAsciiLetterOrDigit(c) || c == '.')) extension = "";
            fileName = $"download-{number}{extension}";
        }
        return $"{(shell == TerminalShell.Bash ? "curl" : "curl.exe")} --fail --user {code} {Quote(url)}"
            + (fileName == null ? "" : " --output " + Quote(fileName));
    }

    private static bool IsCmdFileName(string name)
    {
        if (name.Any(c => char.IsControl(c) || "<>:\"/\\|?*%!".Contains(c)) || name.EndsWith('.') || name.EndsWith(' ')) return false;
        var stem = name.Split('.')[0].ToUpperInvariant();
        return stem is not ("CON" or "PRN" or "AUX" or "NUL") &&
            !(stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsAsciiDigit(stem[3]));
    }
}

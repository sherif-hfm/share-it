using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using ShareIt.Core.Contracts;

namespace ShareIt.Infrastructure.Security;

public sealed class PinHasher : IPinHasher
{
    private readonly byte[] pepper;
    private readonly PasswordHasher<object> hasher = new(Options.Create(new PasswordHasherOptions { IterationCount = 210_000 }));
    private static readonly object User = new();
    public PinHasher(string secretPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(secretPath))!);
        if (!File.Exists(secretPath))
        {
            using var output = new FileStream(secretPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            output.Write(RandomNumberGenerator.GetBytes(32));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(secretPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        pepper = File.ReadAllBytes(secretPath);
        if (pepper.Length < 32) throw new InvalidOperationException("The PIN pepper must contain at least 32 random bytes.");
    }
    private string Prepare(string pin) => Convert.ToBase64String(HMACSHA256.HashData(pepper, Encoding.UTF8.GetBytes(pin)));
    public string Hash(string pin) => hasher.HashPassword(User, Prepare(pin));
    public bool Verify(string pin, string hash)
    {
        if (string.IsNullOrEmpty(hash)) return false;
        try { return hasher.VerifyHashedPassword(User, hash, Prepare(pin)) != PasswordVerificationResult.Failed; }
        catch (FormatException) { return false; }
    }
}

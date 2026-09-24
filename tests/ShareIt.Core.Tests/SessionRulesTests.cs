using ShareIt.Core.Configuration;
using ShareIt.Core.DTOs;
using ShareIt.Core.Models;
using ShareIt.Core.Policies;
using ShareIt.Core.Services;

namespace ShareIt.Core.Tests;

public class SessionRulesTests
{
    [Theory]
    [InlineData("w3r-yub", "w3ryub")]
    [InlineData(" W3R-YUB ", "w3ryub")]
    [InlineData("w3ryub", "w3ryub")]
    [InlineData("../../file", "")]
    public void Codes_normalize_without_allowing_path_characters(string value, string expected) => Assert.Equal(expected, SessionCode.Normalize(value));

    [Theory]
    [InlineData("0047", true)]
    [InlineData("5698", true)]
    [InlineData("１２３４", false)]
    [InlineData("123", false)]
    [InlineData("12345", false)]
    public void Pins_preserve_leading_zeros_and_require_four_ascii_digits(string pin, bool expected) => Assert.Equal(expected, SessionCode.IsPin(pin));

    [Fact]
    public void A_grant_does_not_bypass_expiry_and_curl_cannot_write()
    {
        var now = DateTime.UtcNow;
        var s = new SharedSession { ExpiresAtUtc = now.AddMinutes(1) };
        s.Grants.Add(new BrowserGrant { BrowserId = "browser", SessionId = s.Id });
        SessionAccessPolicy.Require(s, new("browser"), now, true);
        Assert.Equal(403, Assert.Throws<ShareItException>(() => SessionAccessPolicy.Require(s, new(null, s.Id), now, true)).Status);
        Assert.Equal(410, Assert.Throws<ShareItException>(() => SessionAccessPolicy.Require(s, new("browser"), now.AddMinutes(1))).Status);
        s.Status = SessionStatus.Closed;
        Assert.Equal(410, Assert.Throws<ShareItException>(() => SessionAccessPolicy.Require(s, new("browser"), now)).Status);
    }

    [Fact]
    public void Text_quota_counts_utf8_bytes_instead_of_characters()
    {
        var limits = new ShareItLimits { MaxTextBytes = 5 };
        QuotaPolicy.Text(new(), "hello", null, limits);
        Assert.Throws<ShareItException>(() => QuotaPolicy.Text(new(), "😀😀", null, limits));
    }

    [Fact]
    public void File_names_cannot_control_storage_paths()
    {
        Assert.Equal("config.ps1", FileService.CleanName("../../config.ps1"));
        Assert.Equal("config.ps1", FileService.CleanName("C:\\tools\\config.ps1"));
        Assert.Throws<ShareItException>(() => FileService.CleanName(".."));
    }
}

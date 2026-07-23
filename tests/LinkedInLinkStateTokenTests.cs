using Consultologist.Api.Auth;

namespace Consultologist.Api.Tests;

public class LinkedInLinkStateTokenTests
{
    [Fact]
    public void CreateToken_IsBase64UrlOf256Bits()
    {
        var token = LinkedInLinkStateStore.CreateToken();

        // 32 bytes → 43 base64 chars unpadded.
        Assert.Equal(43, token.Length);
        Assert.All(token, c => Assert.True(
            char.IsAsciiLetterOrDigit(c) || c is '-' or '_',
            $"Unexpected character '{c}' in state token."));
    }

    [Fact]
    public void CreateToken_IsUniquePerCall()
    {
        var tokens = Enumerable.Range(0, 100)
            .Select(_ => LinkedInLinkStateStore.CreateToken())
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(100, tokens.Count);
    }
}

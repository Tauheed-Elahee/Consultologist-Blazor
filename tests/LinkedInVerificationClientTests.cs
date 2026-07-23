using System.Text.Json;
using Consultologist.Api.Auth;

namespace Consultologist.Api.Tests;

public class LinkedInVerificationClientTests
{
    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public void ReadCategories_ExtractsVerifications()
    {
        var root = Parse("""{"verifications": ["IDENTITY", "WORKPLACE"], "id": "abc"}""");

        var categories = LinkedInVerificationClient.ReadCategories(root);

        Assert.NotNull(categories);
        Assert.Equal(new[] { "IDENTITY", "WORKPLACE" }, categories);
    }

    [Fact]
    public void ReadCategories_EmptyArray_ReturnsNull()
    {
        var root = Parse("""{"verifications": [], "id": "abc"}""");

        Assert.Null(LinkedInVerificationClient.ReadCategories(root));
    }

    [Fact]
    public void ReadCategories_MissingField_ReturnsNull()
    {
        var root = Parse("""{"id": "abc"}""");

        Assert.Null(LinkedInVerificationClient.ReadCategories(root));
    }

    [Fact]
    public void ReadCategories_IgnoresNonStringEntries()
    {
        var root = Parse("""{"verifications": ["IDENTITY", 42, null, ""]}""");

        var categories = LinkedInVerificationClient.ReadCategories(root);

        Assert.NotNull(categories);
        Assert.Equal(new[] { "IDENTITY" }, categories);
    }
}

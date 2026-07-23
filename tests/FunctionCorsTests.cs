using Microsoft.AspNetCore.Http;

namespace Consultologist.Api.Tests;

public class FunctionCorsTests
{
    private static DefaultHttpContext CreateContext(string? origin)
    {
        var context = new DefaultHttpContext();
        if (origin != null)
        {
            context.Request.Headers.Origin = origin;
        }
        return context;
    }

    [Theory]
    [InlineData("https://app.consultologist.ai")]
    [InlineData("http://localhost:5000")]
    public void Apply_EchoesAllowedOrigin(string origin)
    {
        var context = CreateContext(origin);

        FunctionCors.Apply(context.Request, context.Response);

        Assert.Equal(origin, context.Response.Headers.AccessControlAllowOrigin);
        Assert.Equal("GET, POST, PUT, DELETE, OPTIONS", context.Response.Headers.AccessControlAllowMethods);
        Assert.Equal("Content-Type, Authorization, Last-Event-ID", context.Response.Headers.AccessControlAllowHeaders);
    }

    [Theory]
    [InlineData("https://evil.example.com")]
    [InlineData("http://localhost:9999")]
    [InlineData("")]
    public void Apply_IgnoresDisallowedOrigin(string origin)
    {
        var context = CreateContext(origin);

        FunctionCors.Apply(context.Request, context.Response);

        Assert.Empty(context.Response.Headers);
    }

    [Fact]
    public void Apply_IgnoresRequestWithoutOriginHeader()
    {
        var context = CreateContext(origin: null);

        FunctionCors.Apply(context.Request, context.Response);

        Assert.Empty(context.Response.Headers);
    }

    [Theory]
    [InlineData("https://app.consultologist.ai", true)]
    [InlineData("http://localhost:5173", true)]
    [InlineData("https://evil.example.com", false)]
    [InlineData("https://app.consultologist.ai.evil.example.com", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAllowedOrigin_MatchesExactAllowListEntries(string? origin, bool expected)
    {
        Assert.Equal(expected, FunctionCors.IsAllowedOrigin(origin));
    }
}

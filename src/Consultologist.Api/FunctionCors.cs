using System.Diagnostics.CodeAnalysis;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.AspNetCore.Http;

namespace Consultologist.Api;

internal static class FunctionCors
{
    // Shared by both Apply overloads and by endpoints that need to validate a
    // browser Origin outside of CORS (the LinkedIn link flow derives its
    // redirect-back origin from this list, #133).
    internal static readonly string[] AllowedOrigins =
    {
        "https://app.consultologist.ai",
        "https://gentle-desert-09697700f.3.azurestaticapps.net",
        "http://localhost:3000",
        "http://localhost:5000",
        "http://localhost:5173",
        "http://localhost:5174",
        "http://localhost:7071"
    };

    internal static bool IsAllowedOrigin([NotNullWhen(true)] string? origin)
    {
        return !string.IsNullOrWhiteSpace(origin) && AllowedOrigins.Contains(origin);
    }

    /// <summary>
    /// Open CORS for the anonymous public-registry endpoints (#95): the data is
    /// public and the requests carry no credentials, so any origin — including
    /// the future marketing site — may read.
    /// </summary>
    public static void ApplyPublic(HttpResponseData response)
    {
        response.Headers.Add("Access-Control-Allow-Origin", "*");
        response.Headers.Add("Access-Control-Allow-Methods", "GET, OPTIONS");
        response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
    }

    public static void Apply(HttpRequestData req, HttpResponseData response)
    {
        if (!req.Headers.TryGetValues("Origin", out var originValues))
        {
            return;
        }

        var origin = originValues.FirstOrDefault();

        if (!IsAllowedOrigin(origin))
        {
            return;
        }

        response.Headers.Add("Access-Control-Allow-Origin", origin);
        response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, OPTIONS");
        response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Authorization, Last-Event-ID");
    }

    public static void Apply(HttpRequest req, HttpResponse response)
    {
        if (!req.Headers.TryGetValue("Origin", out var originValues))
        {
            return;
        }

        string? origin = originValues.FirstOrDefault();

        if (!IsAllowedOrigin(origin))
        {
            return;
        }

        response.Headers.AccessControlAllowOrigin = origin;
        response.Headers.AccessControlAllowMethods = "GET, POST, PUT, DELETE, OPTIONS";
        response.Headers.AccessControlAllowHeaders = "Content-Type, Authorization, Last-Event-ID";
    }
}

using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.AspNetCore.Http;

namespace Consultologist.Api;

internal static class FunctionCors
{
    public static void Apply(HttpRequestData req, HttpResponseData response)
    {
        if (!req.Headers.TryGetValues("Origin", out var originValues))
        {
            return;
        }

        var origin = originValues.FirstOrDefault();
        var allowedOrigins = new[]
        {
            "https://app.consultologist.ai",
            "https://gentle-desert-09697700f.3.azurestaticapps.net",
            "http://localhost:3000",
            "http://localhost:5000",
            "http://localhost:5173",
            "http://localhost:5174",
            "http://localhost:7071"
        };

        if (string.IsNullOrWhiteSpace(origin) || !allowedOrigins.Contains(origin))
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

        var origin = originValues.FirstOrDefault();
        var allowedOrigins = new[]
        {
            "https://app.consultologist.ai",
            "https://gentle-desert-09697700f.3.azurestaticapps.net",
            "http://localhost:3000",
            "http://localhost:5000",
            "http://localhost:5173",
            "http://localhost:5174",
            "http://localhost:7071"
        };

        if (string.IsNullOrWhiteSpace(origin) || !allowedOrigins.Contains(origin))
        {
            return;
        }

        response.Headers.AccessControlAllowOrigin = origin;
        response.Headers.AccessControlAllowMethods = "GET, POST, PUT, DELETE, OPTIONS";
        response.Headers.AccessControlAllowHeaders = "Content-Type, Authorization, Last-Event-ID";
    }
}

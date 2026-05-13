using Microsoft.AspNetCore.Http;

namespace Api;

internal static class FunctionCors
{
    public static void Apply(HttpRequest req)
    {
        var origin = req.Headers["Origin"].ToString();
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

        if (!allowedOrigins.Contains(origin))
        {
            return;
        }

        req.HttpContext.Response.Headers.Append("Access-Control-Allow-Origin", origin);
        req.HttpContext.Response.Headers.Append("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
        req.HttpContext.Response.Headers.Append("Access-Control-Allow-Headers", "Content-Type, Authorization");
    }
}

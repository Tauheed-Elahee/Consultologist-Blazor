using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace Consultologist.Api.Probes;

public static class AspNetStrictProbe
{
    [Function("AspNetStrictProbe")]
    public static IActionResult Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "AspNetStrictProbe")] HttpRequest req)
    {
        return new ContentResult
        {
            StatusCode = StatusCodes.Status200OK,
            ContentType = "text/plain; charset=utf-8",
            Content = "ok"
        };
    }
}

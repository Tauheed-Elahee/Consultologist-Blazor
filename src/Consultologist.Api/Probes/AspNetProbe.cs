using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace Consultologist.Api.Probes;

public static class AspNetProbe
{
    [Function("AspNetProbe")]
    public static IActionResult Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "AspNetProbe")] HttpRequest req)
    {
        return new OkObjectResult("ok");
    }
}

using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace Api;

public static class HttpProbe
{
    [Function("HttpProbe")]
    public static HttpResponseData Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", "options", Route = "HttpProbe")] HttpRequestData req)
    {
        return req.CreateResponse(HttpStatusCode.NoContent);
    }

    [Function("HttpStringProbe")]
    public static string RunString(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "HttpStringProbe")] string request)
    {
        return "ok";
    }
}

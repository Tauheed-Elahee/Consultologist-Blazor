using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace Consultologist.Api.Probes;

public static class HttpNoContentStrictProbe
{
    [Function("HttpNoContentStrictProbe")]
    public static HttpResponseData Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "HttpNoContentStrictProbe")] HttpRequestData req)
    {
        var response = req.CreateResponse(HttpStatusCode.NoContent);
        response.Headers.Add("X-Consultologist-Probe", "HttpNoContentStrictProbe");
        return response;
    }
}

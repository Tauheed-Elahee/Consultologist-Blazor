using System.Net;
using Api.Auth;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace Api;

public sealed class Account
{
    private readonly IAccountAuthorizer _authorizer;

    public Account(IAccountAuthorizer authorizer)
    {
        _authorizer = authorizer;
    }

    [Function("AccountMe")]
    public async Task<HttpResponseData> GetMeAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "options", Route = "Account/Me")] HttpRequestData req)
    {
        var cancellationToken = req.FunctionContext.CancellationToken;

        if (string.Equals(req.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            var optionsResponse = req.CreateResponse(HttpStatusCode.OK);
            FunctionCors.Apply(req, optionsResponse);
            return optionsResponse;
        }

        var account = await _authorizer.AuthorizeAsync(req, cancellationToken);

        if (account == null)
        {
            return AccountAuthorizer.CreateUnauthorizedResponse(req);
        }

        if (!AccountAuthorizer.IsActive(account))
        {
            return AccountAuthorizer.CreateForbiddenResponse(req);
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        FunctionCors.Apply(req, response);
        await response.WriteAsJsonAsync(
            new AccountMeResponse(
                account.AppUserId,
                account.DisplayName,
                account.Email,
                account.Status,
                account.CurrentIdentity,
                account.LinkedIdentities),
            cancellationToken);

        return response;
    }
}

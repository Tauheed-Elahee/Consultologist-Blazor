using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker.Http;

namespace Api.Auth;

public interface IAccountAuthorizer
{
    Task<AppAccount?> AuthorizeAsync(HttpRequestData req, CancellationToken cancellationToken);
    Task<AppAccount?> AuthorizeAsync(HttpRequest req, CancellationToken cancellationToken);
}

public sealed class AccountAuthorizer : IAccountAuthorizer
{
    private readonly IBearerTokenValidator _tokenValidator;
    private readonly IAccountStore _accountStore;

    public AccountAuthorizer(IBearerTokenValidator tokenValidator, IAccountStore accountStore)
    {
        _tokenValidator = tokenValidator;
        _accountStore = accountStore;
    }

    public async Task<AppAccount?> AuthorizeAsync(HttpRequestData req, CancellationToken cancellationToken)
    {
        var authorizationHeader = req.Headers.TryGetValues("Authorization", out var values)
            ? values.FirstOrDefault()
            : null;

        return await AuthorizeAsync(authorizationHeader, cancellationToken);
    }

    public async Task<AppAccount?> AuthorizeAsync(HttpRequest req, CancellationToken cancellationToken)
    {
        var authorizationHeader = req.Headers.Authorization.FirstOrDefault();
        return await AuthorizeAsync(authorizationHeader, cancellationToken);
    }

    private async Task<AppAccount?> AuthorizeAsync(string? authorizationHeader, CancellationToken cancellationToken)
    {
        var authenticatedUser = await _tokenValidator.ValidateAsync(authorizationHeader, cancellationToken);

        if (authenticatedUser == null)
        {
            return null;
        }

        return await _accountStore.ResolveOrCreateAsync(authenticatedUser, cancellationToken);
    }

    public static HttpResponseData CreateUnauthorizedResponse(HttpRequestData req)
    {
        var response = req.CreateResponse(HttpStatusCode.Unauthorized);
        FunctionCors.Apply(req, response);
        response.Headers.Add("WWW-Authenticate", "Bearer");
        return response;
    }

    public static HttpResponseData CreateForbiddenResponse(HttpRequestData req)
    {
        var response = req.CreateResponse(HttpStatusCode.Forbidden);
        FunctionCors.Apply(req, response);
        return response;
    }

    public static bool IsActive(AppAccount account)
    {
        return string.Equals(account.Status, AccountStatuses.Active, StringComparison.Ordinal);
    }
}

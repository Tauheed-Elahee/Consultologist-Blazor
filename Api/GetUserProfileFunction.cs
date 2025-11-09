using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using BlazorApp.Api.Helpers;
using BlazorApp.Api.Services;

namespace BlazorApp.Api
{
    public class GetUserProfileFunction
    {
        private readonly ILogger _logger;
        private readonly GraphService _graphService;

        public GetUserProfileFunction(ILoggerFactory loggerFactory, GraphService graphService)
        {
            _logger = loggerFactory.CreateLogger<GetUserProfileFunction>();
            _graphService = graphService;
        }

        [Function("GetUserProfile")]
        public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequestData req)
        {
            _logger.LogInformation("GetUserProfile function triggered.");

            // Check if user is authenticated
            if (!AuthenticationHelper.IsAuthenticated(req))
            {
                _logger.LogWarning("Unauthorized request to GetUserProfile");
                var unauthorizedResponse = req.CreateResponse(HttpStatusCode.Unauthorized);
                await unauthorizedResponse.WriteStringAsync("Unauthorized");
                return unauthorizedResponse;
            }

            try
            {
                // Get user principal from Static Web Apps authentication
                var clientPrincipal = AuthenticationHelper.GetClientPrincipal(req);

                if (clientPrincipal == null || string.IsNullOrEmpty(clientPrincipal.UserId))
                {
                    _logger.LogWarning("Unable to extract user principal from request");
                    var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequestResponse.WriteStringAsync("Unable to determine user identity");
                    return badRequestResponse;
                }

                _logger.LogInformation("Fetching profile for user: {UserId}", clientPrincipal.UserId);

                // Fetch user profile from Microsoft Graph
                var userProfile = await _graphService.GetUserProfileAsync(clientPrincipal.UserId);

                if (userProfile == null)
                {
                    _logger.LogWarning("User profile not found for userId: {UserId}", clientPrincipal.UserId);
                    var notFoundResponse = req.CreateResponse(HttpStatusCode.NotFound);
                    await notFoundResponse.WriteStringAsync("User profile not found");
                    return notFoundResponse;
                }

                // Create response with user profile data
                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    id = userProfile.Id,
                    displayName = userProfile.DisplayName,
                    email = userProfile.Mail ?? userProfile.UserPrincipalName,
                    jobTitle = userProfile.JobTitle,
                    officeLocation = userProfile.OfficeLocation,
                    mobilePhone = userProfile.MobilePhone,
                    businessPhones = userProfile.BusinessPhones
                });

                return response;
            }
            catch (ArgumentNullException ex)
            {
                _logger.LogError(ex, "Configuration error: {Message}", ex.Message);
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new
                {
                    error = "Configuration Error",
                    message = ex.Message,
                    paramName = ex.ParamName,
                    details = "Please configure Azure AD settings in Function App environment variables"
                });
                return errorResponse;
            }
            catch (Azure.Identity.AuthenticationFailedException ex)
            {
                _logger.LogError(ex, "Azure AD authentication failed: {Message}", ex.Message);
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new
                {
                    error = "Authentication Failed",
                    message = "Failed to authenticate with Azure AD",
                    details = ex.Message
                });
                return errorResponse;
            }
            catch (Microsoft.Graph.Models.ODataErrors.ODataError ex)
            {
                _logger.LogError(ex, "Microsoft Graph API error: {Message}", ex.Error?.Message);
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new
                {
                    error = "Graph API Error",
                    message = ex.Error?.Message ?? "Unknown Graph API error",
                    code = ex.Error?.Code
                });
                return errorResponse;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error retrieving user profile: {Message}", ex.Message);
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new
                {
                    error = "Internal Server Error",
                    message = ex.Message,
                    type = ex.GetType().Name,
                    stackTrace = ex.StackTrace
                });
                return errorResponse;
            }
        }
    }
}

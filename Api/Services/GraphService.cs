using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;

namespace BlazorApp.Api.Services
{
    public class GraphService
    {
        private readonly string _tenantId;
        private readonly string _clientId;
        private readonly string _clientSecret;
        private readonly ILogger<GraphService> _logger;

        public GraphService(IConfiguration configuration, ILogger<GraphService> logger)
        {
            _logger = logger;

            _tenantId = configuration["AzureAd_TenantId"];
            _clientId = configuration["AzureAd_ClientId"];
            _clientSecret = configuration["AzureAd_ClientSecret"];

            if (string.IsNullOrEmpty(_tenantId))
            {
                _logger.LogError("AzureAd_TenantId configuration is missing or empty");
                throw new ArgumentNullException("AzureAd_TenantId", "Azure AD Tenant ID is not configured. Please set the AzureAd_TenantId environment variable.");
            }

            if (string.IsNullOrEmpty(_clientId))
            {
                _logger.LogError("AzureAd_ClientId configuration is missing or empty");
                throw new ArgumentNullException("AzureAd_ClientId", "Azure AD Client ID is not configured. Please set the AzureAd_ClientId environment variable.");
            }

            if (string.IsNullOrEmpty(_clientSecret))
            {
                _logger.LogError("AzureAd_ClientSecret configuration is missing or empty");
                throw new ArgumentNullException("AzureAd_ClientSecret", "Azure AD Client Secret is not configured. Please set the AzureAd_ClientSecret environment variable.");
            }

            _logger.LogInformation("GraphService initialized successfully with Tenant ID: {TenantId}", _tenantId);
        }

        public async Task<User?> GetUserProfileAsync(string userId)
        {
            try
            {
                _logger.LogInformation("Fetching user profile for userId: {UserId}", userId);

                // Create credentials for On-Behalf-Of flow
                // Note: For Static Web Apps, we'll use client credentials to query by userId
                var credential = new ClientSecretCredential(_tenantId, _clientId, _clientSecret);

                // Create Graph client
                var graphClient = new GraphServiceClient(credential);

                // Get user profile with limited properties to work with User.ReadBasic.All permission
                // Only request properties that don't require User.Read.All
                var user = await graphClient.Users[userId]
                    .GetAsync(requestConfiguration =>
                    {
                        requestConfiguration.QueryParameters.Select = new[]
                        {
                            "id",
                            "displayName",
                            "mail",
                            "userPrincipalName"
                        };
                    });

                _logger.LogInformation("Successfully retrieved profile for user: {DisplayName}", user?.DisplayName);

                return user;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching user profile for userId: {UserId}", userId);
                throw;
            }
        }
    }
}

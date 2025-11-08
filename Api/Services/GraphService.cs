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
            _tenantId = configuration["AzureAd_TenantId"] ?? throw new ArgumentNullException("AzureAd_TenantId");
            _clientId = configuration["AzureAd_ClientId"] ?? throw new ArgumentNullException("AzureAd_ClientId");
            _clientSecret = configuration["AzureAd_ClientSecret"] ?? throw new ArgumentNullException("AzureAd_ClientSecret");
            _logger = logger;
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

                // Get user profile
                var user = await graphClient.Users[userId].GetAsync();

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

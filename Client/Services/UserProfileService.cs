using System.Net.Http.Json;
using BlazorApp.Client.Models;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;

namespace BlazorApp.Client.Services
{
    public class UserProfileService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IAccessTokenProvider _tokenProvider;
        private UserProfile? _cachedProfile;

        public UserProfileService(IHttpClientFactory httpClientFactory, IAccessTokenProvider tokenProvider)
        {
            _httpClientFactory = httpClientFactory;
            _tokenProvider = tokenProvider;
        }

        public async Task<UserProfile?> GetUserProfileAsync()
        {
            // Return cached profile if available
            if (_cachedProfile != null)
            {
                return _cachedProfile;
            }

            try
            {
                // Request an access token for Microsoft Graph
                var tokenResult = await _tokenProvider.RequestAccessToken(new AccessTokenRequestOptions
                {
                    Scopes = new[] { "User.Read" }
                });

                if (!tokenResult.TryGetToken(out var token))
                {
                    throw new Exception("Failed to acquire access token for Microsoft Graph.");
                }

                // Create HttpClient for Graph API
                var httpClient = _httpClientFactory.CreateClient("GraphAPI");

                // Add the access token to the request
                var request = new HttpRequestMessage(HttpMethod.Get, "me");
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Value);

                var response = await httpClient.SendAsync(request);
                response.EnsureSuccessStatusCode();

                // Parse the Graph API response
                var graphUser = await response.Content.ReadFromJsonAsync<GraphUser>();

                if (graphUser != null)
                {
                    // Map Graph API response to UserProfile
                    _cachedProfile = new UserProfile
                    {
                        DisplayName = graphUser.DisplayName,
                        GivenName = graphUser.GivenName,
                        Surname = graphUser.Surname,
                        UserPrincipalName = graphUser.UserPrincipalName,
                        Mail = graphUser.Mail,
                        JobTitle = graphUser.JobTitle,
                        OfficeLocation = graphUser.OfficeLocation,
                        MobilePhone = graphUser.MobilePhone,
                        BusinessPhones = graphUser.BusinessPhones
                    };

                    return _cachedProfile;
                }

                return null;
            }
            catch (AccessTokenNotAvailableException ex)
            {
                // Redirect to login if token is not available
                ex.Redirect();
                return null;
            }
            catch (HttpRequestException ex)
            {
                // Log error to console
                Console.WriteLine($"Error fetching user profile from Graph API: {ex.Message}");
                throw new Exception("Unable to fetch user profile from Microsoft Graph. Please try logging out and back in.", ex);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Unexpected error: {ex.Message}");
                throw;
            }
        }

        public void ClearCache()
        {
            _cachedProfile = null;
        }
    }

    // Internal class to deserialize Graph API response
    internal class GraphUser
    {
        public string? DisplayName { get; set; }
        public string? GivenName { get; set; }
        public string? Surname { get; set; }
        public string? UserPrincipalName { get; set; }
        public string? Mail { get; set; }
        public string? JobTitle { get; set; }
        public string? OfficeLocation { get; set; }
        public string? MobilePhone { get; set; }
        public string[]? BusinessPhones { get; set; }
    }
}

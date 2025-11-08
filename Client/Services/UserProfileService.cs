using System.Net.Http.Json;
using BlazorApp.Client.Models;

namespace BlazorApp.Client.Services
{
    public class UserProfileService
    {
        private readonly HttpClient _httpClient;
        private UserProfile? _cachedProfile;

        public UserProfileService(HttpClient httpClient)
        {
            _httpClient = httpClient;
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
                // Call the Azure Function endpoint to get user profile
                var profile = await _httpClient.GetFromJsonAsync<UserProfile>("/api/GetUserProfile");

                // Cache the profile for the session
                _cachedProfile = profile;

                return profile;
            }
            catch (HttpRequestException ex)
            {
                // Log error to console
                Console.WriteLine($"Error fetching user profile: {ex.Message}");
                throw new Exception("Unable to fetch user profile. Please try logging out and back in.", ex);
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
}

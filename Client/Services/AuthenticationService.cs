using System.Net.Http.Json;
using BlazorStaticWebApps.Client.Models;

namespace BlazorStaticWebApps.Client.Services
{
    public class AuthenticationService
    {
        private readonly HttpClient _httpClient;

        public AuthenticationService(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public async Task<ClientPrincipal?> GetUserInfoAsync()
        {
            try
            {
                var response = await _httpClient.GetFromJsonAsync<AuthResponse>("/.auth/me");
                return response?.ClientPrincipal;
            }
            catch
            {
                return null;
            }
        }

        public async Task<bool> IsAuthenticatedAsync()
        {
            var user = await GetUserInfoAsync();
            return user != null && !string.IsNullOrEmpty(user.UserId);
        }
    }
}

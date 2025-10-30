using System.Net.Http.Json;
using BlazorStaticWebApps.Client.Models;
using Microsoft.JSInterop;

namespace BlazorStaticWebApps.Client.Services
{
    public class AuthenticationService
    {
        private readonly HttpClient _httpClient;
        private readonly IJSRuntime _jsRuntime;
        private ClientPrincipal? _cachedUser;
        private DateTime _lastCheck = DateTime.MinValue;
        private readonly TimeSpan _cacheTimeout = TimeSpan.FromSeconds(30);
        private System.Timers.Timer? _sessionCheckTimer;

        public event Action? OnAuthenticationStateChanged;

        public AuthenticationService(HttpClient httpClient, IJSRuntime jsRuntime)
        {
            _httpClient = httpClient;
            _jsRuntime = jsRuntime;
            StartSessionMonitoring();
        }

        public async Task<ClientPrincipal?> GetUserInfoAsync(bool forceRefresh = false)
        {
            if (!forceRefresh && _cachedUser != null && DateTime.Now - _lastCheck < _cacheTimeout)
            {
                return _cachedUser;
            }

            try
            {
                var response = await _httpClient.GetFromJsonAsync<AuthResponse>("/.auth/me");
                var previousUser = _cachedUser;
                _cachedUser = response?.ClientPrincipal;
                _lastCheck = DateTime.Now;

                if (HasAuthenticationChanged(previousUser, _cachedUser))
                {
                    NotifyAuthenticationStateChanged();
                    await SetStorageAuthStateAsync(_cachedUser != null);
                }

                return _cachedUser;
            }
            catch
            {
                var previousUser = _cachedUser;
                _cachedUser = null;
                _lastCheck = DateTime.Now;

                if (previousUser != null)
                {
                    NotifyAuthenticationStateChanged();
                    await SetStorageAuthStateAsync(false);
                }

                return null;
            }
        }

        public async Task<bool> IsAuthenticatedAsync()
        {
            var user = await GetUserInfoAsync();
            return user != null && !string.IsNullOrEmpty(user.UserId);
        }

        public ClientPrincipal? GetCachedUser()
        {
            return _cachedUser;
        }

        public async Task RefreshAuthenticationStateAsync()
        {
            await GetUserInfoAsync(forceRefresh: true);
        }

        private void StartSessionMonitoring()
        {
            _sessionCheckTimer = new System.Timers.Timer(60000);
            _sessionCheckTimer.Elapsed += async (sender, e) =>
            {
                await GetUserInfoAsync(forceRefresh: true);
            };
            _sessionCheckTimer.Start();
        }

        private bool HasAuthenticationChanged(ClientPrincipal? previous, ClientPrincipal? current)
        {
            if (previous == null && current == null) return false;
            if (previous == null || current == null) return true;
            return previous.UserId != current.UserId;
        }

        private void NotifyAuthenticationStateChanged()
        {
            OnAuthenticationStateChanged?.Invoke();
        }

        private async Task SetStorageAuthStateAsync(bool isAuthenticated)
        {
            try
            {
                await _jsRuntime.InvokeVoidAsync("localStorage.setItem", "authState", isAuthenticated.ToString());
                await _jsRuntime.InvokeVoidAsync("sessionStorage.setItem", "authStateChanged", DateTime.Now.Ticks.ToString());
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            _sessionCheckTimer?.Stop();
            _sessionCheckTimer?.Dispose();
        }
    }
}

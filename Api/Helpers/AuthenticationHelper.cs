using System.Text;
using System.Text.Json;
using Api.Models;
using Microsoft.Azure.Functions.Worker.Http;

namespace Api.Helpers
{
    public static class AuthenticationHelper
    {
        public static ClientPrincipal? GetClientPrincipal(HttpRequestData req)
        {
            if (!req.Headers.TryGetValues("x-ms-client-principal", out var headerValues))
            {
                return null;
            }

            var header = headerValues.FirstOrDefault();
            if (string.IsNullOrEmpty(header))
            {
                return null;
            }

            try
            {
                var decoded = Convert.FromBase64String(header);
                var json = Encoding.UTF8.GetString(decoded);
                var principal = JsonSerializer.Deserialize<ClientPrincipal>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                return principal;
            }
            catch
            {
                return null;
            }
        }

        public static bool IsAuthenticated(HttpRequestData req)
        {
            var principal = GetClientPrincipal(req);
            return principal != null && !string.IsNullOrEmpty(principal.UserId);
        }
    }
}

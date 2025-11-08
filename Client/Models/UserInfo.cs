using System.Text.Json.Serialization;

namespace BlazorApp.Client.Models
{
    public class AuthResponse
    {
        [JsonPropertyName("clientPrincipal")]
        public ClientPrincipal? ClientPrincipal { get; set; }
    }

    public class ClientPrincipal
    {
        [JsonPropertyName("identityProvider")]
        public string? IdentityProvider { get; set; }

        [JsonPropertyName("userId")]
        public string? UserId { get; set; }

        [JsonPropertyName("userDetails")]
        public string? UserDetails { get; set; }

        [JsonPropertyName("userRoles")]
        public List<string>? UserRoles { get; set; }

        [JsonPropertyName("claims")]
        public List<UserClaim>? Claims { get; set; }

        public string? DisplayName
        {
            get
            {
                if (Claims == null || !Claims.Any())
                    return UserDetails;

                // Try multiple common claim types for display name
                var nameClaim = Claims.FirstOrDefault(c =>
                    c.Type == "name" ||
                    c.Type == "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name" ||
                    c.Type == "preferred_username" ||
                    c.Type == "given_name");

                return nameClaim?.Value ?? UserDetails;
            }
        }
    }

    public class UserClaim
    {
        [JsonPropertyName("typ")]
        public string? Type { get; set; }

        [JsonPropertyName("val")]
        public string? Value { get; set; }
    }
}

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

        public string? DisplayName =>
            Claims?.FirstOrDefault(c => c.Type == "name")?.Value ?? UserDetails;
    }

    public class UserClaim
    {
        [JsonPropertyName("typ")]
        public string? Type { get; set; }

        [JsonPropertyName("val")]
        public string? Value { get; set; }
    }
}

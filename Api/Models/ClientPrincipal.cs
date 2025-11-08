using System.Text.Json.Serialization;

namespace BlazorApp.Api.Models
{
    public class ClientPrincipal
    {
        [JsonPropertyName("identityProvider")]
        public string? IdentityProvider { get; set; }

        [JsonPropertyName("userId")]
        public string? UserId { get; set; }

        [JsonPropertyName("userDetails")]
        public string? UserDetails { get; set; }

        [JsonPropertyName("userRoles")]
        public IEnumerable<string>? UserRoles { get; set; }

        [JsonPropertyName("claims")]
        public IEnumerable<Claim>? Claims { get; set; }
    }

    public class Claim
    {
        [JsonPropertyName("typ")]
        public string? Type { get; set; }

        [JsonPropertyName("val")]
        public string? Value { get; set; }
    }
}

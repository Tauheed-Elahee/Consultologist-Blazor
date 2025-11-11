using System.Text.Json.Serialization;

namespace BlazorApp.Client.Models
{
    public class UserProfile
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("email")]
        public string? Email { get; set; }
    }
}

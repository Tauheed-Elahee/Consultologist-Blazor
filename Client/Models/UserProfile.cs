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

        [JsonPropertyName("jobTitle")]
        public string? JobTitle { get; set; }

        [JsonPropertyName("officeLocation")]
        public string? OfficeLocation { get; set; }

        [JsonPropertyName("mobilePhone")]
        public string? MobilePhone { get; set; }

        [JsonPropertyName("businessPhones")]
        public List<string>? BusinessPhones { get; set; }
    }
}

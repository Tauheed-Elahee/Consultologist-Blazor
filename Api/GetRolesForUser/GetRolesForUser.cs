// File: GetRolesForUser.cs
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

public class GetRolesForUser
{
    [Function("GetRolesForUser")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "getRolesForUser")] HttpRequestData req)
    {
        // 1) Read the SWA principal header
        var principalHeader = req.Headers.TryGetValues("x-ms-client-principal", out var values)
            ? values.FirstOrDefault()
            : null;

        var roles = new List<string>();

        if (!string.IsNullOrEmpty(principalHeader))
        {
            // 2) Decode the principal
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(principalHeader));
            var principal = JsonSerializer.Deserialize<ClientPrincipal>(json);

            // 3) Start with SWA’s default role if signed in
            if (principal?.UserId != null) roles.Add("authenticated");

            // 4) Map Entra ID claims -> app roles (sample mappings)
            //    - App roles claim
            roles.AddRange(principal?.Claims
                .Where(c => c.Type.Equals("roles", StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Value) ?? Enumerable.Empty<string>());

            //    - Group-based mapping (optional)
            //      e.g., map a specific group to a role
            var groupIds = principal?.Claims
                .Where(c => c.Type is "groups" or "http://schemas.microsoft.com/ws/2008/06/identity/claims/groupsid")
                .Select(c => c.Value) ?? Enumerable.Empty<string>();

            if (groupIds.Contains("<AAD-GROUP-ID-FOR-CLINICIANS>"))
                roles.Add("clinician");

            if (groupIds.Contains("<AAD-GROUP-ID-FOR-ADMINS>"))
                roles.Add("admin");
        }

        // 5) Deduplicate and return a flat array
        roles = roles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var res = req.CreateResponse(System.Net.HttpStatusCode.OK);
        await res.WriteAsJsonAsync(roles);
        return res;
    }

    private sealed class ClientPrincipal
    {
        [JsonPropertyName("identityProvider")] public string? IdentityProvider { get; set; }
        [JsonPropertyName("userId")] public string? UserId { get; set; }
        [JsonPropertyName("userDetails")] public string? UserDetails { get; set; }
        [JsonPropertyName("userRoles")] public string[]? UserRoles { get; set; }
        [JsonPropertyName("claims")] public List<ClientClaim> Claims { get; set; } = new();
    }

    private sealed class ClientClaim
    {
        [JsonPropertyName("typ")] public string Type { get; set; } = "";
        [JsonPropertyName("val")] public string Value { get; set; } = "";
    }
}
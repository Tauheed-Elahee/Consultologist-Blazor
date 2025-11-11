using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Authentication.WebAssembly.Msal;
using BlazorApp.Client;
using BlazorApp.Client.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Configure base HttpClient (for non-Graph API calls)
builder.Services.AddScoped(sp => new HttpClient
{
    BaseAddress = new Uri(builder.Configuration["API_Prefix"] ?? builder.HostEnvironment.BaseAddress)
});

// Configure MSAL authentication
builder.Services.AddMsalAuthentication(options =>
{
    builder.Configuration.Bind("AzureAd", options.ProviderOptions.Authentication);

    // Add default scopes for Microsoft Graph
    var graphScopes = builder.Configuration.GetSection("MicrosoftGraph:Scopes").Get<string[]>();
    foreach (var scope in graphScopes ?? new[] { "User.Read" })
    {
        options.ProviderOptions.DefaultAccessTokenScopes.Add(scope);
    }
});

// Configure HttpClient for Microsoft Graph
builder.Services.AddHttpClient("GraphAPI", client =>
{
    var graphBaseUrl = builder.Configuration["MicrosoftGraph:BaseUrl"] ?? "https://graph.microsoft.com/v1.0";
    client.BaseAddress = new Uri(graphBaseUrl);
});

// Register services
builder.Services.AddScoped<UserProfileService>();

await builder.Build().RunAsync();

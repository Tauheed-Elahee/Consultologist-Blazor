using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using BlazorWasm;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddMsalAuthentication(options =>
{
    builder.Configuration.Bind("AzureAd", options.ProviderOptions.Authentication);
    options.ProviderOptions.DefaultAccessTokenScopes
            .Add("https://graph.microsoft.com/User.Read");
});

// Named HttpClient for authenticated Graph API calls
builder.Services.AddHttpClient("GraphAPI", client =>
{
    client.BaseAddress = new Uri("https://graph.microsoft.com/v1.0/");
})
.AddHttpMessageHandler(sp =>
{
    var handler = sp.GetRequiredService<AuthorizationMessageHandler>();
    handler.ConfigureHandler(
        authorizedUrls: new[] { "https://graph.microsoft.com" },
        scopes: new[] { "User.Read" });
    return handler;
});

// Default HttpClient without authentication
builder.Services.AddScoped(sp => new HttpClient());

await builder.Build().RunAsync();

using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using BlazorWasm;
using BlazorWasm.Services.AI;
using BlazorWasm.Services.Templates;
using Microsoft.FluentUI.AspNetCore.Components;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddFluentUIComponents();

builder.Services.AddMsalAuthentication(options =>
{
    builder.Configuration.Bind("AzureAd", options.ProviderOptions.Authentication);
    options.ProviderOptions.DefaultAccessTokenScopes
            .Add("https://graph.microsoft.com/User.Read");
});

builder.Services.AddScoped(sp =>
{
    var authorizationMessageHandler =
        sp.GetRequiredService<AuthorizationMessageHandler>();
    authorizationMessageHandler.InnerHandler = new HttpClientHandler();
    authorizationMessageHandler.ConfigureHandler(
        authorizedUrls: new[] { "https://graph.microsoft.com/v1.0" },
        scopes: new[] { "User.Read" });

    return new HttpClient(authorizationMessageHandler);
});

// Register AI Endpoint Service with separate HttpClient (no Graph auth handler)
builder.Services.AddHttpClient<IAIEndpointService, AIEndpointService>();

// Register template rendering service
builder.Services.AddScoped<ITemplateRenderService, ScribanTemplateRenderService>();

await builder.Build().RunAsync();

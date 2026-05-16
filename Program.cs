using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using BlazorWasm;
using BlazorWasm.Services.Accounts;
using BlazorWasm.Services.AI;
using Microsoft.FluentUI.AspNetCore.Components;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddFluentUIComponents();

builder.Services.AddMsalAuthentication(options =>
{
    builder.Configuration.Bind("AzureAd", options.ProviderOptions.Authentication);
    options.ProviderOptions.LoginMode = "redirect";
});

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

// Register AI Endpoint Service with separate HttpClient (no Graph auth handler)
var agentProxyTimeoutSeconds = builder.Configuration.GetValue<int?>("AzureFunction:TimeoutSeconds") ?? 240;
builder.Services.AddHttpClient<IAIEndpointService, AIEndpointService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(agentProxyTimeoutSeconds);
});

builder.Services.AddHttpClient<IAccountEndpointService, AccountEndpointService>();

await builder.Build().RunAsync();

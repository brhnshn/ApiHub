using System;
using System.Net.Http;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using DockerPanel.Client;
using DockerPanel.Client.Security;
using DockerPanel.Client.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// 1. MudBlazor Servis Kayıtları
builder.Services.AddMudServices();

// 2. JWT API İstek Yetkilendirme Delegating Handler
builder.Services.AddScoped<IAuthTokenStore, BrowserAuthTokenStore>();
builder.Services.AddTransient<JwtAuthorizationHandler>();

// 3. API HttpClient Yapılandırması (Localhost Port 5293 Backend Hedefli)
builder.Services.AddScoped(sp =>
{
    var handler = sp.GetRequiredService<JwtAuthorizationHandler>();
    handler.InnerHandler = new HttpClientHandler();
    
    return new HttpClient(handler) 
    { 
        BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) 
    };
});


// 4. Blazor Kimlik Doğrulama Servislerinin Entegrasyonu
builder.Services.AddAuthorizationCore();
builder.Services.AddScoped<AuthenticationStateProvider, JwtAuthenticationStateProvider>();
builder.Services.AddScoped(sp => (JwtAuthenticationStateProvider)sp.GetRequiredService<AuthenticationStateProvider>());

// 5. Global Uygulama Durum Servisi (AppState)
builder.Services.AddScoped<AppState>();
builder.Services.AddSingleton<DeepLinkService>();
builder.Services.AddSingleton(new PlatformInfo 
{ 
    IsMobileApp = false,
    LocalVersion = "2.0",
    GetServerUrlFunc = () => Task.FromResult(builder.HostEnvironment.BaseAddress),
    SaveServerUrlFunc = (url) => Task.CompletedTask
});

await builder.Build().RunAsync();

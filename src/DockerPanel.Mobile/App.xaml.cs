using System;
using System.Threading.Tasks;
using DockerPanel.Mobile.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

namespace DockerPanel.Mobile;

public partial class App : Application
{
    private readonly IServiceProvider _serviceProvider;
    private readonly MainPage _mainPage;

    public App(IServiceProvider serviceProvider)
    {
        InitializeComponent();
        _serviceProvider = serviceProvider;
        _mainPage = _serviceProvider.GetRequiredService<MainPage>();
        MainPage = _mainPage;

        // Connect MobileLifecycleService to PlatformInfo bridge (Clean Architecture lifecycle wiring)
        var lifecycleService = _serviceProvider.GetService<MobileLifecycleService>();
        var platformInfo = _serviceProvider.GetService<DockerPanel.Client.Services.PlatformInfo>();
        if (lifecycleService != null && platformInfo != null)
        {
            lifecycleService.OnAppStateChanged += isActive =>
            {
                platformInfo.TriggerAppStateChanged(isActive);
            };
        }
    }

    protected override void OnStart()
    {
        base.OnStart();
        TriggerLifecycle(true);
        // CheckForUpdates(); // Delegated to premium glassmorphic Blazor UI for unified modern aesthetics
        // Uygulama açılışında kayıtlı FCM token'ı sunucuya yeniden gönder
        RefreshFcmTokenRegistration();
    }

    protected override void OnSleep()
    {
        base.OnSleep();
        TriggerLifecycle(false);
    }

    protected override void OnResume()
    {
        base.OnResume();
        TriggerLifecycle(true);
    }

    private void TriggerLifecycle(bool isAppActive)
    {
        try
        {
            var lifecycleService = _serviceProvider.GetService<MobileLifecycleService>();
            lifecycleService?.SetAppState(isAppActive);
        }
        catch
        {
            // Fail-safe for startup DI timing.
        }
    }

    private void RefreshFcmTokenRegistration()
    {
        Task.Run(async () =>
        {
            try
            {
                // Kısa bir gecikme: HttpClient DI scope'u hazır olsun
                await Task.Delay(2000);
                using var scope = _serviceProvider.CreateScope();
                var registrationService = scope.ServiceProvider.GetService<DockerPanel.Mobile.Services.PushTokenRegistrationService>();
                if (registrationService != null)
                {
                    await registrationService.RefreshAndRegisterStoredTokenAsync();
                }
            }
            catch
            {
                // Token yenileme hatası uygulama açılışını engellemez.
            }
        });
    }

    private void CheckForUpdates()
    {
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(3000);

                UpdateCheckResult result;
                using (var scope = _serviceProvider.CreateScope())
                {
                    var updateService = scope.ServiceProvider.GetService<AutoUpdateService>();
                    if (updateService == null)
                    {
                        return;
                    }

                    result = await updateService.CheckForUpdatesAsync();
                }

                if (!result.HasUpdate)
                {
                    return;
                }

                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    var accept = await _mainPage.DisplayAlert(
                        "Guncelleme Mevcut",
                        $"ApiHub icin yeni bir guncelleme (v{result.ServerVersion}) hazir.\n\nDegisiklikler:\n{result.Changelog}\n\nSimdi guncellemek istiyor musunuz?",
                        "Guncelle",
                        "Sonra Hatirlat");

                    if (!accept)
                    {
                        return;
                    }

                    using var installScope = _serviceProvider.CreateScope();
                    var installService = installScope.ServiceProvider.GetService<AutoUpdateService>();
                    var success = installService != null && await installService.InstallUpdateAsync(result.ServerVersion);
                    if (!success)
                    {
                        await _mainPage.DisplayAlert("Hata", "Guncelleme indirilirken veya kurulurken bir hata olustu.", "Tamam");
                    }
                });
            }
            catch
            {
                // Update checks must never block app startup.
            }
        });
    }
}

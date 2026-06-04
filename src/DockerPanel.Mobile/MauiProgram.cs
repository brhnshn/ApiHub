using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;
using MudBlazor.Services;
using DockerPanel.Client.Security;
using DockerPanel.Client.Services;
using DockerPanel.Mobile.Security;
using DockerPanel.Mobile.Services;

namespace DockerPanel.Mobile;

public static class MauiProgram
{
#if DEBUG
	private const string DefaultApiServerUrl = "http://10.0.2.2:5293/";
#else
	private const string DefaultApiServerUrl = "https://api.burhansahin.com.tr/";
#endif

	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
			});

		builder.Services.AddMauiBlazorWebView();

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
#endif

		// 1. MudBlazor Servisleri
		builder.Services.AddMudServices();

		// 2. Mobil Özel Servisler
		builder.Services.AddSingleton<MobileLifecycleService>();
		builder.Services.AddSingleton<SecureTokenService>();
		builder.Services.AddSingleton<DeepLinkService>();
		builder.Services.AddScoped<AutoUpdateService>();
		// FCM token'ı API'ye kaydeden servis (Transient: her çağrıda HttpClient scope'u ile uyumlu)
		builder.Services.AddTransient<PushTokenRegistrationService>();

		// 3. Delegating Handler for HTTP Authorization Headers
		builder.Services.AddScoped<IAuthTokenStore, MobileAuthTokenStore>();
		builder.Services.AddTransient<JwtAuthorizationHandler>();

		// 4. Scoped HttpClient (Dynamic API address from Preferences with Android Emulator fallback)
		builder.Services.AddScoped(sp =>
		{
			var handler = sp.GetRequiredService<JwtAuthorizationHandler>();
			handler.InnerHandler = new HttpClientHandler();
			
			return new HttpClient(handler) 
			{ 
				BaseAddress = new Uri(GetConfiguredApiServerUrl()) 
			};
		});

		// 4.5 Platform Info Bridging (Enables Blazor pages to know they run in MAUI and edit settings)
		builder.Services.AddSingleton<PlatformInfo>(sp => new PlatformInfo
		{
			IsMobileApp = true,
			LocalVersion = Microsoft.Maui.ApplicationModel.AppInfo.VersionString,
			GetServerUrlFunc = () => Task.FromResult(GetConfiguredApiServerUrl()),
			SaveServerUrlFunc = (url) => 
			{
				Microsoft.Maui.Storage.Preferences.Default.Set("api_server_url", NormalizeApiServerUrl(url, throwOnInvalid: true));
				return Task.CompletedTask;
			},
			GetFcmTokenFunc = () => sp.GetRequiredService<SecureTokenService>().GetTokenAsync(),
			GetDeviceNameFunc = () => Microsoft.Maui.Devices.DeviceInfo.Current.Name,
			TriggerApkInstallFunc = async (serverVersion) =>
			{
				using var scope = sp.CreateScope();
				var updateService = scope.ServiceProvider.GetRequiredService<AutoUpdateService>();
				return await updateService.InstallUpdateAsync(serverVersion);
			},
			CheckForUpdatesFunc = async () =>
			{
				using var scope = sp.CreateScope();
				var updateService = scope.ServiceProvider.GetRequiredService<AutoUpdateService>();
				var res = await updateService.CheckForUpdatesAsync();
				return (res.HasUpdate, res.ServerVersion, res.Changelog);
			},
			GetCacheSizeFunc = () =>
			{
				try
				{
					var cacheDir = Microsoft.Maui.Storage.FileSystem.CacheDirectory;
					if (!System.IO.Directory.Exists(cacheDir)) return Task.FromResult(0L);
					
					long size = 0;
					var dirInfo = new System.IO.DirectoryInfo(cacheDir);
					foreach (var file in dirInfo.GetFiles("*", System.IO.SearchOption.AllDirectories))
					{
						size += file.Length;
					}
					return Task.FromResult(size);
				}
				catch
				{
					return Task.FromResult(0L);
				}
			},
			ClearCacheFunc = () =>
			{
				try
				{
					var cacheDir = Microsoft.Maui.Storage.FileSystem.CacheDirectory;
					if (System.IO.Directory.Exists(cacheDir))
					{
						var dirInfo = new System.IO.DirectoryInfo(cacheDir);
						foreach (var file in dirInfo.GetFiles())
						{
							try { file.Delete(); } catch { }
						}
						foreach (var dir in dirInfo.GetDirectories())
						{
							try { dir.Delete(true); } catch { }
						}
					}
				}
				catch
				{
				}
				return Task.CompletedTask;
			},
			AuthenticateBiometricFunc = async (title) =>
			{
				try
				{
					var isAvailable = await Plugin.Fingerprint.CrossFingerprint.Current.IsAvailableAsync(true);
					if (!isAvailable) return false;

					var request = new Plugin.Fingerprint.Abstractions.AuthenticationRequestConfiguration("Güvenlik Doğrulaması", title)
					{
						AllowAlternativeAuthentication = false
					};

					var result = await Plugin.Fingerprint.CrossFingerprint.Current.AuthenticateAsync(request);
					return result.Authenticated;
				}
				catch
				{
					return false;
				}
			},
			TriggerHapticFeedbackFunc = () =>
			{
				try
				{
					Microsoft.Maui.Devices.HapticFeedback.Default.Perform(Microsoft.Maui.Devices.HapticFeedbackType.Click);
				}
				catch {}
			}
		});

		// 5. Auth State Provider (SecureStorage based Mobile Provider)
		builder.Services.AddAuthorizationCore();
		builder.Services.AddScoped<MobileJwtAuthenticationStateProvider>();
		builder.Services.AddScoped<JwtAuthenticationStateProvider>(sp => sp.GetRequiredService<MobileJwtAuthenticationStateProvider>());
		builder.Services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<MobileJwtAuthenticationStateProvider>());

		// 6. Shared Client AppState
		builder.Services.AddScoped<AppState>();

		// 7. Pages registration
		builder.Services.AddSingleton<MainPage>();

		return builder.Build();
	}

	private static string GetConfiguredApiServerUrl()
	{
		var storedUrl = Microsoft.Maui.Storage.Preferences.Default.Get("api_server_url", DefaultApiServerUrl);
		return NormalizeApiServerUrl(storedUrl, throwOnInvalid: false);
	}

	private static string NormalizeApiServerUrl(string? serverUrl, bool throwOnInvalid)
	{
		var value = serverUrl?.Trim();
		if (string.IsNullOrWhiteSpace(value))
		{
			if (throwOnInvalid)
			{
				throw new ArgumentException("Sunucu adresi bos olamaz.");
			}

			return DefaultApiServerUrl;
		}

		if (!value.Contains("://", StringComparison.Ordinal))
		{
			value = $"https://{value}";
		}

		if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
			(uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
		{
			var normalized = uri.ToString();
			return normalized.EndsWith("/", StringComparison.Ordinal) ? normalized : $"{normalized}/";
		}

		if (throwOnInvalid)
		{
			throw new ArgumentException("Sunucu adresi http veya https ile baslayan gecerli bir URL olmalidir.");
		}

		Microsoft.Maui.Storage.Preferences.Default.Set("api_server_url", DefaultApiServerUrl);
		return DefaultApiServerUrl;
	}
}

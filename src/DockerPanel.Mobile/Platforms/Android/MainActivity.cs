using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Microsoft.Maui;
using Microsoft.Extensions.DependencyInjection;
using Firebase.Messaging;
using System;
using System.Threading.Tasks;

namespace DockerPanel.Mobile;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[IntentFilter(new[] { Intent.ActionView },
	Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
	DataScheme = "apihub",
	DataHost = "navigate",
	AutoVerify = true)]
[MetaData("android.app.shortcuts", Resource = "@xml/shortcuts")]
public class MainActivity : MauiAppCompatActivity
{
	private const int NotificationPermissionRequestCode = 1001;

	protected override void OnCreate(Bundle? savedInstanceState)
	{
		base.OnCreate(savedInstanceState);
		
		if (Build.VERSION.SdkInt >= BuildVersionCodes.Lollipop)
		{
			Window.SetStatusBarColor(Android.Graphics.Color.ParseColor("#10131a"));
		}

		// Android 13+ (API 33) için bildirim izni runtime'da isteniyor
		RequestNotificationPermission();

		// Bildirim Kanalını Sistemde Oluştur (Arka plan bildirimlerinin düşmesi için kritik!)
		CreateNotificationChannel();

		// Cihazın Gerçek FCM Token'ını Başlangıçta Çek ve Kaydet
		FetchAndSaveFcmToken();

		HandleIntent(Intent);
	}

	protected override void OnNewIntent(Intent? intent)
	{
		base.OnNewIntent(intent);
		HandleIntent(intent);
	}

	private void RequestNotificationPermission()
	{
		if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu) // API 33 = Android 13
		{
			if (CheckSelfPermission(Android.Manifest.Permission.PostNotifications) != Permission.Granted)
			{
				RequestPermissions(new[] { Android.Manifest.Permission.PostNotifications }, NotificationPermissionRequestCode);
			}
		}
	}

	private void CreateNotificationChannel()
	{
		if (Build.VERSION.SdkInt >= BuildVersionCodes.O) // API 26 = Android 8.0
		{
			var channelId = "apihub_notifications";
			var channelName = "ApiHub Alerts";
			var channelDescription = "Servis durum uyarıları ve sistem bildirimleri";
			
			var notificationManager = GetSystemService(NotificationService) as NotificationManager;
			if (notificationManager != null)
			{
				var channel = new NotificationChannel(channelId, channelName, NotificationImportance.High)
				{
					Description = channelDescription
				};
				notificationManager.CreateNotificationChannel(channel);
			}
		}
	}

	private void FetchAndSaveFcmToken()
	{
		try
		{
			FirebaseMessaging.Instance.GetToken().AddOnCompleteListener(new OnCompleteListener(task =>
			{
				if (task.IsSuccessful)
				{
					var token = task.Result?.ToString();
					if (!string.IsNullOrEmpty(token))
					{
						System.Diagnostics.Debug.WriteLine($"[FCM Startup] Başarıyla gerçek token çekildi: {token}");
						
						// Güvenli depolama servisini bul ve kaydet
						var tokenService = IPlatformApplication.Current?.Services.GetService<DockerPanel.Mobile.Services.SecureTokenService>();
						if (tokenService != null)
						{
							_ = tokenService.SaveTokenAsync(token);
						}
					}
				}
				else
				{
					System.Diagnostics.Debug.WriteLine($"[FCM Startup] Token çekme başarısız: {task.Exception?.Message}");
				}
			}));
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"[FCM Startup] Hata: {ex.Message}");
		}
	}

	// Xamarin Task Callback'lerini C# Action ile birleştiren yardımcı sınıf
	private class OnCompleteListener : Java.Lang.Object, Android.Gms.Tasks.IOnCompleteListener
	{
		private readonly Action<Android.Gms.Tasks.Task> _callback;

		public OnCompleteListener(Action<Android.Gms.Tasks.Task> callback)
		{
			_callback = callback;
		}

		public void OnComplete(Android.Gms.Tasks.Task task)
		{
			_callback(task);
		}
	}

	private void HandleIntent(Intent? intent)
	{
		var data = intent?.DataString;
		if (!string.IsNullOrEmpty(data))
		{
			try
			{
				var deepLinkService = IPlatformApplication.Current?.Services.GetService<DockerPanel.Client.Services.DeepLinkService>();
				deepLinkService?.HandleDeepLink(data);
			}
			catch
			{
				// Fail-safe for startup timing
			}
		}
	}
}

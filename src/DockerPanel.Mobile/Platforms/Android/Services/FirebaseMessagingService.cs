using System;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Firebase.Messaging;
using Microsoft.Maui;
using Microsoft.Extensions.DependencyInjection;

namespace DockerPanel.Mobile.Platforms.Android.Services;

[Service(Exported = true)]
[IntentFilter(new[] { "com.google.firebase.MESSAGING_EVENT" })]
public class FirebaseMessagingService : Firebase.Messaging.FirebaseMessagingService
{
	public override void OnNewToken(string token)
	{
		base.OnNewToken(token);
		try
		{
			// 1. Token'ı yerel şifreli depolamaya kaydet
			var tokenService = IPlatformApplication.Current?.Services.GetService<DockerPanel.Mobile.Services.SecureTokenService>();
			if (tokenService != null)
			{
				_ = tokenService.SaveTokenAsync(token);
			}

			// 2. Token'ı sunucuya (API) kaydet — ana uçurum burası!
			var registrationService = IPlatformApplication.Current?.Services.GetService<DockerPanel.Mobile.Services.PushTokenRegistrationService>();
			if (registrationService != null)
			{
				_ = registrationService.RegisterTokenWithServerAsync(token);
			}
		}
		catch
		{
			// Fail-safe
		}
	}

	public override void OnMessageReceived(RemoteMessage message)
	{
		base.OnMessageReceived(message);

		// Önce data payload'dan başlık/gövde almayı dene (FCM v1'de notification payload)
		var title = message.GetNotification()?.Title;
		var body = message.GetNotification()?.Body;

		// Notification payload yoksa data payload'a bak
		if (string.IsNullOrEmpty(title) && message.Data != null)
		{
			message.Data.TryGetValue("title", out title);
		}
		if (string.IsNullOrEmpty(body) && message.Data != null)
		{
			message.Data.TryGetValue("body", out body);
		}

		if (!string.IsNullOrEmpty(title) || !string.IsNullOrEmpty(body))
		{
			SendLocalNotification(title ?? "ApiHub Uyarısı", body ?? string.Empty);
		}
	}

	private void SendLocalNotification(string title, string body)
	{
		var intent = new Intent(this, typeof(MainActivity));
		intent.AddFlags(ActivityFlags.ClearTop);
		var pendingIntent = PendingIntent.GetActivity(this, 0, intent, PendingIntentFlags.OneShot | PendingIntentFlags.Immutable);
		var notificationManager = GetSystemService(Context.NotificationService) as NotificationManager;
		if (notificationManager == null)
		{
			return;
		}

		var channelId = "apihub_notifications";

		var notificationBuilder = new AndroidX.Core.App.NotificationCompat.Builder(this, channelId);
		notificationBuilder.SetSmallIcon(Resource.Mipmap.appicon);
		notificationBuilder.SetContentTitle(title);
		notificationBuilder.SetContentText(body);
		notificationBuilder.SetAutoCancel(true);
		notificationBuilder.SetPriority(AndroidX.Core.App.NotificationCompat.PriorityHigh);
		if (pendingIntent != null)
		{
			notificationBuilder.SetContentIntent(pendingIntent);
		}

		if (OperatingSystem.IsAndroidVersionAtLeast(26))
		{
			var channel = new NotificationChannel(channelId, "ApiHub Alerts", NotificationImportance.High);
			channel.Description = "Servis durum uyarıları ve sistem bildirimleri";
			notificationManager.CreateNotificationChannel(channel);
		}

		var notification = notificationBuilder.Build();
		if (notification != null)
		{
			notificationManager.Notify(Random.Shared.Next(1000, 9999), notification);
		}
	}
}

using System;
using Android.App;
using Android.Runtime;
using Firebase;

namespace DockerPanel.Mobile;

[Application]
public class MainApplication : MauiApplication
{
	public MainApplication(IntPtr handle, JniHandleOwnership ownership)
		: base(handle, ownership)
	{
	}

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

	public override void OnCreate()
	{
		base.OnCreate();
		// Firebase'i uygulama seviyesinde başlat (google-services.json'dan otomatik okur)
		FirebaseApp.InitializeApp(this);
	}
}

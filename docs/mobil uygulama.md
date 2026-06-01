# ApiHub — Mobil Uygulama & Ek Özellikler Planı

> **Tarih:** 28 Mayıs 2026  
> **Platform:** .NET MAUI Blazor Hybrid (Android)  
> **Kullanım:** Kişisel hobi projesi — tek kullanıcı  
> **Durum:** Taslak — Onay Bekliyor

---

## Kapsam Özeti

| Özellik | Durum |
|:---|:---:|
| 📋 Audit Log (Denetim Kaydı) | ✅ **Tamamlandı** (Web/API/DB) |
| 💾 Backup & Restore | ✅ **Tamamlandı** (Web/API/VDS SSH) |
| 📱 Mobil Uygulama (.NET MAUI) | ✅ **Temel Yapı Tamamlandı** |
| 🔔 FCM Push Bildirimler | 🔄 **Geliştiriliyor** |
| 📦 APK Dağıtım & Otomatik Güncelleme | 🔄 **Geliştiriliyor** |
| 🔗 App Shortcuts & Deep Linking | 🔄 **Geliştiriliyor** |
| ~~📊 Status Page~~ | ❌ İptal |
| ~~🚨 Incident Management~~ | ❌ İptal |
| ~~📡 Webhook / Çoklu Bildirim~~ | ❌ İptal |
| ~~🔧 Planlı Bakım Bildirimleri~~ | ❌ İptal |
| ~~👥 Multi-Tenant~~ | ❌ İptal |

---

# BÖLÜM 1: MOBİL UYGULAMA

---

## 1. Neden .NET MAUI Blazor Hybrid?

Mevcut projemiz tamamen **.NET 8 Clean Architecture** ve **Blazor WASM + MudBlazor** üzerine kurulu. MAUI Blazor Hybrid bu ekosistemin altın standardıdır:

| Avantaj | Açıklama |
|:---|:---|
| **%100 Kod Paylaşımı** | Web panelindeki tüm Razor sayfaları (Containers, Databases, Domains, Email, Webmail, Terminal, Firewall, DeployWizard) **tek satır değiştirmeden** mobil uygulamaya gömülür |
| **Tek Dil** | C# ile hem backend, hem frontend, hem mobil — sıfır context switch |
| **Native API Erişimi** | Android kamera, bildirimler, dosya sistemi, SecureStorage — doğrudan C# ile |
| **Performans** | WebView değil, native .NET runtime — düşük bellek, hızlı açılış |
| **Batarya Dostu** | SignalR yaşam döngüsü yönetimi ile arka planda sıfır tüketim |

---

## 2. Proje Yapısı ve Dosya Haritası

Solution'a eklenecek yeni MAUI projesi:

```
src/
└── DockerPanel.Mobile/                     ← [NEW] .NET MAUI Blazor Hybrid
    │
    ├── DockerPanel.Mobile.csproj           ← Proje dosyası (.NET 8, Android target)
    ├── MauiProgram.cs                      ← DI container, servis kaydı, MudBlazor init
    ├── App.xaml                            ← Uygulama kaynakları ve tema
    ├── App.xaml.cs                         ← Yaşam döngüsü (OnSleep/OnResume)
    ├── MainPage.xaml                       ← BlazorWebView host sayfası
    │
    ├── Platforms/
    │   └── Android/
    │       ├── MainActivity.cs             ← Deep Link intent yakalama
    │       ├── MainApplication.cs          ← Android Application sınıfı
    │       ├── AndroidManifest.xml         ← İzinler, intent-filter, shortcuts
    │       ├── Resources/
    │       │   ├── xml/
    │       │   │   └── shortcuts.xml       ← App Shortcuts tanımları
    │       │   ├── drawable/               ← Uygulama ikonu ve bildirim ikonu
    │       │   └── values/
    │       │       └── colors.xml          ← Android native renk paleti
    │       └── Services/
    │           └── FirebaseMessagingService.cs  ← FCM arka plan mesaj servisi
    │
    ├── Services/
    │   ├── MobileLifecycleService.cs       ← OnSleep → SignalR kapat, OnResume → aç
    │   ├── DeepLinkService.cs              ← Intent URL → Blazor NavigateTo
    │   ├── PushNotificationService.cs      ← FCM token kayıt ve yenileme
    │   ├── AutoUpdateService.cs            ← Versiyon kontrol ve APK kurulum
    │   └── SecureTokenService.cs           ← JWT token'ı SecureStorage'da şifreli saklama
    │
    ├── wwwroot/
    │   ├── css/
    │   │   └── mobile-overrides.css        ← Mobil ekrana özel CSS düzeltmeleri
    │   └── index.html                      ← BlazorWebView giriş sayfası
    │
    └── Resources/
        ├── AppIcon/                        ← Uygulama ikonu (SVG → tüm çözünürlükler)
        ├── Splash/                         ← Açılış ekranı
        └── Raw/                            ← google-services.json (Firebase config)
```

---

## 3. Mimari Akış Diyagramı

```
┌─────────────────────────────────────────────────────────┐
│                    SENİN TELEFONUN                       │
│                                                          │
│  ┌──────────────────────────────────────────────────┐   │
│  │          DockerPanel.Mobile (MAUI)                │   │
│  │                                                    │   │
│  │  ┌──────────────┐    ┌──────────────────────┐    │   │
│  │  │ BlazorWebView │◄──►│ DockerPanel.Client    │    │   │
│  │  │ (Native Host) │    │ (Paylaşılan Razor     │    │   │
│  │  └──────────────┘    │  Sayfaları & CSS)      │    │   │
│  │                       └──────────────────────┘    │   │
│  │                                                    │   │
│  │  ┌────────────────────────────────────────────┐  │   │
│  │  │           Mobil Servisler                    │  │   │
│  │  │  SecureTokenService  ← JWT şifreli saklama  │  │   │
│  │  │  LifecycleService    ← SignalR yönetimi     │  │   │
│  │  │  DeepLinkService     ← Intent yönlendirme   │  │   │
│  │  │  AutoUpdateService   ← APK güncelleme       │  │   │
│  │  │  PushService         ← FCM token kayıt      │  │   │
│  │  └────────────────────────────────────────────┘  │   │
│  └──────────────────────────────────────────────────┘   │
│                          │                                │
│                          │ HTTPS + SignalR                │
│                          ▼                                │
└─────────────────────────────────────────────────────────┘
                           │
                           ▼
┌─────────────────────────────────────────────────────────┐
│                   SUNUCU (ApiHub API)                    │
│                                                          │
│  ┌───────────┐  ┌──────────────┐  ┌────────────────┐  │
│  │ Controllers│  │ MetricLogHub │  │ FCM Watchdog   │  │
│  │ (REST API) │  │ (SignalR)    │  │ (FirebaseAdmin)│  │
│  └───────────┘  └──────────────┘  └────────────────┘  │
│         │              │                  │             │
│         ▼              ▼                  ▼             │
│  ┌─────────────────────────────────────────────────┐   │
│  │              PostgreSQL (dockerpanel_db)          │   │
│  │  Users | Projects | DeviceTokens | AuditLogs     │   │
│  └─────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────┘
```

---

## 4. Batarya ve Arka Plan Stratejisi

### A. Hibrit İzleme Modu

```
📱 Uygulama ÖN PLANDA (Açık):
─────────────────────────────────────────────────────
[Mobil] ──SignalR Bağlantısı──► [ApiHub API]
         (Canlı CPU/RAM grafikleri + Log akışı)
         (Batarya tüketimi: NORMAL — zaten ekrana bakıyorsun)

═══════════════════════════════════════════════════

😴 Uygulama ARKA PLANDA (Kapalı/Kilitli):
─────────────────────────────────────────────────────
[Mobil] (SignalR KAPALI — 0% batarya tüketimi)

[ApiHub Watchdog] ─── Çökme Algıladı ───► [Firebase Cloud Messaging]
                                                    │
                                                    ▼
                                            [Android OS Push]
                                                    │
                                                    ▼
                                          📲 Ekran Bildirimi!
                                      "QR Menü servisi çöktü!"
```

### B. Yaşam Döngüsü Kancaları

| Olay | Ne Olur | Teknik Detay |
|:---|:---|:---|
| **OnSleep** | SignalR kapatılır | `HubConnection.StopAsync()` → soket kapanır, 0 ağ trafiği |
| **OnResume** | SignalR yeniden bağlanır | `HubConnection.StartAsync()` → 3 saniyede canlı akış geri döner |
| **OnDestroying** | Kaynaklar temizlenir | `HubConnection.DisposeAsync()` + FCM token geçerli kalır |

---

## 5. Firebase Cloud Messaging (FCM)

### A. Ne İçin?

Telefonun cebinde. Uygulama kapalı. Sunucundaki bir konteyner çöktü — haberin yok. FCM sayesinde Android seni anında uyarır, uygulamayı bile açmana gerek kalmaz.

### B. Cihaz Token — Veritabanı Tablosu

**[NEW] `DeviceToken.cs` → Domain/Entities/**

| Alan | Tip | Açıklama |
|:---|:---|:---|
| `Id` | Guid, PK | Benzersiz kayıt |
| `UserId` | Guid, FK → Users.Id | Senin hesabın |
| `Token` | VARCHAR(512), Unique | Firebase'in verdiği cihaz token |
| `Platform` | VARCHAR(20) | `Android` |
| `DeviceName` | VARCHAR(100) | "Samsung Galaxy S24" gibi |
| `CreatedAt` | DateTimeOffset | İlk kayıt |
| `LastUsedAt` | DateTimeOffset | Son başarılı push |

### C. API Endpoint'leri

| Metot | Endpoint | Ne Yapar |
|:---|:---|:---|
| `POST` | `api/devices/register` | Uygulama açılışında token'ı sunucuya kaydeder |
| `DELETE` | `api/devices/unregister/{token}` | Çıkış yapınca token silinir |

### D. Bildirim Akışı

```
MetricBackgroundWorker her 3 saniyede kontrol ediyor
         │
         ▼
   Proje durumu: Running → Error/Stopped geçişi algılandı!
         │
         ▼
   FirebaseAdmin SDK tetikleniyor
         │
         ▼
   Senin tüm kayıtlı cihazlarına push gönderiliyor
   (Priority = High → ekran kapalı olsa bile bildirim gelir)
         │
         ▼
   📲 Telefonunda bildirim kartı beliriyor:
   ┌─────────────────────────────────────┐
   │ 🔴 ApiHub                           │
   │ "qrmenu-app" servisi durdu!         │
   │ Dokunarak kontrol et →              │
   └─────────────────────────────────────┘
         │
         ▼ (Bildirime dokundun)
         │
   Deep Link: apihub://navigate?path=/containers&projectId=xxx
         │
         ▼
   Uygulama açılır → doğrudan o projenin sayfasına gider
```

### E. Firebase Kurulum Adımları

1. **Firebase Console** → Yeni proje oluştur ("ApiHub")
2. **Android uygulaması ekle** → Package name: `com.burhansahin.apihub`
3. **`google-services.json`** indir → `Platforms/Android/Resources/Raw/` altına koy
4. **Service Account JSON** oluştur → Backend'in `appsettings.json`'ına ekle
5. **NuGet:** Mobil'e `Plugin.Firebase.CloudMessaging`, Backend'e `FirebaseAdmin`

---

## 6. APK Dağıtım ve Otomatik Güncelleme

### A. Ne İçin?

Play Store'a koymana gerek yok — bu senin kişisel panelin. APK'yı doğrudan web panelinden indirirsin veya QR kod ile telefonuna atarsın. Güncelleme geldiğinde uygulama seni uyarır.

### B. Panel Üzerinden İndirme

```
┌──────────────────────────────────────────┐
│  WEB PANELİ — Sağ üst köşe              │
│                                           │
│  ┌─────────────────────────────────────┐ │
│  │  📱 Android Uygulaması              │ │
│  │                                      │ │
│  │  ┌────────┐   ┌──────────────────┐  │ │
│  │  │        │   │  "APK İndir" 📥  │  │ │
│  │  │ QR Kod │   │                  │  │ │
│  │  │        │   │  v1.0.4 · 24 MB  │  │ │
│  │  └────────┘   └──────────────────┘  │ │
│  │  Telefonla tara                      │ │
│  └─────────────────────────────────────┘ │
└──────────────────────────────────────────┘
```

### C. API Endpoint'leri

| Metot | Endpoint | Ne Yapar |
|:---|:---|:---|
| `GET` | `api/downloads/apk` | APK dosyasını indirir (JWT korumalı) |
| `GET` | `api/downloads/version` | Güncel versiyon + changelog döner |
| `GET` | `api/downloads/qr-token` | QR kod için 15dk'lık tek kullanımlık token üretir |
| `GET` | `api/downloads/apk/{token}` | Token ile indirme (QR'dan gelen istek) |

### D. Otomatik Güncelleme Akışı

```
Uygulama açıldı
     │
     ▼
GET api/downloads/version
     │
     ▼
Sunucu: v1.0.5  vs  Telefon: v1.0.4
     │
     ▼ (Yeni sürüm var!)
     │
┌──────────────────────────────────┐
│  🔄 Güncelleme Mevcut!           │
│                                   │
│  v1.0.5 — Değişiklikler:         │
│  • Yeni konteyner metrikleri     │
│  • Bildirim iyileştirmeleri      │
│                                   │
│  [Güncelle]    [Sonra Hatırlat]  │
└──────────────────────────────────┘
     │
     ▼ ("Güncelle" dokundun)
     │
APK arka planda indirilir
     │
     ▼
Android PackageInstaller açılır → kurulum başlar
     │
     ▼
✅ Uygulama güncellendi!
```

---

## 7. App Shortcuts ve Deep Linking

### A. Ne İçin?

Telefonda uygulama ikonuna basılı tutunca **hızlı kısayollar** açılır — tek dokunuşla istediğin ekrana gidersin. Bildirime dokunduğunda da doğru sayfada açılırsın.

### B. Kısayollar

| Kısayol | İkon | Hedef | Ne Yapar |
|:---|:---:|:---|:---|
| **Konteynerler** | 🐳 | `/containers` | Doğrudan proje listesine git |
| **Canlı Terminal** | 💻 | `/terminal` | Log akışını anında gör |
| **Veritabanları** | 🗄️ | `/databases` | DB yönetim ekranına atla |

### C. Deep Link Şeması

```
apihub://navigate?path=/containers
apihub://navigate?path=/containers&projectId=UUID
apihub://navigate?path=/terminal
apihub://navigate?path=/databases
```

**Akış:**
1. Bildirime veya kısayola dokunursun
2. Android uygulamayı `apihub://navigate?path=/containers` ile başlatır
3. `MainActivity.cs` → `OnNewIntent` → URL'yi yakalar
4. `DeepLinkService` → Blazor `NavigationManager.NavigateTo("/containers")`
5. Doğru sayfada açılırsın — sıfır gecikme

---

## 8. Mobil UI Optimizasyonları

### A. Ne İçin?

Web paneli geniş ekran için tasarlandı. Telefon ekranında tablolar taşar, menü sığmaz. Mobil'e özel düzenleme gerekiyor.

### B. Tek Tip Ortak Layout (Single Unified Layout) Mimarisi

Tasarım farklılığı ve uyumsuzlukları önlemek amacıyla mobil uygulamadaki tüm Razor sayfalarında **tek tip ortak layout** (`MainLayout.razor`) kullanılır.

1. **Merkezi Şablon Kontrolü (`MainLayout.razor`):**
   - Mobil üst bar (Header), premium cam efekti alt navigasyon çubuğu (Bottom Navigation Bar), Home Indicator ve 12 sayfalık Bento Sheet menü çekmecesi (More Menu / Bento Grid Drawer) tamamen merkezi `MainLayout.razor` içerisinde konumlandırılmıştır.
   - Sayfa geçişleri `NavigateToMobilePage(url)` üzerinden merkezi olarak yönetilir.

2. **Sayfa Düzeyinde Sadeleşme (Redundancy Clean-up):**
   - Sayfalar (`Home.razor`, `Containers.razor` vb.) kendi içlerinde mükerrer bottom navigation, home indicator ve bento drawer overlay şablonları **barındırmaz**.
   - Sayfalar sadece kendilerine has kaydırılabilir gövde içeriklerini, arama/filtreleme ve işlem aksiyon butonlarını inline olarak barındırır.
   - Bu sayede sayfa bazlı absolute katman çakışmaları tamamen önlenmiş ve tüm sayfalar için tek bir akıcı tasarım standardı sağlanmıştır.

| Web Paneli | Mobil Uygulama (Ortak Layout) |
|:---|:---|
| Sol dikey menü (sidebar) | Alt navigasyon çubuğu (Centralized Bottom Nav) |
| Geniş veri tabloları | Kaydırılabilir premium cam kart listeleri (Bento Grid) |
| Çoklu kolon yerleşim | Tek kolon responsive akış |
| Hover efektleri | Touch/Swipe gesture'ları & Mikro-etkileşimler |
| Büyük modal'lar | Tam ekran bottom sheet'ler & Bento drawer |

### C. JWT Token Güvenliği

| Web (Blazor WASM) | Mobil (MAUI) |
|:---|:---|
| `localStorage` (XSS riski var) | `SecureStorage` (Android Keystore ile şifreli) |
| Tarayıcı kapanırsa token kalır | Uygulama silinirse token silinir |

---

## 9. Mobil Tarayıcı Banner'ı

Web paneline telefonun tarayıcısından girersen:

```
┌──────────────────────────────────────────┐
│  📱 ApiHub'ı mobilde deneyimle!           │
│  Daha hızlı, bildirimlerle.              │
│  [Hemen İndir]            [Kapat ✕]      │
└──────────────────────────────────────────┘
```

- `navigator.userAgent`'tan mobil cihaz tespiti
- Kapatılırsa 7 gün gösterilmez (`localStorage`)

---

## 10. Geliştirme ve Derleme

### A. Solution'a Ekleme

```bash
# 1. MAUI Blazor Hybrid projesi oluştur
dotnet new maui-blazor -n DockerPanel.Mobile -o src/DockerPanel.Mobile

# 2. Solution'a ekle
dotnet sln DockerPanel.sln add src/DockerPanel.Mobile/DockerPanel.Mobile.csproj

# 3. Paylaşılan projeleri referans al
cd src/DockerPanel.Mobile
dotnet add reference ../DockerPanel.Client/DockerPanel.Client.csproj
dotnet add reference ../DockerPanel.Domain/DockerPanel.Domain.csproj
```

### B. NuGet Paketleri

| Paket | Amaç |
|:---|:---|
| `Microsoft.Maui.Controls` | MAUI çekirdek |
| `Microsoft.AspNetCore.Components.WebView.Maui` | BlazorWebView host |
| `MudBlazor` | UI bileşen kütüphanesi |
| `Microsoft.AspNetCore.SignalR.Client` | Canlı metrik akışı |
| `Plugin.Firebase.CloudMessaging` | FCM push bildirim |

### C. APK Derleme

```bash
# Debug (test)
dotnet build src/DockerPanel.Mobile -f net8.0-android -c Debug

# Release (kendi telefonuna yüklemek için)
dotnet publish src/DockerPanel.Mobile -f net8.0-android -c Release
```

---

## 11. Test Planı

| Test | Nasıl | Başarı |
|:---|:---|:---|
| **Sayfa Uyumu** | 10 web sayfasını mobilde aç | Responsive, kırılma yok |
| **SignalR** | Arka plana at, 5dk bekle | Batarya ≈ 0%, geri dönüşte 3sn'de bağlanır |
| **FCM Push** | Bir konteyneri durdur | 30sn içinde telefona bildirim gelir |
| **Deep Link** | Bildirime dokun | Doğru proje sayfasında açılır |
| **App Shortcuts** | İkona basılı tut | 3 kısayol çalışır |
| **Auto-Update** | Sunucuya yeni APK koy | Uygulama açılışında güncelleme uyarısı |
| **QR Kod** | Panelden QR tara | APK telefona iner |
| **SecureStorage** | Uygulamayı kapat/aç | Yeniden giriş gerekmez |
| **Offline** | İnterneti kes | Uyarı gösterir, çökmez |

---

---

# BÖLÜM 2: BACKUP & RESTORE

---

## 1. Ne İçin Yapılacak?

Sunucunda çalışan tüm projeler, veritabanları, mail hesapları ve konfigürasyonlar tek bir yerde: PostgreSQL. Disk bozulursa, yanlışlıkla bir şey silersen veya sunucu çökerse — **her şeyi kaybedersin.**

Backup & Restore sistemi şunları korur:

| Korunan Veri | Neden Önemli |
|:---|:---|
| **PostgreSQL veritabanı** | Tüm projeler, subdomainler, DNS kayıtları, kullanıcı bilgileri, mail hesapları |
| **Native proje dosyaları** | `/opt/dockerpanel/projects/` altındaki web siteleri ve uygulamalar |
| **Nginx konfigürasyonları** | `/etc/nginx/sites-available/` altındaki proxy kuralları |
| **Mail verileri** | `/opt/dockerpanel/mail/` altındaki e-posta kutuları |

**Gerçek Hayat Senaryoları:**
- 🔴 Yanlışlıkla `DROP DATABASE` çalıştırdın → Yedekten 5 dakikada geri yükle
- 🔴 Sunucu diski bozuldu → Yeni sunucuya yedekten restore et
- 🔴 Bir native projenin dosyalarını yanlışlıkla sildin → Yedekten geri al
- 🟡 Nginx config bozuldu, hiçbir site açılmıyor → Önceki config'i yedekten geri yükle

---

## 2. Nasıl Yapılacak?

### A. Mimari Akış

```
                    ┌──────────────────────────────────┐
                    │     BackupWorker                  │
                    │     (BackgroundService)           │
                    │                                    │
                    │  Her gece 03:00'te otomatik       │
                    │  çalışır (cron benzeri)            │
                    └───────────┬──────────────────────┘
                                │
                ┌───────────────┼───────────────┐
                ▼               ▼               ▼
        ┌──────────┐    ┌──────────┐    ┌──────────┐
        │ pg_dump  │    │ tar.gz   │    │ tar.gz   │
        │ (DB)     │    │ (Projeler)│    │ (Nginx)  │
        └────┬─────┘    └────┬─────┘    └────┬─────┘
             │               │               │
             ▼               ▼               ▼
        ┌─────────────────────────────────────────┐
        │  /opt/dockerpanel/backups/               │
        │                                          │
        │  backup_2026-05-28_030000/               │
        │  ├── database.sql.gz          (DB dump)  │
        │  ├── projects.tar.gz    (Proje dosyaları)│
        │  ├── nginx.tar.gz     (Nginx configleri) │
        │  └── manifest.json   (Meta bilgi)        │
        │                                          │
        │  backup_2026-05-27_030000/               │
        │  └── ...                                 │
        │                                          │
        │  (7 günden eski yedekler otomatik siliniyor)
        └─────────────────────────────────────────┘
```

### B. Backend Katmanı

#### [NEW] `BackupWorker.cs` → API/Workers/

**Ne yapar:** Her gece 03:00'te arka planda otomatik çalışan `BackgroundService`.

**Adım adım işleyişi:**

1. **Saat kontrolü:** `PeriodicTimer` ile her 1 saatte bir tetiklenir. Saat 03:00 değilse atlar.
2. **Yedek dizini oluşturma:** `/opt/dockerpanel/backups/backup_[tarih_saat]/` klasörünü `mkdir -p` ile oluşturur.
3. **PostgreSQL yedekleme:**
   - `pg_dump -h localhost -U dp_admin dockerpanel_db | gzip > database.sql.gz` komutunu `Process.Start` ile çalıştırır.
   - Çıkış kodu 0 değilse hata loglar ve Audit Log'a "BackupFailed" kaydı düşer.
4. **Proje dosyalarını arşivleme:**
   - `tar -czf projects.tar.gz -C /opt/dockerpanel/projects .` ile tüm native proje dizinlerini sıkıştırır.
5. **Nginx config yedekleme:**
   - `tar -czf nginx.tar.gz -C /etc/nginx/sites-available .` ile proxy konfigürasyonlarını yedekler.
6. **Manifest dosyası yazma:**
   ```json
   {
     "timestamp": "2026-05-28T03:00:00Z",
     "databaseSize": "12.4 MB",
     "projectsSize": "256.8 MB",
     "nginxSize": "1.2 KB",
     "totalSize": "270.4 MB",
     "status": "success"
   }
   ```
7. **Eski yedekleri temizleme:** 7 günden eski `backup_*` klasörlerini siler.

#### [NEW] `BackupController.cs` → API/Controllers/

| Metot | Endpoint | Ne Yapar |
|:---|:---|:---|
| `GET` | `api/backups` | Tüm yedeklerin listesini döner (tarih, boyut, durum) |
| `GET` | `api/backups/{folderName}/download/{type}` | Belirli bir yedeğin DB/projeler/nginx dosyasını indirir |
| `POST` | `api/backups/trigger` | Manuel yedekleme başlatır (beklemeden 202 döner) |
| `POST` | `api/backups/{folderName}/restore/{type}` | Seçilen yedeği geri yükler |
| `DELETE` | `api/backups/{folderName}` | Belirli bir yedeği siler |

#### Restore Akışı (Geri Yükleme)

```
"database" restore komutu geldi
         │
         ▼
   1. Aktif DB bağlantılarını kapat (pg_terminate_backend)
   2. Mevcut DB'yi DROP et
   3. Yeni boş DB oluştur (CREATE DATABASE)
   4. gunzip < database.sql.gz | psql komutuyla verileri yükle
   5. API'yi restart et (connection pool yenilensin)
   6. Audit Log'a "DatabaseRestored" kaydı düş
         │
         ▼
   ✅ Veritabanı geri yüklendi!
```

### C. Frontend Katmanı

#### [NEW] `Backups.razor` → Client/Pages/

**Ekran Tasarımı:**

```
┌──────────────────────────────────────────────────────┐
│  💾 Yedekleme Yönetimi                 [Şimdi Yedekle]│
│                                                       │
│  ┌─────────────────────────────────────────────────┐ │
│  │  ✅ 28 Mayıs 2026, 03:00         270.4 MB       │ │
│  │     DB: 12.4 MB  |  Projeler: 256.8 MB          │ │
│  │     [İndir ▼]  [Geri Yükle ▼]  [Sil 🗑️]        │ │
│  ├─────────────────────────────────────────────────┤ │
│  │  ✅ 27 Mayıs 2026, 03:00         268.1 MB       │ │
│  │     DB: 12.2 MB  |  Projeler: 254.7 MB          │ │
│  │     [İndir ▼]  [Geri Yükle ▼]  [Sil 🗑️]        │ │
│  ├─────────────────────────────────────────────────┤ │
│  │  ⚠️ 26 Mayıs 2026, 03:00         — BAŞARISIZ    │ │
│  │     Hata: pg_dump connection refused              │ │
│  └─────────────────────────────────────────────────┘ │
│                                                       │
│  Otomatik yedekleme: Her gece 03:00                   │
│  Saklama süresi: 7 gün                                │
└──────────────────────────────────────────────────────┘
```

### D. Veritabanı Değişiklikleri

Backup sistemi ayrı bir tablo gerektirmiyor — yedekler dosya sistemi üzerinde saklanır. Sadece `manifest.json` dosyaları meta bilgi tutar. Audit Log tablosu backup işlemlerini kaydeder.

### E. NavMenu Güncellemesi

Sol menüye yeni madde:
```
💾 Yedeklemeler → /backups
```

---

---

# BÖLÜM 3: AUDIT LOG

---

## 1. Ne İçin Yapılacak?

Panel üzerinde yaptığın her önemli işlemin kaydı tutuluyor. "Dün gece hangi konteyneri silmiştim?", "Bu subdomain'i ne zaman oluşturmuştum?", "Son girişim ne zamandı?" — hepsinin cevabı burada.

**Gerçek Hayat Senaryoları:**
- 🟡 3 gün önce oluşturduğun bir projeyi yanlışlıkla sildin → Audit Log'dan ne zaman, hangi projeydi öğrenirsin
- 🟡 Bir subdomain çalışmıyor → Log'dan en son ne değişiklik yapıldığını kontrol edersin
- 🟡 Sunucuya birileri girmiş mi? → Login kayıtlarını incelersin
- 🟡 Mobil'den mi web'den mi giriş yapıldığını görürsün

---

## 2. Nasıl Yapılacak?

### A. Mimari Akış

```
  Herhangi bir Controller'da bir işlem yapılıyor
  (örn: ProjectController.CreateContainer)
         │
         ▼
  İşlem başarılı olduktan sonra:
  AuditLogService.LogAsync(new AuditEntry
  {
      UserId = currentUser.Id,
      Action = "ContainerCreated",
      TargetEntity = "Project",
      TargetId = newProject.Id,
      Details = { name: "qrmenu", image: "node:20-alpine" },
      IpAddress = HttpContext.Connection.RemoteIpAddress,
      UserAgent = Request.Headers["User-Agent"]
  });
         │
         ▼
  PostgreSQL: AuditLogs tablosuna INSERT
         │
         ▼
  Admin panelinde görüntülenebilir
```

### B. Veritabanı Tablosu

#### [NEW] `AuditLog.cs` → Domain/Entities/

| Alan | Tip | Açıklama |
|:---|:---|:---|
| `Id` | Guid, PK | Log kaydı |
| `UserId` | Guid, FK → Users.Id | İşlemi yapan (sen) |
| `Action` | VARCHAR(50) | İşlemin tipi |
| `TargetEntity` | VARCHAR(50) | Hangi entity etkilendi |
| `TargetId` | Guid? | Etkilenen kaydın ID'si |
| `Details` | JSONB | Değişiklik detayları |
| `IpAddress` | VARCHAR(45) | İsteğin geldiği IP |
| `UserAgent` | VARCHAR(512) | Web mi, mobil mi? |
| `Timestamp` | DateTimeOffset | İşlem zamanı |

**Kaydedilecek İşlem Tipleri:**

| Action | TargetEntity | Ne Zaman |
|:---|:---|:---|
| `UserLogin` | User | Giriş yapıldığında |
| `UserLogout` | User | Çıkış yapıldığında |
| `ContainerCreated` | Project | Docker konteyner oluşturulduğunda |
| `ContainerStarted` | Project | Konteyner başlatıldığında |
| `ContainerStopped` | Project | Konteyner durdurulduğunda |
| `ContainerDeleted` | Project | Konteyner silindiğinde |
| `NativeProjectDeployed` | Project | ZIP deploy yapıldığında |
| `SubdomainCreated` | Subdomain | Subdomain eklendiğinde |
| `SubdomainDeleted` | Subdomain | Subdomain silindiğinde |
| `DnsRecordCreated` | DnsRecord | DNS kaydı eklendiğinde |
| `DnsRecordDeleted` | DnsRecord | DNS kaydı silindiğinde |
| `DatabaseCreated` | DatabaseSchema | DB oluşturulduğunda |
| `DatabaseDeleted` | DatabaseSchema | DB silindiğinde |
| `MailAccountCreated` | MailAccount | E-posta hesabı açıldığında |
| `MailAccountDeleted` | MailAccount | E-posta hesabı silindiğinde |
| `BackupCreated` | Backup | Yedekleme yapıldığında |
| `BackupRestored` | Backup | Geri yükleme yapıldığında |
| `FirewallRuleAdded` | Firewall | UFW kuralı eklendiğinde |
| `FirewallRuleRemoved` | Firewall | UFW kuralı silindiğinde |

### C. Backend Katmanı

#### [NEW] `IAuditLogService.cs` → Domain/Interfaces/

```
Tek metot: LogAsync(AuditEntry entry) → Task
```

#### [NEW] `AuditLogService.cs` → Infrastructure/Services/

- `DockerPanelDbContext` üzerinden `AuditLogs` tablosuna INSERT
- Fire-and-forget: Controller'ın yanıt süresini yavaşlatmaz
- 90 günden eski logları otomatik temizleme (opsiyonel)

#### [NEW] `AuditLogController.cs` → API/Controllers/

| Metot | Endpoint | Ne Yapar |
|:---|:---|:---|
| `GET` | `api/audit-logs` | Logları filtreli listeler (action, entity, tarih aralığı) |
| `GET` | `api/audit-logs/stats` | Özet istatistik (bugün kaç işlem, en çok ne yapıldı) |

#### Mevcut Controller'lara Ekleme

Her controller'daki başarılı işlem sonrasına `_auditLogService.LogAsync(...)` çağrısı eklenir. Örnek:

**ProjectController.cs → CreateContainer metodu:**
```
... konteyner oluşturuldu ...
await _auditLogService.LogAsync(new AuditEntry {
    Action = "ContainerCreated",
    TargetEntity = "Project",
    TargetId = project.Id,
    Details = new { name, image, memory, cpu }
});
return Ok(project);
```

### D. Frontend Katmanı

#### [NEW] `AuditLogs.razor` → Client/Pages/

**Ekran Tasarımı:**

```
┌────────────────────────────────────────────────────────────┐
│  📋 Denetim Kayıtları                                      │
│                                                             │
│  Filtreler: [Tüm İşlemler ▼] [Tüm Entity'ler ▼] [7 Gün ▼]│
│                                                             │
│  ┌───────────────────────────────────────────────────────┐ │
│  │  🟢 ContainerCreated          Bugün 14:32             │ │
│  │     Project: qrmenu-app                                │ │
│  │     image: node:20-alpine, memory: 512MB               │ │
│  │     IP: 192.168.1.5 · Web                              │ │
│  ├───────────────────────────────────────────────────────┤ │
│  │  🔴 ContainerDeleted          Bugün 14:28             │ │
│  │     Project: test-container                            │ │
│  │     IP: 192.168.1.5 · Web                              │ │
│  ├───────────────────────────────────────────────────────┤ │
│  │  🟡 SubdomainCreated          Bugün 13:15             │ │
│  │     Subdomain: api.burhansahin.com.tr                  │ │
│  │     → qrmenu-app:3000                                  │ │
│  │     IP: 10.0.0.1 · Mobil (Android)                     │ │
│  ├───────────────────────────────────────────────────────┤ │
│  │  🔵 UserLogin                  Bugün 12:00             │ │
│  │     IP: 192.168.1.5 · Web                              │ │
│  └───────────────────────────────────────────────────────┘ │
│                                                             │
│  Sayfa: [◀ 1 2 3 4 5 ▶]                                   │
└────────────────────────────────────────────────────────────┘
```

### E. NavMenu Güncellemesi

Sol menüye yeni madde:
```
📋 Denetim Kayıtları → /audit-logs
```

### F. Veritabanı Migration

```bash
dotnet ef migrations add AddAuditLogs --project src/DockerPanel.Infrastructure --startup-project src/DockerPanel.API
dotnet ef database update --project src/DockerPanel.Infrastructure --startup-project src/DockerPanel.API
```

---

---

# UYGULAMA SIRASI

Önerilen geliştirme sırası:

| Sıra | Özellik | Tahmini Süre | Bağımlılık |
|:---:|:---|:---:|:---|
| **1** | Audit Log (tablo + servis + sayfa) | 1-2 gün | Yok — hemen başlanabilir |
| **2** | Backup & Restore (worker + controller + sayfa) | 2-3 gün | Audit Log (backup olaylarını loglamak için) |
| **3** | Mobil MAUI proje oluşturma ve temel entegrasyon | 2-3 gün | Yok |
| **4** | FCM Push bildirim altyapısı (DeviceTokens + Firebase) | 2-3 gün | Mobil proje |
| **5** | APK dağıtım ve otomatik güncelleme | 1-2 gün | Mobil proje |
| **6** | App Shortcuts + Deep Linking | 1 gün | FCM + Mobil |
| **7** | Mobil UI optimizasyonları ve testler | 2-3 gün | Tüm mobil özellikler |

**Toplam tahmini süre: ~12-17 gün**

---

## Açık Karar Noktaları

1. **Firebase projesi** — Firebase Console'da proje oluşturulmuş mu? `google-services.json` hazır mı?

2. **APK imzalama** — Android APK için bir keystore dosyan var mı, yoksa yeni mi oluşturalım?

3. **Minimum Android sürümü** — Hedef ne olmalı? (Öneri: Android 8.0 / API 26)

4. **Bu sıralama uygun mu?** Audit Log → Backup → Mobil şeklinde ilerleyelim mi?

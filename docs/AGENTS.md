DockerPanel Ajan Entegrasyon ve API Operasyon Kılavuzu (AGENT.md)

Bu kılavuz, DockerPanel projesinin tamamını hayata geçirmek için bir Yapay Zeka Ajanının (Agent) takip etmesi gereken bütünsel sistem mimarisini, mantıksal algoritmalarını, veri tabanı ilişkilerini ve sayfa gereksinimlerini içerir.

Bu doküman, sunucu üzerinde koşan Docker servislerinin yanı sıra, native olarak barındırılan projeleri yöneten project-manager entegrasyonunu ve ZIP tabanlı otomatik dağıtım (deployment) kurallarını kapsar. Kod blokları içermez; tamamen iş mantığı ve mimari talimatlara dayalıdır.

---

## 1. Genel Teknoloji Yığını (Tech Stack) ve Katmanlar

Sistem, kaynakları izole etmek ve mikroservis yaklaşımını korumak için Docker konteynerleri, host üzerinde koşan bir Web API ve süreç yönetim aracı etrafında şekillenmiştir.

*   **Kullanıcı Arayüzü (Frontend UI):** Blazor WebAssembly (.NET 8) ve MudBlazor kütüphanesi. Renk paleti; Indigo (temel derinlik), Siber Zümrüt (çalışan/sağlıklı servisler) ve Asil Yakut (durmuş/kritik/tehlikeli durumlar) hibrit yapısına dayanır.
*   **Kontrol Merkezi (Backend API):** ASP.NET Core Web API (.NET 8). Doğrudan host üzerinde kısıtlı yetkili sistem kullanıcısı (`dockerpanel_api`) altında çalışır. Sunucudaki `/var/run/docker.sock` dosyası üzerinden Docker Engine API'si ile doğrudan konuşur, `/etc/project-manager/projects.conf` dosyasını iplik-güvenli (thread-safe) yönetir ve gelen ZIP dosyalarını Zip Slip korumasıyla açarak native süreçleri yönetir.
*   **Giriş Kapısı (Nginx Gateway Proxy):** Host üzerinde kurulu bare-metal Nginx. `/etc/nginx/sites-available/` ve `/etc/nginx/sites-enabled/` dizinleri üzerinden yönetilir. Subdomain yönlendirmelerini, dinamik sanal host (vhost) kurallarını ve SSL sonlandırma işlemlerini reload ile sıfır kesintili yürütür.
*   **Süreç Yöneticisi (Project Manager):** Host üzerindeki native projeleri yöneten `project-manager.sh` bash altyapısı.
*   **Veri Yönetimi (Database):** Dockerize edilmiş PostgreSQL (dockerpanel_db). Panel ayarlarını, kullanıcı kayıtlarını, aktif proje haritalarını, DNS kayıtlarını ve webmail verilerini saklar.
*   **E-Posta Servisi:** docker-mailserver (Postfix, Dovecot, SpamAssassin ve Fail2Ban entegreli kurumsal çözüm).
*   **Real-Time Akış:** ASP.NET Core SignalR. Proje loglarını ve anlık donanım (CPU/RAM) tüketim verilerini arayüze canlı akıtır.

---

## 2. Sunucu Dizin Yapısı (Directory Architecture)

Ajan, sunucuda aşağıdaki gerçek dizin hiyerarşisini kurmalı ve korumalıdır:

```
/opt/dockerpanel/
│
├── docker-compose.yml          # Mail sunucusu ve veritabanını ayağa kaldıran orkestrasyon dosyası
├── nginx-template.conf         # C# servisinin dinamik vhost oluştururken okuduğu şablon
│
├── projects/                   # Native web projelerinin ZIP arşivlerinden çıkarıldığı ana dizin
│   └── [proje_adi]/            # Çıkarılan native projenin fiziksel dizini
│
├── backups/                    # Veritabanı ve müşteri projelerinin yedekleme klasörü
│
└── mail/
    ├── data/                   # E-posta kutularının (maildir) depolandığı alan
    ├── state/                  # docker-mailserver çalışma durum verileri
    └── config/                 # Postfix ve Dovecot hesap konfigürasyon dosyaları
```

**Nginx Dizin Yapısı (Host Sistemi):**
*   `/etc/nginx/sites-available/` - Dinamik olarak oluşturulan subdomain `.conf` dosyaları.
*   `/etc/nginx/sites-enabled/` - Aktif yönlendirmeler için `sites-available` dosyalarına oluşturulan symlink'ler.
*   `/etc/nginx/certs/` - SSL sertifikaları (.pem).

---

## 3. İlişkisel Veri Tabanı Şeması (Database Schema)

Panel PostgreSQL veritabanında (`dockerpanel_db`) bulunması gereken tablolar ve aralarındaki yabancı anahtar (Foreign Key) ilişkileri:

### A. Tablo: Users (Panel Yöneticileri ve Müşteriler)
*   **Id** (UUID, Primary Key) - Benzersiz kullanıcı kimliği.
*   **Username** (VARCHAR 50, Unique, Not Null) - Giriş adı.
*   **PasswordHash** (VARCHAR 255, Not Null) - BCrypt veya Argon2id ile şifrelenmiş parola.
*   **Role** (VARCHAR 20, Not Null) - Yetki seviyesi (Administrator, Customer).
*   **CreatedAt** (TIMESTAMP WITH TIME ZONE, Default Now) - Hesap oluşturulma tarihi.

### B. Tablo: Projects (Müşteri Projeleri - Docker & Native Hibrit)
*   **Id** (UUID, Primary Key) - Sistem içi benzersiz proje kaydı.
*   **UserId** (UUID, Foreign Key -> Users.Id, Cascade Delete) - Projenin sahibi olan kullanıcı.
*   **Type** (VARCHAR 20, Not Null) - Projenin tipi (`DockerContainer`, `NativeProject`).
*   **Name** (VARCHAR 64, Unique, Not Null) - İşletim sistemindeki benzersiz adı.
*   **ImageOrPath** (VARCHAR 255, Not Null) - Docker imajı ve tag'i (Örn: `node:20-alpine`) veya Native projenin fiziksel çalışma yolu.
*   **MemoryLimitBytes** (BIGINT, Not Null) - RAM kısıtlama boyutu (Örn: 536870912 byte).
*   **CpuCount** (DOUBLE PRECISION, Not Null) - Çekirdek kısıtlama adedi (Örn: 0.5).
*   **InternalPort** (INTEGER, Not Null) - Projenin konteyner/süreç içinde dinlediği port (Örn: 3000).
*   **Status** (VARCHAR 20, Not Null) - Proje durumu (`Provisioning`, `Running`, `Stopped`, `Error`).
*   **CreatedAt** (TIMESTAMP WITH TIME ZONE) - Oluşturulma tarihi.

### C. Tablo: Subdomains (Nginx Proxy Yönlendirmeleri)
*   **Id** (UUID, Primary Key) - Benzersiz yönlendirme kaydı.
*   **UserId** (UUID, Foreign Key -> Users.Id) - Alan adının sahibi.
*   **ProjectId** (UUID, Foreign Key -> Projects.Id, Cascade Delete) - İsteğin iletileceği hedef proje.
*   **SubdomainName** (VARCHAR 63, Not Null) - Alt alan adı ön eki (Örn: api).
*   **DomainName** (VARCHAR 253, Not Null) - Ana alan adı (Örn: domain.com).
*   **SslEnabled** (BOOLEAN, Default True) - HTTPS trafiğinin aktiflik durumu.
*   **CreatedAt** (TIMESTAMP WITH TIME ZONE) - Oluşturulma tarihi.
*   *Kısıt (Constraint):* `SubdomainName` ve `DomainName` ikilisi benzersiz (Unique) olmalıdır.

### D. Tablo: DnsRecords (Dinamik DNS Verileri)
*   **Id** (UUID, Primary Key) - Benzersiz DNS kaydı.
*   **UserId** (UUID, Foreign Key -> Users.Id) - Kaydı oluşturan kullanıcı.
*   **Type** (VARCHAR 10, Not Null) - DNS kayıt tipi (A, CNAME, MX, TXT).
*   **Name** (VARCHAR 253, Not Null) - Kayıt ismi (Örn: *, @, mail).
*   **Value** (TEXT, Not Null) - Hedef IP, alan adı veya metin içeriği.
*   **Ttl** (INTEGER, Default 3600) - Saniye cinsinden yaşam süresi.
*   **Proxied** (BOOLEAN, Default False) - Cloudflare CDN/Proxy koruma durumu.
*   **CloudflareRecordId** (VARCHAR 128, Nullable) - Cloudflare tarafındaki gerçek kayıt ID'si.

### E. Tablo: DatabaseSchemas (Müşteri PostgreSQL Şemaları)
*   **Id** (UUID, Primary Key) - Benzersiz veritabanı kaydı.
*   **UserId** (UUID, Foreign Key -> Users.Id) - Veritabanının sahibi.
*   **DbName** (VARCHAR 63, Unique, Not Null) - PostgreSQL üzerindeki veritabanı adı.
*   **DbUser** (VARCHAR 63, Unique, Not Null) - PostgreSQL kullanıcısı.
*   **CreatedAt** (TIMESTAMP WITH TIME ZONE) - Oluşturulma tarihi.

### F. Tablo: MailAccounts (E-Posta Hesapları)
*   **Id** (UUID, Primary Key) - Benzersiz e-posta kaydı.
*   **UserId** (UUID, Foreign Key -> Users.Id) - E-postanın sahibi.
*   **EmailAddress** (VARCHAR 254, Unique, Not Null) - E-posta adresi.
*   **QuotaBytes** (BIGINT, Not Null) - Posta kutusu kota sınırı.
*   **CreatedAt** (TIMESTAMP WITH TIME ZONE) - Oluşturulma tarihi.

---

## 4. Modüler Mantıksal Servis Algoritmaları (Business Logic)

Ajan, API backend katmanındaki C# servislerini tasarlarken aşağıdaki detaylı algoritma akışlarını birebir gerçek koda dökmelidir:

### A. Konteyner Sağlama Mantığı (Container Provisioning Flow)
1.  **Girdi Doğrulama:** Gelen `appName` parametresinin sadece `^[a-z0-9_-]+$` regex süzgecine uyduğunu kontrol et.
2.  **Mükerrerlik Kontrolü:** `Projects` tablosunda aynı isimde başka bir kayıt var mı sorgula.
3.  **Docker API İletişimi:** `Docker.DotNet` istemcisini `/var/run/docker.sock` üzerinden tetikle.
4.  **İmaj Kontrolü:** İstenen Docker imajının yerel sunucuda olup olmadığını denetle; yoksa `CreateImageAsync` ile resmi Docker Hub'dan çek.
5.  **Donanım Hesaplamaları:**
    *   CPU limitini (Örn: 0.5) 1.000.000.000 ile çarparak `NanoCPUs` limitine çevir.
    *   RAM limitini megabayt cinsinden alıp byte değerine çevir (1 MB = 1.048.576 Byte).
6.  **Konteyner Yaratımı:**
    *   Konteyneri `HostConfig.NetworkMode = "dockerpanel-global-net"` parametresiyle global köprü ağına bağla.
    *   Gerekli RAM ve CPU limit değerlerini `HostConfig` nesnesine aktar.
    *   `RestartPolicy` değerini `always` olarak ayarla.
7.  **Konteyner Çalıştırma:** Oluşturulan konteyneri başlat, işletim sisteminden gelen Docker ID değerini veri tabanına `Running` statüsüyle kaydet.

### B. Güvenli ZIP Dağıtım Mantığı (Secure ZIP Deployment Flow)
1.  **Girdi Doğrulama:** Gelen `projectName` parametresini doğrula ve projenin `Projects` tablosunda mükerrerliğini kontrol et.
2.  **Zip Slip Engelleme Algoritması:** ZIP dosyasındaki her giriş (entry) için hedef yolu asenkron olarak oluştur.
    *   `Path.GetFullPath` kullanarak çıkarılacak dosyanın tam disk yolunu (`fileFullPath`) çözümle.
    *   `Path.GetFullPath` kullanarak hedef dağıtım dizininin tam yolunu (`destinationFullPath` -> `/opt/dockerpanel/projects/[proje_adi]/`) al.
    *   `fileFullPath.StartsWith(destinationFullPath, StringComparison.OrdinalIgnoreCase)` kontrolünü gerçekleştir.
    *   Eğer dosya yolu hedef dizinin dışına taşıyorsa işlemi derhal durdur, açılan geçici dosyaları sil ve `InvalidOperationException` fırlatarak deploy sürecini iptal et.
3.  **Çıkarma ve Dizin Yapılandırması:** Güvenlik denetimini geçen dosyaları `/opt/dockerpanel/projects/[proje_adi]/` dizinine çıkar.

### C. Konu-Paralel Süreç Yöneticisi Mantığı (Process Manager Flow)
1.  **İplik-Güvenli (Thread-Safe) INI Okuma/Yazma:**
    *   `/etc/project-manager/projects.conf` dosyasını okurken ve yazarken asenkron yarış durumlarını (race conditions) önlemek amacıyla `static SemaphoreSlim(1,1)` kilitlemesi uygula.
    *   Yeni native projenin parametrelerini (`Name`, `Path`, `Port`, `MemoryLimit`, `CpuLimit`) INI formatında ekle/güncelle ve diske kaydet.
2.  **Parolasız Sudo Süreç Orkestrasyonu:**
    *   Süreç yönetim script'ini asenkron alt süreçler (Processes) vasıtasıyla tetikle: `sudo project-manager.sh [start|stop|restart|delete] [project_name]`.
    *   Komutun çıktı ve hata akışlarını (`stdout`/`stderr`) dinleyerek çıkış kodunun 0 olduğunu doğrula. 0 ise işlemi veritabanına `Running` veya ilgili statüyle kaydet.
3.  **Log Okuma:** `/var/log/project-manager/[proje_adi].log` dosyasının son 100 satırını asenkron olarak oku ve arayüze dön.

### D. Nginx Proxy Yapılandırma Mantığı (Nginx Reverse Proxy Flow)
1.  **Şablon Okuma:** `/opt/dockerpanel/nginx-template.conf` dosyasını belleğe yükle.
2.  **Token Değiştirme:** Şablondaki `{{Subdomain}}`, `{{Domain}}`, `{{ContainerName}}` (veya Native proje adı) ve `{{ContainerPort}}` ifadelerini yeni proxy bilgileriyle güvenli bir şekilde değiştir.
3.  **Dosya Yazımı:** Derlenen yeni metni `/etc/nginx/sites-available/[subdomain].[domain].conf` yoluna kaydet.
4.  **Symlink Oluşturma:** Dosyayı `/etc/nginx/sites-enabled/[subdomain].[domain].conf` yoluna sembolik bağ (symlink) olarak bağla.
5.  **Nginx Konfigürasyon Testi:** Sunucuda `sudo nginx -t` komutunu çalıştırarak yazılan yeni konfigürasyonun Nginx tarafından doğrulanıp doğrulanmadığını kontrol et.
    *   **Eğer hata varsa (Rollback):** Eklenen `.conf` dosyasını ve oluşturulan symlink'i derhal sil, veri tabanı kaydını geri al ve arayüze hata fırlat.
    *   **Eğer hata yoksa (Zero-Downtime Reload):** Sunucuda `sudo systemctl reload nginx` komutunu koşturarak sıfır kesintiyle yönlendirmeyi aktif et.

### E. Dinamik PostgreSQL Veritabanı Mantığı (Database Provisioning Flow)
1.  **Girdi Güvenliği:** `dbName` ve `dbUser` parametrelerinin SQL enjeksiyonu riski taşımadığını doğrulamak için katı karakter sınırlaması uygula.
2.  **Master DB Bağlantısı:** API'nin yerel PostgreSQL sunucusuna süper yönetici haklarıyla (`dp_admin`) bağlanmasını sağla.
3.  **Sıralı SQL Çalıştırma:**
    *   Öncelikle `CREATE USER [dbUser] WITH ENCRYPTED PASSWORD '[dbPassword]';` sorgusunu çalıştır.
    *   Ardından `CREATE DATABASE [dbName] OWNER [dbUser];` sorgusuyla şemayı yarat.
    *   Son olarak `GRANT ALL PRIVILEGES ON DATABASE [dbName] TO [dbUser];` sorgusuyla yetki sınırlarını çiz.
4.  **Bağlantı Kapatma:** İşlem bittiğinde master havuz bağlantısını güvenli bir şekilde kapat.

### F. docker-mailserver Yönetim Mantığı (Mail Management Flow)
1.  **Hesap Oluşturma:**
    *   Sunucuda `docker exec dockerpanel-mailserver setup email add [emailAddress] [password]` komutunu arka planda güvenli işlem süreci (Process) olarak başlatmalıdır.
    *   Komutun çıktı ve hata akışlarını dinle. Hata akışı boşsa ve çıkış kodu 0 ise işlemi veritabanına kaydet.
2.  **Hesap Silme:**
    *   `docker exec dockerpanel-mailserver setup email del [emailAddress]` komutunu tetikle.
    *   Mail sunucusundaki fiziksel posta kutusu dizinlerinin silindiğinden emin ol.

### G. Cloudflare DNS Entegrasyon Mantığı (Cloudflare API Flow)
1.  **İstek Hazırlığı:** Cloudflare v4 API'sine istek atmak için bir HTTP istemcisi yapılandır.
2.  **Yetkilendirme:** İstek başlıklarına `Authorization: Bearer [Cloudflare_API_Token]` bilgisini yerleştir.
3.  **JSON Payload Derleme:** Gövdeyi oluştur: `type: "A"`, `name: Subdomain veya domain`, `content: Sunucu IP`, `proxied: true/false`.
4.  **İstek Gönderimi:** `POST zones/[Zone_ID]/dns_records` adresine isteği gönder.
5.  **Yanıt İşleme:** Cloudflare'den dönen JSON yanıtındaki `id` değerini yakala ve veri tabanındaki `DnsRecords.CloudflareRecordId` alanına kaydet.

### H. Canlı Log ve Metrik Akışı Mantığı (SignalR Signal Flow)
1.  **Hafızadaki Metrik Takibi (Background Worker):** Arka planda çalışan bir C# servisi, her 3 saniyede bir aktif projeleri sorgular.
    *   **Docker Projeleri:** `/var/run/docker.sock` üzerinden donanım istatistiklerini (`docker stats`) asenkron okur. Log akışı için `GetContainerLogsAsync` metodunu dinler.
    *   **Native Projeler:** Disk/süreç performans istatistiklerini simüle eder. Log akışı için `/var/log/project-manager/[ad].log` dosyasını asenkron izler.
2.  **Gruplandırma:** SignalR Hub üzerinde her `ProjectId` için özel bir grup (`project_[projectId]`) oluştur.
3.  **İstemci Yayony:** Canlı CPU, RAM ve log satırlarını sadece ilgili grubun üyelerine (`ReceiveProjectMetrics` ve `ReceiveProjectLogs` metotları ile) basar.

---

## 5. Kullanıcı Arayüzü Ekranları ve Fonksiyonel Gereksinimleri

Blazor WebAssembly arayüzü, siber-hibrit renk paletiyle tasarlanmış olup şu sayfa ve işlevlere sahip olmalıdır:

### A. Genel Bakış (Dashboard)
*   **İşlev:** Sunucunun genel donanım sağlığını ve panel özetini gösterir.
*   **Görsel Bileşenler:**
    *   Aktif Konteyner/Proje Sayısı, Toplam CPU Yükü, Toplam Bellek Tüketimi ve Aktif Subdomain widget'ları.
    *   *Canlı Sistem Yükü Grafiği:* SignalR'dan gelen verilerle anlık dalgalanan, yeşil (CPU) ve kırmızı (RAM) çizgilerden oluşan dinamik SVG çizgi grafiği.
    *   *Canlı Docker Log Akış Kutusu:* Sunucu genelindeki orkestrasyon adımlarını siber zümrüt yeşili monospaced fontla gösteren terminal kutusu.

### B. Proje & Konteyner Yönetimi (Project & Container Manager)
*   **İşlev:** Müşterilerin izole Docker servislerini veya Native web projelerini oluşturup yönettikleri ana ekrandır.
*   **Görsel Bileşenler:**
    *   *Toplu Yeniden Başlatma Butonu:* Sağ üst köşede bulunan, tek tıkla tüm çalışan projeleri (hem Docker hem Native) asenkron olarak yeniden başlatan modern buton.
    *   *Yeni Proje Sihirbazı Modalı:* Docker veya Native ZIP seçimi sunan iki sekmeli form yapısı:
        *   **Docker Sekmesi:** Uygulama adı, kullanılacak Docker imajı, RAM limiti seçici, CPU çekirdek limiti seçici ve dış port girdileri.
        *   **Native ZIP Sekmesi:** Uygulama adı, RAM/CPU limiti, iç port girdisi ve drag-and-drop görsel standardına sahip premium ZIP dosya yükleyici (`FolderZip` simgeli dosya görseli).
    *   *Proje Bento-Grid Kartları:* Her kartta proje adı, tipi (DOCKER / NATIVE), çalışma durumu göstergesi (pulse-green, pulse-red), canlı performans metrikleri (CPU/RAM yüzdeleri), port bilgisi, Başlat/Durdur butonu, Canlı Log Gör butonu ve Projeyi Yok Et butonu yer alır.

### C. Domain & DNS Yönetimi (Domain / DNS / SSL)
*   **İşlev:** Nginx Reverse Proxy kurallarını ve DNS yönlendirmelerini yönetir.
*   **Görsel Bileşenler:**
    *   *DNS Modu Seçici (Toggle):* "Yerel / Manuel DNS" ve "Cloudflare API" modları arasında interaktif geçiş.
    *   *Cloudflare API Kartı:* API Token, Zone ID giriş alanları ve bağlantı durum rozeti.
    *   *Proxy Kuralları Tablosu:* Tanımlanmış alt alan adları, yönlendirildikleri hedef projeler, proxy çalışma durumu ve Let's Encrypt SSL durumlarını içeren tablo.
    *   *DNS Zone Kayıtları Tablosu:* A, CNAME, MX ve TXT kayıtlarını listeler ve yeni kayıt formu sunar.

### D. Veritabanı Yönetimi (Database Manager)
*   **İşlev:** PostgreSQL master sunucusu üzerinde dinamik şemaların yönetildiği ekrandır.
*   **Görsel Bileşenler:**
    *   *Yeni Şema Yarat Modalı:* Veritabanı adı ve bu şemaya erişecek benzersiz veri tabanı kullanıcısı oluşturma formu.
    *   *Veritabanı Listesi Tablosu:* Şema adı, yetkili kullanıcı adı, tipi (PostgreSQL) ve fiziksel boyutu gösteren tablo ile satır silme butonu.
    *   *PostgreSQL Durum Kartı:* İç port (5432), aktif bağlantı sayısı ve toplam veritabanı kullanım alanını gösteren bilgi paneli.

### E. E-Posta Sunucusu Yönetimi (Email Server Manager)
*   **İşlev:** docker-mailserver üzerindeki e-posta hesaplarını ve kotalarını yönetir.
*   **Görsel Bileşenler:**
    *   *Kayıtlı E-Posta Adresleri Tablosu:* E-posta adresi, kota doluluk durumunu gösteren ilerleme çubuğu, Spam süzgeci aktiflik rozeti, hesap durum rozeti ve hesabı silme butonu.
    *   *Yeni E-Posta Ekle Modalı:* E-posta ön eki, şifre ve kota limiti seçimi barındıran form.
    *   *E-Posta İstemci Bilgileri Kartı:* Dış istemci kurulumları için gereken IMAP (Port 993 SSL) ve SMTP (Port 587 TLS) host adreslerini gösteren bilgi alanı.

### F. Entegre Webmail İstemcisi (Webmail Inbox)
*   **İşlev:** Sunucu üzerindeki mail hesaplarına ait e-postaları okuma, cevaplama ve yeni e-posta gönderme işlemlerini gerçekleştiren tam fonksiyonel webmail arayüzüdür.
*   **Görsel Düzen (3 Kolonlu Profesyonel Yerleşim):**
    *   *Sol Kolon:* Aktif mail kutusunu değiştiren dropdown, "Gelen Kutusu", "Gönderilenler", "Taslaklar" ve "Spam" butonları ile kota doluluk ilerleme çubuğu.
    *   *Orta Kolon:* E-posta listesi (okunmamış mailler yeşil zümrüt noktasıyla listelenir). Gönderilenler veya Taslaklar klasörü listelenirken gönderici adı yerine alıcı ("Kime: alici@domain.com") bilgisi listelenir. Klasör ilk yüklendiğinde ya da klasörler arası geçişte en baştaki mail otomatik olarak seçilmez ve okunmaz; kullanıcı tıklayana kadar sağ kolon boş kalır.
    *   *Sağ Kolon:* Seçilen mailin konusu, adresi, tarihi, alıcı bilgisi, zengin metin (Rich Text) mail gövdesi, cevapla/sil butonları ve hızlı yanıt alanı.
    *   *E-Posta Gönderme Modalı:* "Kime", "Konu" ve "Mesaj Gövdesi" alanlarını barındıran SMTP kuyruk yönetimini simüle eden modern form. Form içinde Kalın, Eğik, Altı Çizili, Başlık, Bağlantı ve Paragraf etiketleri eklemeyi sağlayan HTML Zengin Metin Editörü Toolbar'ı bulunur.
    *   *Dosya Eki Desteği:* Gönderilecek iletilerde en fazla 5 dosya ve 10MB limitli dosya yükleme altyapısı bulunur. Eklenen dosyalar Base64 formatına çevrilerek asenkron olarak maile gömülür ve alıcı tarafında şık, indirilebilir bloklar halinde görüntülenir.

### G. Canlı Terminal & Log Akışı (Terminal Console)
*   **İşlev:** Sunucudaki ana orkestratör loglarını ve tüm alt docker servislerinin ham çıktılarını terminalde izleme olanağı sağlar.
*   **Görsel Bileşenler:**
    *   Yeşil yanıp sönen siber nokta barındıran `/var/log/cpanel-orchestrator.log` dosyası simülasyonu.
    *   Terminal geçmişini sıfırlayan "Temizle" butonu.
    *   Siyah arka plan üzerine siber zümrüt yeşili akan monospaced log satırları.

---

## 6. Güvenlik Duvarı, Ağ ve Docker İzolasyon Politikaları

Ajan, sunucu kurulum aşamasında güvenlik seviyesini maksimumda tutmak için şu kuralları uygulamalıdır:

*   **Donanım Socket İzolasyonu:** Kontrol paneli Web API'si, ana Linux işletim sisteminde `root` haklarıyla çalıştırılmamalıdır. Web API, kısıtlı yetkilere sahip bir sistem kullanıcısı (`dockerpanel_api`) altında çalıştırılmalı ve sadece `/var/run/docker.sock` dosyasına okuma/yazma izni verilmelidir (chmod 666).
*   **Sudoers Yetkilendirmesi:** Web API kullanıcısının Nginx konfigürasyonlarını reload edebilmesi ve Native süreç yönetim script'ini tetikleyebilmesi için `/etc/sudoers.d/dockerpanel_api` dosyası oluşturulmalı ve sadece ilgili komutlar için şifresiz sudo hakkı tanınmalıdır:
    ```
    dockerpanel_api ALL=(ALL) NOPASSWD: /usr/sbin/nginx -t, /usr/sbin/service nginx reload, /usr/sbin/systemctl reload nginx, /usr/bin/certbot *, /usr/local/bin/project-manager.sh *, /usr/sbin/ufw *, /usr/bin/tar *, /usr/bin/chown *, /usr/bin/rm *
    ```
*   **Ağ Segmentasyonu (Network Segmentation):** Müşteri konteynerlerinin tamamı `dockerpanel-global-net` köprü ağına dahil edilmelidir. Bu ağın dışındaki hiçbir servis, müşteri konteynerlerinin iç portlarına doğrudan erişemez. Tüm giriş trafiği sadece host üzerindeki Nginx Gateway reverse proxy yönlendirmesinden geçmek zorundadır.
*   **PostgreSQL Güvenlik Sınırı:** PostgreSQL master konteyneri, sunucunun dış IP adresindeki 5432 portunu dış dünyaya kesinlikle açmamalıdır. API ile DB arasındaki iletişim yalnızca Docker iç ağı üzerinden sağlanmalıdır.
*   **UFW Kuralları:** Sunucuda sadece SSH (25017), HTTP (80), HTTPS (443), SMTP (25/587) ve IMAP (993) portlarına dışarıdan gelen isteklere izin verilmelidir. Diğer tüm portlar bloklanmalıdır.

---

## 7. Sistem Servisi ve Log Okuma İzinleri

*   **API Servis Tanımı:** Sunucu üzerindeki ana backend API'si systemd altında `dockerpanel-api.service` adıyla çalıştırılır.
*   **Log Erişim Yetkileri:** API kullanıcısının (`dockerpanel_api`) sistem ve web sunucu loglarını (`/var/log/nginx/` ve `/var/log/syslog`) okuyabilmesi için sırasıyla `adm` ve `www-data` gruplarına eklenmiş olması gerekmektedir.
*   **Süreç Yöneticisi Log Klasörü:** `/var/log/project-manager/` dizini sunucuda bulunmadığı takdirde, öncelikle `mkdir -p` ile oluşturulmalı, ardından sahipliği `dockerpanel_api:dockerpanel_api` olarak güncellenip `755` izni verilmelidir. Değişikliklerin geçerli olması için `dockerpanel-api.service` servisi yeniden başlatılmalıdır.

---

## 8. Ajan İletişim ve Çıktı Standardı (Agent Communication & Output Standard)

*   **Sanal Ajan Çıktı Kuralı:** Yapay zeka ajanları çıktıları asla uzun tutmamalıdır. Sadece hangi dosyada neyi, nasıl yaptığını net bir şekilde belirtmeli; kesinlikle ekstra öneri, tavsiye veya "şunları da yapabiliriz" gibi yönlendirmeler sunmamalıdır. Sadece doğrudan iş mantığını uygulayıp raporlamalıdır.

---

## 9. Mobil Cihaz Algılama ve Mobil Banner Yönlendirme Politikası

*   **Mobil Tarayıcı Algılama (User-Agent Detection):** Web paneline mobil tarayıcılar (Android/iOS) üzerinden giriş yapıldığında, kullanıcıya premium `.NET MAUI Blazor Hybrid` mobil uygulamasının indirilmesini öneren modern, dinamik bir mobil banner gösterilir.
*   **Banner Kapatma ve Çerez Saklama (LocalStorage Persistence):** Kullanıcı banner'ı kapattığında `localStorage` üzerinde 7 günlük bir engelleyici kayıt oluşturulmalı ve bu süre boyunca banner kullanıcıya tekrar gösterilmemelidir.
*   **Güvenli APK İndirme Akışı (Secure APK Download Flow):** Banner'daki "Hemen İndir" butonu, JWT doğrulamalı kullanıcı oturumuna veya QR kod ile oluşturulmuş 15 dakikalık geçici tek kullanımlık token'lara dayanarak güvenli `/api/downloads/apk` uç noktasından indirmeyi başlatır.


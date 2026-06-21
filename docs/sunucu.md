# DockerPanel Sunucu Kurulum ve Yapılandırma Kılavuzu (sunucu.md)

Bu döküman, DockerPanel platformunun (ASP.NET Core Web API ve Blazor WebAssembly) Linux sunucu üzerindeki gereksinimlerini, güvenlik sınırlarını, sistem kullanıcısı ve yetkilendirme yapılandırmalarını ayrıntılı bir şekilde açıklamaktadır.

---

## 1. Sunucu Gereksinimleri (Server Requirements)

DockerPanel'in sorunsuz çalışabilmesi için sunucuda aşağıdaki bileşenlerin kurulu ve yapılandırılmış olması gerekir:

*   **İşletim Sistemi:** Ubuntu 22.04 LTS veya güncel Debian tabanlı Linux dağıtımı.
*   **Docker Engine:** Sürüm 20.10+ (Docker socket erişilebilir olmalıdır).
*   **Nginx:** Web sunucusu ve reverse proxy gateway olarak host üzerinde bare-metal kurulu olmalıdır.
*   **Certbot (Let's Encrypt):** Dinamik SSL sertifikası üretimi için.
*   **PostgreSQL Client & Utilities:** Veritabanı yedekleme ve geri yükleme işlemleri için sunucuda `pg_dump` ve `psql` araçlarının bulunması gerekir.
*   **.NET Runtime 8.0:** Host üzerinde Web API'nin koşturulabilmesi için.

---

## 2. Sistem Kullanıcısı Tanımlama (System User Setup)

Güvenlik prensipleri gereğince, DockerPanel Web API'si kesinlikle `root` kullanıcısı ile çalıştırılmamalıdır. Panel için kısıtlı yetkilere sahip `dockerpanel_api` adında bir sistem kullanıcısı oluşturulur.

### Kullanıcı Oluşturma Komutları:
```bash
# Sistem kullanıcısı olarak oturum açma yetkisi olmadan oluşturun
sudo useradd -r -s /usr/sbin/nologin dockerpanel_api
```

---

## 3. Docker Socket İzinleri ve İzolasyon

Web API'nin Docker Engine ile doğrudan haberleşebilmesi için `/var/run/docker.sock` dosyasına okuma ve yazma erişimi olmalıdır. 

### İzinlerin Yapılandırılması:
```bash
# Docker soketine gerekli okuma/yazma izinlerini verin
sudo chmod 666 /var/run/docker.sock

# Alternatif olarak dockerpanel_api kullanıcısını docker grubuna ekleyin
sudo usermod -aG docker dockerpanel_api
```

---

## 4. Sudoers Yapılandırması (Sudoers Configurations)

`dockerpanel_api` kullanıcısının sistem üzerinde Nginx konfigürasyonlarını test etmesi, yeniden yüklemesi, SSL sertifikaları üretmesi ve native süreç yöneticisi scriptini tetikleyebilmesi için şifresiz sudo yetkileri tanımlanmalıdır.

Bu amaçla `/etc/sudoers.d/dockerpanel_api` dosyası oluşturulur ve aşağıdaki satırlar eklenir:

```
dockerpanel_api ALL=(ALL) NOPASSWD: /usr/sbin/nginx -t, /usr/sbin/service nginx reload, /usr/sbin/systemctl reload nginx, /usr/bin/certbot *, /usr/local/bin/project-manager.sh *, /usr/sbin/ufw *, /usr/bin/tar *, /usr/bin/chown *, /usr/bin/rm *
```

---

## 5. Log ve Süreç Yöneticisi Dizin Yetkileri

API kullanıcısının sistem ve web sunucu loglarını okuyabilmesi ve süreç yöneticisi loglarını yazabilmesi için gerekli dizin ve grup izinleri ayarlanmalıdır.

### Nginx ve Sistem Logları Erişim Yetkileri:
`dockerpanel_api` kullanıcısı `adm` ve `www-data` gruplarına eklenmelidir:
```bash
sudo usermod -aG adm dockerpanel_api
sudo usermod -aG www-data dockerpanel_api
```

### Süreç Yöneticisi Log Klasörü Kurulumu:
Süreç yöneticisine ait log dizini (/var/log/project-manager/) oluşturularak sahipliği güncellenmeli ve uygun yazma izinleri verilmelidir:
```bash
# Dizin oluşturma
sudo mkdir -p /var/log/project-manager/

# Sahiplik ve izin atamaları
sudo chown -R dockerpanel_api:dockerpanel_api /var/log/project-manager/
sudo chmod 755 /var/log/project-manager/
```

---

## 6. Systemd Servis Kurulumu (Systemd Service Setup)

DockerPanel Web API uygulamasının sunucu başlangıcında otomatik olarak ayağa kalkması ve arka planda sürekli çalışması için bir systemd servisi tanımlanır.

### Servis Dosyası: `/etc/systemd/system/dockerpanel-api.service`
```ini
[Unit]
Description=DockerPanel Web API Service
After=network.target

[Service]
WorkingDirectory=/opt/dockerpanel/api
ExecStart=/usr/bin/dotnet DockerPanel.API.dll
Restart=always
# Uygulamanın çöktüğünde 10 saniye sonra tekrar başlamasını sağlar
RestartSec=10
KillSignal=SIGINT
SyslogIdentifier=dockerpanel-api
User=dockerpanel_api
Group=dockerpanel_api
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=DOTNET_PRINT_TELEMETRY_MESSAGE=false

[Install]
WantedBy=multi-user.target
```

### Servisi Aktifleştirme Komutları:
```bash
# Systemd konfigürasyonlarını yeniden yükleyin
sudo systemctl daemon-reload

# Servisi başlangıçta çalışacak şekilde etkinleştirin
sudo systemctl enable dockerpanel-api.service

# Servisi başlatın
sudo systemctl start dockerpanel-api.service

# Durumunu kontrol edin
sudo systemctl status dockerpanel-api.service
```

---

## 7. Sunucu Dizin Haritalaması (Directory Mappings)

Platformun sunucu üzerinde kullandığı kritik dizinlerin fiziksel yolları ve işlevleri aşağıdaki gibidir:

| Sunucu Dizin Yolu | Açıklama | İzin Sınıfı |
| :--- | :--- | :--- |
| `/opt/dockerpanel/` | DockerPanel ana çalışma ve orkestrasyon dizini. | `dockerpanel_api:dockerpanel_api` (755) |
| `/opt/dockerpanel/projects/` | Native web projelerinin ZIP arşivlerinden çıkarıldığı dizin. | `dockerpanel_api:dockerpanel_api` (755) |
| `/opt/dockerpanel/backups/` | Veritabanı ve müşteri projelerinin yedek depolama alanı. | `dockerpanel_api:dockerpanel_api` (700) |
| `/opt/dockerpanel/mail/` | E-posta kutularının (maildir) ve yapılandırmaların depolandığı dizin. | `root:root` / `dockerpanel_api` |
| `/var/log/project-manager/` | Native projelerin çalışma ve süreç günlükleri (logs). | `dockerpanel_api:dockerpanel_api` (755) |
| `/etc/nginx/sites-available/` | Dinamik olarak üretilen subdomain vhost dosyaları. | `root:root` / `dockerpanel_api` (Sudo ile yazılır) |
| `/etc/nginx/sites-enabled/` | Aktif vhost sembolik bağlantıları (symlinks). | `root:root` / `dockerpanel_api` (Sudo ile yazılır) |
| `/etc/project-manager/projects.conf` | Native projelerin listesini ve kısıtlarını tutan INI dosyası. | `dockerpanel_api:dockerpanel_api` (644) |

---

## 8. Nginx Güvenlik Sınırlandırması ve Certbot SSL Yapılandırması

Sistem güvenliğini artırmak ve SSL sertifikası üretim süreçlerini iyileştirmek amacıyla aşağıdaki yapılandırmalar Nginx ve Certbot süreçlerine dahil edilmiştir:

### A. Nginx default_server ve API Erişim Sınırları
* **Yetkisiz Domainlerin Engellenmesi:** Sunucu IP adresini hedef alan ancak DockerPanel üzerinde tanımlı olmayan domainler veya rastgele bağlantılar için port 80 üzerinden gelen istekler Nginx `default_server` bloğu tarafından karşılanır ve **444 Connection Closed (Bağlantıyı Kapat)** yanıtı ile sonlandırılır. Bu sayede kontrol paneli API'si dış dünyaya yetkisiz şekilde ifşa edilmez.
* **İzin Verilen Hostlar (Server Names):** DockerPanel API'sine ve Blazor web paneline port 80 üzerinden sadece `localhost`, `127.0.0.1`, sunucunun tespit edilen **kamusal IP adresi** ve panele kaydedilen aktif **panel alan adları** üzerinden erişilebilir.

### B. Certbot Webroot Moduna Geçiş (Resilient SSL)
* **Webroot Challenge Dizin Entegrasyonu:** SSL doğrulamaları için Certbot'un `--nginx` yükleyicisi yerine, daha kararlı olan `certonly --webroot -w /var/www/html` eklentisi kullanılır.
* **Nginx Konfigürasyonunun Korunması:** Bu yöntemle Certbot, Nginx konfigürasyon dosyalarını doğrudan düzenlemez. Sadece doğrulama token dosyasını `/var/www/html/.well-known/acme-challenge/` altına yazar.
* **Evrensel ACME Yönlendirmesi:** DockerPanel tarafından üretilen tüm Nginx site (vhost) konfigürasyonlarına ve default sunucu bloğuna `location /.well-known/acme-challenge/` tanımı eklenerek isteklerin doğrudan `/var/www/html` dizininden sunulması sağlanır. Bu sayede proxy yönlendirmeli projeler veya HTTPS yönlendirmeli web siteleri için bile HTTP-01 doğrulamaları sıfır hata ile tamamlanır.
* **DNS Ön Kontrolleri:** Apex alan adı (`domain.com`) için SSL talep edildiğinde sistem, `www.domain.com` adresinin DNS (A/AAAA) kaydı üzerinden çözümlenip çözümlenmediğini denetler. DNS kaydı olmayan adresler SSL talebinden çıkarılarak Certbot işleminin çökmesi engellenir.


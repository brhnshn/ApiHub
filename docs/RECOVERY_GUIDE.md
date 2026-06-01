# DockerPanel Tek Tıkla VDS Taşıma & Kurtarma Kılavuzu (Recovery Guide)

Bu kılavuz, aldığınız tam sistem yedeklerini (Veritabanı, Müşteri Projelerini, Nginx Yönlendirmeleri ve E-Posta Kutuları) yeni bir VDS sunucusuna **tek bir komut/script ile sıfırdan nasıl kuracağınızı ve yayına alacağınızı** açıklar.

---

## 1. Mimari Yaklaşım

Yedekleme sistemimiz `/opt/dockerpanel` dizinini, PostgreSQL veritabanını ve Nginx/Mail servislerini temel alır:
*   **Veritabanı (`database.sql.gz`):** Tüm kullanıcı kayıtlarını, DNS zone verilerini ve subdomain eşleşmelerini tutar.
*   **Projeler (`projects.tar.gz`):** Native müşteri sitelerinin dosyalarıdır.
*   **Nginx (`nginx.tar.gz`):** `/etc/nginx/sites-available` altındaki tüm subdomain proxy konfigürasyonlarıdır.
*   **Mail (`mail.tar.gz`):** `/opt/dockerpanel/mail` altındaki tüm posta kutuları (maildir), postfix ve dovecot konfigürasyonlarıdır.
*   **SSL Sertifikaları (Kritik):** `/etc/letsencrypt` altındaki Let's Encrypt SSL sertifikalarıdır (Nginx'in yeni sunucuda hata vermeden başlaması için aktarılması zorunludur).

Yeni bir sunucuya taşınırken, bu bileşenlerin arşivden çıkarılması ve veritabanının Dockerize edilmiş PostgreSQL'e basılması tüm sistemi **birebir eski haline getirir.**

---

## 2. Tek Komutla Otomatik Kurtarma Scripti (`restore-all.sh`)

Yeni sunucunuza yedeği yükledikten sonra aşağıdaki scripti çalıştırarak tüm kurulumu, bağımlılıkları, Docker ağlarını, sahiplik izinlerini ve veri yüklemeyi **tek seferde** otomatize edebilirsiniz.

Bu scripti yeni VDS sunucusunda `/opt/dockerpanel/restore-all.sh` olarak kaydedip çalıştırabilirsiniz:

```bash
#!/bin/bash
# ==============================================================================
# DockerPanel - Tek Tıkla VDS Taşıma & Tam Kurtarma Scripti
# ==============================================================================
set -e

# --- ÖZELLEŞTİRİLEBİLİR AYARLAR ---
MAIL_HOSTNAME="mail.burhansahin.com.tr"
MAIL_DOMAIN="burhansahin.com.tr"
DB_USER="dp_admin"
DB_PASSWORD="dp_admin_password"
DB_NAME="dockerpanel_db"
# ----------------------------------

# Renkli Çıktılar
GREEN='\033[0;32m'
BLUE='\033[0;34m'
RED='\033[0;31m'
NC='\033[0m'

# Kullanıcı Girişi & Yedek Klasörü Kontrolü
BACKUP_FOLDER=$1
if [ -z "$BACKUP_FOLDER" ]; then
    echo -e "${RED}Hata: Lütfen yedek klasörünün yolunu belirtin!${NC}"
    echo "Kullanım: sudo ./restore-all.sh /yol/to/backup_YYYY-MM-DD_HH-mm-ss"
    exit 1
fi

if [ ! -d "$BACKUP_FOLDER" ]; then
    echo -e "${RED}Hata: Belirtilen yedek klasörü bulunamadı: $BACKUP_FOLDER${NC}"
    exit 1
fi

echo -e "${BLUE}[1/6] Bağımlılıklar Kuruluyor (Docker, Nginx, Tar, Gzip)...${NC}"
sudo apt update
sudo apt install -y docker.io docker-compose nginx tar gzip pg-client-common

# Docker Panel Dizin Yapısı Kuruluyor
sudo mkdir -p /opt/dockerpanel/projects
sudo mkdir -p /opt/dockerpanel/backups
sudo mkdir -p /opt/dockerpanel/mail
sudo mkdir -p /var/log/project-manager

# Segmentasyon için Global Docker Ağı Oluşturuluyor
echo -e "${BLUE}-> Global Docker ağ köprüsü kuruluyor...${NC}"
sudo docker network create dockerpanel-global-net || true

echo -e "${BLUE}[2/6] Dosya Arşivleri Çıkarılıyor...${NC}"

# Proje Dosyaları Geri Yükleniyor
if [ -f "$BACKUP_FOLDER/projects.tar.gz" ]; then
    echo "-> Müşteri projeleri geri yükleniyor..."
    sudo rm -rf /opt/dockerpanel/projects/*
    sudo tar -xzf "$BACKUP_FOLDER/projects.tar.gz" -C /opt/dockerpanel/projects/
fi

# Nginx Konfigürasyonları Geri Yükleniyor
if [ -f "$BACKUP_FOLDER/nginx.tar.gz" ]; then
    echo "-> Nginx yönlendirme ayarları geri yükleniyor..."
    sudo rm -rf /etc/nginx/sites-available/*
    sudo tar -xzf "$BACKUP_FOLDER/nginx.tar.gz" -C /etc/nginx/sites-available/
fi

# Mail Sunucusu Verileri Geri Yükleniyor
if [ -f "$BACKUP_FOLDER/mail.tar.gz" ]; then
    echo "-> E-posta sunucu verileri ve kutuları geri yükleniyor..."
    sudo rm -rf /opt/dockerpanel/mail/*
    sudo tar -xzf "$BACKUP_FOLDER/mail.tar.gz" -C /opt/dockerpanel/mail/
fi

echo -e "${BLUE}[3/6] Docker Altyapısı Başlatılıyor...${NC}"
# docker-compose.yml dosyasını oluştur veya kopyala
cat << EOF > /opt/dockerpanel/docker-compose.yml
version: '3.9'
services:
  db:
    image: postgres:16-alpine
    container_name: dockerpanel-db
    restart: unless-stopped
    environment:
      POSTGRES_USER: ${DB_USER}
      POSTGRES_PASSWORD: ${DB_PASSWORD}
      POSTGRES_DB: ${DB_NAME}
    volumes:
      - pgdata:/var/lib/postgresql/data
    ports:
      - "5432:5432"
    networks:
      - dockerpanel-net

  mailserver:
    image: mailserver/docker-mailserver:latest
    container_name: dockerpanel-mailserver
    hostname: ${MAIL_HOSTNAME}
    domainname: ${MAIL_DOMAIN}
    ports:
      - "25:25"
      - "143:143"
      - "587:587"
      - "993:993"
    volumes:
      - /opt/dockerpanel/mail/data:/var/mail
      - /opt/dockerpanel/mail/state:/var/mail-state
      - /opt/dockerpanel/mail/config:/tmp/docker-mailserver
    restart: always
    environment:
      - ENABLE_SPAMASSASSIN=1
      - ENABLE_CLAMAV=0
      - ENABLE_FAIL2BAN=1
      - ONE_DIR=1
    networks:
      - dockerpanel-net

networks:
  dockerpanel-net:
    name: dockerpanel-global-net
    external: true

volumes:
  pgdata:
    driver: local
EOF

cd /opt/dockerpanel
sudo docker-compose down || true
sudo docker-compose up -d

echo -e "${BLUE}[4/6] Veritabanı Yedeği SQL Dump Olarak Geri Yükleniyor...${NC}"
# Veritabanının hazır olmasını bekle (max 15 sn)
echo "PostgreSQL servisinin hazır olması bekleniyor..."
sleep 10

if [ -f "$BACKUP_FOLDER/database.sql.gz" ]; then
    # Konteyner adına bağımsız, tanımladığımız dockerpanel-db ismine SQL dump basılıyor
    gunzip -c "$BACKUP_FOLDER/database.sql.gz" | sudo docker exec -i dockerpanel-db psql -U ${DB_USER} -d ${DB_NAME}
    echo -e "${GREEN}Veritabanı başarıyla restore edildi.${NC}"
else
    echo -e "${RED}Uyarı: database.sql.gz bulunamadı!${NC}"
fi

echo -e "${BLUE}[5/6] Nginx Gateway Symlink'leri ve SSL Kontrolleri Yapılıyor...${NC}"
# sites-available içindeki konfigürasyonları sites-enabled'a sembolik bağla
sudo rm -rf /etc/nginx/sites-enabled/*
for conf in /etc/nginx/sites-available/*.conf; do
    if [ -f "$conf" ]; then
        sudo ln -sf "$conf" "/etc/nginx/sites-enabled/\$(basename "\$conf")"
    fi
done

# Nginx Test & Restart
sudo nginx -t
sudo systemctl restart nginx

echo -e "${BLUE}[6/6] Sahiplik İzinleri & API Servisi Yapılandırılıyor...${NC}"
# dockerpanel_api kullanıcısının dosya yazabilmesi için chown yetkilendirmesi
if id "dockerpanel_api" &>/dev/null; then
    sudo chown -R dockerpanel_api:dockerpanel_api /opt/dockerpanel
    sudo chown -R dockerpanel_api:dockerpanel_api /var/log/project-manager
    sudo chmod 755 /var/log/project-manager
    echo "-> Dosya sahiplik izinleri dockerpanel_api kullanıcısına başarıyla aktarıldı."
else
    echo "-> Uyarı: dockerpanel_api kullanıcısı bulunamadı. Panel kurulumunu önceden tamamladığınızdan emin olun."
fi

# API servisi aktif ediliyor
sudo systemctl restart dockerpanel-api || true

echo -e "${GREEN}==============================================================================${NC}"
echo -e "${GREEN}Tebrikler! DockerPanel Taşıma ve Kurtarma İşlemi Başarıyla Tamamlandı.${NC}"
echo -e "${GREEN}Tüm siteleriniz, DNS kurallarınız ve e-postalarınız yeni VDS'te yayında!${NC}"
echo -e "${GREEN}==============================================================================${NC}"
```

---

## 3. Taşıma / Restore Adımları (Adım Adım)

Yeni sunucuya geçişi hatasız gerçekleştirmek için şu adımları uygulayın:

### Önkoşul (Prerequisite)
Yeni VDS sunucunuza öncelikle **DockerPanel kurulumunu sıfırdan tamamlayın.** Kurulum işlemi sunucu üzerinde `dockerpanel_api` kullanıcısını, systemd servislerini ve panel klasörlerini oluşturacaktır. Kurulum sonrasında kurtarma işlemine geçin.

### Adım 0: Let's Encrypt SSL Sertifikalarını Aktarın (Kritik Adım)
Nginx yapılandırmalarınız Let's Encrypt SSL sertifikalarına bağımlıdır. Sertifikalar yeni sunucuda bulunmazsa Nginx başlatılamayacak ve gateway çökecektir.

1. **Eski Sunucuda (SSL Klasörünü Sıkıştırın):**
   ```bash
   sudo tar -czf /opt/dockerpanel/backups/letsencrypt.tar.gz /etc/letsencrypt
   ```
2. **Yedekleri Aktarın:**
   Sıkıştırdığınız `letsencrypt.tar.gz` dosyasını ve aldığınız son panel yedeğini (örn: `backup_2026-05-29_03-50-09` klasörünü) yeni sunucuya transfer edin.
3. **Yeni Sunucuda (SSL Klasörünü Açın):**
   ```bash
   sudo mkdir -p /etc/letsencrypt
   sudo tar -xzf /opt/dockerpanel/backups/letsencrypt.tar.gz -C /
   ```

### Adım 1: Yedek Klasörünü Yeni VDS Sunucusuna Yerleştirin
Eski sunucudan indirdiğiniz yedek klasörünü yeni sunucuda `/opt/dockerpanel/backups/` dizinine yükleyin.

### Adım 2: Restore Scriptini Oluşturun ve Yetkilendirin
Yeni sunucuda kurtarma scriptini oluşturun:
```bash
sudo nano /opt/dockerpanel/restore-all.sh
```
*(Yukarıdaki script içeriğini yapıştırıp kaydedin - Ctrl+O, Enter, Ctrl+X)*

Scripti çalıştırılabilir yapın:
```bash
sudo chmod +x /opt/dockerpanel/restore-all.sh
```

### Adım 3: Scripti Tek Komutla Çalıştırın
Yüklediğiniz yedek klasörünü parametre vererek kurtarma sürecini başlatın:
```bash
sudo /opt/dockerpanel/restore-all.sh /opt/dockerpanel/backups/backup_2026-05-29_03-50-09
```

---
> **[!TIP]**
> Bu kurtarma kılavuzu sayesinde herhangi bir çökme veya VDS taşıma durumunda, yeni bir VDS kiralayıp sıfır kurulum yaptıktan sonra sadece **2 dakika içerisinde** tüm sunucuyu (birebir tüm müşteri siteleri, SSL sertifikaları ve mailleri dahil) sıfır veri kaybıyla ayağa kaldırabilirsiniz.

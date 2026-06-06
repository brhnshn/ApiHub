#!/bin/bash
# ==============================================================================
# DockerPanel - Tek Komutla Sunucu Kurulum Scripti (install.sh)
# ==============================================================================
set -e

# Renkli Çıktılar
GREEN='\033[0;32m'
BLUE='\033[0;34m'
RED='\033[0;31m'
NC='\033[0m'

# Root Kontrolü
if [ "$EUID" -ne 0 ]; then
    echo -e "${RED}Hata: Bu script root yetkileri ile çalıştırılmalıdır!${NC}"
    echo "Kullanım: sudo ./scripts/install.sh"
    exit 1
fi

echo -e "${BLUE}=== DockerPanel Sunucu Kurulum Süreci Başlıyor ===${NC}"

# 1. Bağımlılıkların Kurulması
echo -e "${BLUE}[1/7] Sistem Paketleri Güncelleniyor ve Bağımlılıklar Kuruluyor...${NC}"
apt-get update

# Microsoft dotnet repolarını ekle (.NET 8 SDK için)
wget https://packages.microsoft.com/config/ubuntu/$(lsb_release -rs)/packages-microsoft-prod.deb -O packages-microsoft-prod.deb || \
wget https://packages.microsoft.com/config/debian/$(lsb_release -rs 2>/dev/null || echo "12")/packages-microsoft-prod.deb -O packages-microsoft-prod.deb || true

if [ -f "packages-microsoft-prod.deb" ]; then
    dpkg -i packages-microsoft-prod.deb
    rm packages-microsoft-prod.deb
    apt-get update
fi

apt-get install -y docker.io docker-compose nginx certbot unzip curl dotnet-sdk-8.0 nodejs npm

# 2. dockerpanel_api Kullanıcısı ve Sudoers Yapılandırması
echo -e "${BLUE}[2/7] Sistem Kullanıcısı ve Yetkileri Yapılandırılıyor...${NC}"
if ! id "dockerpanel_api" &>/dev/null; then
    useradd -m -s /bin/bash dockerpanel_api
    echo -e "${GREEN}-> 'dockerpanel_api' kullanıcısı oluşturuldu.${NC}"
fi

# Kullanıcıyı docker grubuna ekle
usermod -aG docker dockerpanel_api

# Sudoers İzinleri
cat << 'EOF' > /etc/sudoers.d/dockerpanel_api
dockerpanel_api ALL=(ALL) NOPASSWD: /usr/sbin/nginx -t, /usr/sbin/service nginx reload, /usr/sbin/systemctl reload nginx, /usr/bin/certbot *, /usr/local/bin/project-manager.sh *, /usr/sbin/ufw *, /usr/bin/tar *, /usr/bin/chown *, /usr/bin/rm *
EOF
chmod 440 /etc/sudoers.d/dockerpanel_api
echo -e "${GREEN}-> Sudoers izinleri (/etc/sudoers.d/dockerpanel_api) yapılandırıldı.${NC}"

# 3. Dizin Yapısının Kurulması
echo -e "${BLUE}[3/7] Panel Dizin Yapısı Kuruluyor...${NC}"
mkdir -p /opt/dockerpanel/projects
mkdir -p /opt/dockerpanel/backups
mkdir -p /opt/dockerpanel/mail
mkdir -p /var/log/project-manager

# project-manager.sh scriptini kopyala ve yetkilendir
if [ -f "scripts/project-manager.sh" ]; then
    cp scripts/project-manager.sh /usr/local/bin/project-manager.sh
    chmod +x /usr/local/bin/project-manager.sh
    echo -e "${GREEN}-> project-manager.sh /usr/local/bin/ konumuna yüklendi.${NC}"
else
    echo -e "${RED}Uyarı: scripts/project-manager.sh bulunamadı!${NC}"
fi

# 4. Güvenli Çevre Değişkenleri (.env) Üretimi
echo -e "${BLUE}[4/7] Çevre Değişkenleri ve Gizli Anahtarlar Üretiliyor...${NC}"
if [ ! -f "/opt/dockerpanel/.env" ]; then
    RANDOM_DB_PASS=$(openssl rand -hex 16)
    RANDOM_JWT_KEY=$(openssl rand -hex 32)
    
    cat << EOF > /opt/dockerpanel/.env
POSTGRES_USER=dp_admin
POSTGRES_PASSWORD=${RANDOM_DB_PASS}
POSTGRES_DB=dockerpanel_db
JWT_SECRET_KEY=${RANDOM_JWT_KEY}
ConnectionStrings__DefaultConnection="Host=localhost;Port=5432;Username=dp_admin;Password=${RANDOM_DB_PASS};Database=dockerpanel_db;"
JwtSettings__SecretKey="${RANDOM_JWT_KEY}"
JwtSettings__Issuer="DockerPanelAPI"
JwtSettings__Audience="DockerPanelClient"
EOF
    echo -e "${GREEN}-> Yeni .env dosyası oluşturuldu ve güvenli şifreler üretildi.${NC}"
else
    echo -e "${BLUE}-> Mevcut .env dosyası korundu.${NC}"
fi

# 5. Global Docker Ağı Kurulumu
echo -e "${BLUE}[5/7] Global Docker Ağı Kuruluyor...${NC}"
docker network create dockerpanel-global-net || true

# 6. Uygulamanın Derlenmesi ve Systemd Servis Kurulumu
echo -e "${BLUE}[6/7] API ve Client Uygulamaları Derleniyor...${NC}"
dotnet publish src/DockerPanel.API/DockerPanel.API.csproj -c Release -o /opt/dockerpanel/api/DockerPanel_V1

# Systemd Servis Dosyası
cat << 'EOF' > /etc/systemd/system/dockerpanel-api.service
[Unit]
Description=DockerPanel API and Client Service
After=network.target

[Service]
WorkingDirectory=/opt/dockerpanel/api/DockerPanel_V1
ExecStart=/usr/bin/dotnet DockerPanel.API.dll
Restart=always
RestartSec=10
KillSignal=SIGINT
SyslogIdentifier=dockerpanel-api
User=dockerpanel_api
EnvironmentFile=/opt/dockerpanel/.env
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://*:5000
Environment=HOME=/home/dockerpanel_api

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable dockerpanel-api
echo -e "${GREEN}-> systemd servisi kuruldu ve aktif edildi.${NC}"

# 7. Altyapı Docker Servislerinin Başlatılması
echo -e "${BLUE}[7/7] PostgreSQL ve Altyapı Servisleri Başlatılıyor...${NC}"
cp docker-compose.yml /opt/dockerpanel/docker-compose.yml
cd /opt/dockerpanel
docker-compose down || true
docker-compose up -d

# API Servisini Başlat
systemctl restart dockerpanel-api

# İzinleri Son Kez Düzenle
chown -R dockerpanel_api:dockerpanel_api /opt/dockerpanel
chown -R dockerpanel_api:dockerpanel_api /var/log/project-manager
chmod 755 /var/log/project-manager

echo -e "${GREEN}==============================================================================${NC}"
echo -e "${GREEN}Kurulum Tamamlandı! DockerPanel Başarıyla Ayağa Kaldırıldı.${NC}"
echo -e "${GREEN}API & Client Portu: http://SUNUCU_IP:5000${NC}"
echo -e "${GREEN}==============================================================================${NC}"

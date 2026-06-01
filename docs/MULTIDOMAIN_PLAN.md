# DockerPanel Çoklu Domain (Multi-Domain) Yönetimi Entegrasyon Planı

Bu döküman, DockerPanel projesinin tek bir alan adı (subdomain) odaklı yapısından sıyrılarak, tek bir VDS/VPS üzerinde **birden fazla tamamen bağımsız ana alan adını (root domain)** ve bunların alt alan adlarını (subdomain) dinamik olarak yönetebilmesini sağlayan mimari tasarımı ve refaktör adımlarını içerir.

---

## 1. Mimari Değişiklik Özeti

Şu anki veritabanı şemamız (`Subdomains` tablosu) zaten `SubdomainName` (örn: `api`) ve `DomainName` (örn: `burhansahin.com.tr`) kolonlarını ayrı ayrı tutmaktadır. Nginx yönlendirme sistemimiz (Certbot SSL dahil) aslında altyapı olarak çoklu domain desteğine hazırdır.

Ancak Cloudflare DNS entegrasyonu tarayıcı belleğinde (`localStorage`) global olarak tutulmaktadır. Çoklu domain mimarisinde her domainin Cloudflare üzerindeki **Zone ID** değeri farklı olacağından, bu verileri veri tabanında domain bazlı saklamamız gerekmektedir. Ayrıca ön yüzde domain adının el yazısı ile yazılması yerine, kayıtlı alan adlarının listelendiği açılır kutular (Dropdown / Select) entegre edilecektir.

---

## 2. Yapılacak Teknik Değişiklikler

### A. Veritabanı ve Domain Katmanı (Database & Entities)
Kullanıcının panele sahip olduğu ana alan adlarını (root domainler) ekleyebilmesi için yeni bir `RootDomain` tablosu oluşturulacaktır.

#### [NEW] [RootDomain.cs](file:///c:/Users/sahin/Desktop/cpanelproje/src/DockerPanel.Domain/Entities/RootDomain.cs)
```csharp
using System;

namespace DockerPanel.Domain.Entities;

public class RootDomain
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    
    // Örn: "burhansahin.com.tr", "yeniproje.com"
    public string Name { get; set; } = string.Empty; 
    
    // Cloudflare Entegrasyonu (Her domaine özel Zone ve Token)
    public string? CloudflareToken { get; set; }
    public string? CloudflareZoneId { get; set; }
    
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // EF Core İlişkileri
    public virtual User User { get; set; } = null!;
}
```

#### [MODIFY] [ApplicationDbContext.cs](file:///c:/Users/sahin/Desktop/cpanelproje/src/DockerPanel.Infrastructure/Data/ApplicationDbContext.cs)
- `DbSet<RootDomain> RootDomains` eklenecek.
- `OnModelCreating` içerisinde `RootDomain.Name` alanı için **Benzersizlik (Unique Constraint)** kuralı tanımlanacak.
- `User` -> `RootDomains` ilişkisi (Cascade Delete) yapılandırılacak.

---

### B. Backend API Katmanı (Controllers)

#### [NEW] [RootDomainsController.cs](file:///c:/Users/sahin/Desktop/cpanelproje/src/DockerPanel.API/Controllers/RootDomainsController.cs)
Kullanıcının ana alan adlarını yönetebilmesi için REST API uç noktaları oluşturulacak:
- `GET api/domains/roots`: Oturum açmış kullanıcının kayıtlı tüm ana alan adlarını listeler.
- `POST api/domains/roots`: Yeni bir ana alan adı (ve isteğe bağlı Cloudflare anahtarları) kaydeder.
- `DELETE api/domains/roots/{id}`: Alan adını ve ilişkili subdomain/DNS verilerini siler.

#### [MODIFY] [DnsController.cs](file:///c:/Users/sahin/Desktop/cpanelproje/src/DockerPanel.API/Controllers/DnsController.cs) ve [NginxController.cs](file:///c:/Users/sahin/Desktop/cpanelproje/src/DockerPanel.API/Controllers/NginxController.cs)
- **Akıllı Kimlik Arama:** Ön yüzden artık sorgu parametresiyle `cfToken` veya `cfZoneId` gönderilmesine gerek kalmayacak.
- Backend, işlem yapılan `DomainName` değerini (örn: `burhansahin.com.tr`) `RootDomains` tablosunda sorgulayacak, o domaine ait `CloudflareToken` ile `CloudflareZoneId` değerlerini otomatik olarak çekip Cloudflare API'sine güvenle iletecektir. Bu sayede ön yüz (client) güvenlik anahtarlarını taşımakla uğraşmayacaktır.

---

### C. Ön Yüz Katmanı (MudBlazor Frontend)

#### [MODIFY] [Domains.razor](file:///c:/Users/sahin/Desktop/cpanelproje/src/DockerPanel.Client/Pages/Domains.razor)
- **Ana Alan Adı Yönetim Paneli:** Sayfanın en üstüne "Ana Alan Adları (Root Domains)" kartı eklenecek. Kullanıcı buradan alan adlarını (Cloudflare Token ve Zone ID ile birlikte) ekleyip silebilecek.
- **Açılır Kutu Entegrasyonu (Dropdown):** Subdomain veya DNS Zone kaydı ekleme modallarında, el yazısı ile domain girilen alan kaldırılacak. Yerine sistemdeki aktif root domainlerinizi listeleyen modern bir açılır kutu (`MudSelect`) yerleştirilecek.

#### [MODIFY] [DeployWizard.razor](file:///c:/Users/sahin/Desktop/cpanelproje/src/DockerPanel.Client/Pages/DeployWizard.razor)
- **Otomatik Domain Seçimi:** Dağıtım Sihirbazı'ndaki `_domain = "burhansahin.com.tr"` statik tanımı kaldırılacak.
- Yerine, kullanıcının panele kaydettiği domainlerin listelendiği bir açılır kutu eklenecek. Kullanıcı hangi domaini seçerse, o domainin Cloudflare entegrasyonu arka planda otomatik olarak kullanılacaktır.

---

## 3. Doğrulama ve Test Adımları

1. **EF Core Veritabanı Geçişi:** `dotnet ef migrations add AddRootDomains` ve `dotnet ef database update` komutları koşturulacaktır.
2. **Virtual Host Testi:** Panele iki farklı domain (`domainA.com` ve `domainB.com`) eklenecektir. Dağıtım sihirbazında bu domainler seçilerek Nginx Virtual Host dosyalarının `/etc/nginx/sites-available/` altında `api.domainA.com.conf` ve `test.domainB.com.conf` şeklinde bağımsız olarak oluşturulduğu doğrulanacaktır.
3. **SSL (Certbot) Testi:** Her iki domain için Certbot SSL doğrulamalarının ayrı ayrı sorunsuz koşturulduğu gözlemlenecektir.

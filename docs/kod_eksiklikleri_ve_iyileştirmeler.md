# Kod Eksiklikleri ve İyileştirmeler (kod_eksiklikleri_ve_iyileştirmeler.md)

Bu döküman, DockerPanel projesinin mevcut kod tabanındaki teknik borçları (technical debt), güvenlik açıklarını, mimari eksiklikleri ve gelecekte yapılması planlanan iyileştirme önerilerini listelemektedir.

---

## 1. Birim Test Projelerinin Eksikliği (Absence of Unit Test Projects)

Mevcut durumda, clean architecture yapısına sahip olan çözüm (solution) içerisinde herhangi bir test projesi (örneğin `DockerPanel.Tests` veya `DockerPanel.UnitTests`) yer almamaktadır.
*   **Sorun:** Servislerde yapılan en ufak bir değişiklikte veya yeni özellik ekleme aşamasında regresyon testlerinin (regressions) otomatik olarak gerçekleştirilememesi, manuel test yükünü ve hata payını artırmaktadır.
*   **İyileştirme:**
    *   `xUnit` veya `NUnit` tabanlı test projeleri eklenmeli.
    *   Özellikle kritik güvenlik mekanizmaları (Zip Slip koruması, Regex parametre kontrolleri, SQL parametre bağlamaları) için kapsamlı birim testler yazılmalı.
    *   Docker API ve SSH operasyonları için `Moq` veya `NSubstitute` gibi kütüphaneler kullanılarak mock testleri kurgulanmalı.

---

## 2. Yerel Token Güvenliği Farklılıkları (Local Token Security Differences)

Token saklama stratejilerindeki güvenlik seviyesi, istemci platformuna göre farklılık göstermektedir:
*   **Blazor WebAssembly (WASM):** JWT token'ları tarayıcının yerel depolama alanında (`localStorage`) düz metin olarak saklanmaktadır. Bu durum, olası bir Siteler Arası Betik Çalıştırma (XSS) saldırısında token'ın çalınması riskini doğurmaktadır.
*   **MAUI Mobile App:** Mobil uygulamada token'lar `SecureStorage` (Android Keystore / iOS Keychain) kullanılarak şifreli ve izole bir şekilde saklanmaktadır.
*   **İyileştirme:** 
    *   Blazor WASM tarafında token'ların doğrudan `localStorage` yerine, XSS saldırılarına karşı daha korunaklı olan `httpOnly` ve `secure` işaretli çerezler (cookies) aracılığıyla saklanması ve yönetilmesi mimarisine geçiş düşünülmelidir.

---

## 3. Sabit Kodlanmış Yapılandırma Bilgileri (Hardcoded Configurations & Credentials)

Projede, üçüncü parti entegrasyonlar için gereken bazı hassas ayarlar ve kimlik bilgileri statik dosyalarda veya kaynak kodda yer alabilmektedir:
*   **Sorun:** Firebase admin SDK yapılandırmaları, SMTP şifreleri veya Cloudflare API anahtarları gibi hassas bilgilerin `appsettings.json` veya kaynak kod dosyalarında statik olarak yer alması, kodun kamuya açık repolara (public repositories) sızması durumunda ciddi güvenlik açığı oluşturur.
*   **İyileştirme:**
    *   Hassas ayarların tamamı sunucudaki Çevre Değişkenleri (Environment Variables) üzerinden okunacak şekilde güncellenmelidir.
    *   Geliştirme ortamları için .NET `User Secrets` (Kullanıcı Sırları) mekanizması kullanılmalıdır.

---

## 4. Tek Kullanıcılı Tasarım Kısıtlamaları (Single-User Design Constraints)

Uygulama, kişisel kullanım ve tek kullanıcı odaklı olarak tasarlanmıştır.
*   **Sorun:** `Users` tablosunda rol yapısı tanımlanmış olsa da, sistem kaynakları (veritabanları, projeler, mail hesapları, vhost'lar) sunucu genelinde izole edilmemiş olup dolaylı olarak paylaşımlı kullanılmaktadır. Uygulamanın gelecekte birden fazla bağımsız kullanıcıya (multi-tenant) hizmet vermesi istendiğinde, tüm veritabanı şeması ve servis katmanı baştan aşağı refaktör edilmek zorundadır.
*   **İyileştirme:**
    *   Gelecekte çoklu kullanıcı desteği hedeflendiğinde, veri tabanındaki tüm tablolarda `TenantId` kolonları oluşturulmalı ve veritabanı seviyesinde sorgu filtreleri (Global Query Filters) eklenmelidir.
    *   Sunucu düzeyinde her tenant için ayrı bir klasör yapısı (`/opt/dockerpanel/tenants/[tenant_id]/`) kurgulanmalıdır.

---

## 5. Diğer Teknik Borçlar ve İyileştirme Alanları

*   **Hız Sınırlama (Rate Limiting) Eksikliği:** API uç noktalarında, özellikle giriş (`api/auth/login`) ve dosya indirme (`api/downloads/apk`) noktalarında istek sınırlama (Rate Limiting / Throttling) mekanizması bulunmamaktadır. Bu durum sunucuyu kaba kuvvet (brute force) ve DoS saldırılarına açık hale getirmektedir.
*   **Yedekleme İşlemlerinde Yüksek İşlemci (CPU) Kullanımı:** Dosya sıkıştırma (`tar.gz`) ve veritabanı dump işlemlerinin tek iş parçacığında senkronize şekilde uzun sürmesi, sistem kaynaklarını anlık olarak tüketebilir. Sıkıştırma işlemlerinin asenkron ve düşük öncelikli (nice değeri ayarlanarak) alt süreçler halinde koşturulması performans açısından faydalı olacaktır.
*   **Geri Alım (Rollback) Eksiklikleri:** Nginx konfigürasyon değişiklikleri veya veritabanı oluşturma işlemleri yarıda kaldığında, kısmi olarak oluşturulan dosyaların veya veritabanı kullanıcılarının temizlenmesi tam otomatik olarak yapılamayabilir. Hata durumlarında (exception) tüm sürecin başladığı ana geri dönmesini sağlayan bütüncül bir Transaction / Rollback mekanizması kurulmalıdır.

# Original User Request

## Initial Request — 2026-06-05T01:07:09+03:00

Bütün projeyi okuyup anlayarak docs/ klasöründeki tüm dökümanları temizlemek, yenilemek, güncellemek ve hepsini Türkçe diline çevirmek. Gerekli görülürse yeni dökümanlar eklemek ve projedeki eksiklikleri/iyileştirilmesi gereken yerleri raporlayan ayrı bir doküman oluşturmak.

Working directory: c:\Users\sahin\Desktop\cpanelproje
Integrity mode: development

## Requirements

### R1. Kod Tabanının Analizi ve Anlaşılması
`src/` altındaki kaynak kodları, Docker yapılandırmalarını (`docker-compose.yml` vb.) ve `DockerPanel.sln` çözüm dosyasını analiz ederek projenin mimarisini, bileşenlerini ve nasıl çalıştığını tam olarak kavrayın.

### R2. Mevcut Dokümanların Güncellenmesi ve Türkçeleştirilmesi
`docs/` klasöründeki tüm dosyaları (ARCHITECTURE.md, AGENTS.md, RECOVERY_GUIDE.md, mobil uygulama.md vb.) gözden geçirin. İçeriğindeki eski bilgileri temizleyin, güncel kod yapısına göre yenileyin ve tamamını anlaşılır, profesyonel bir Türkçe diline çevirin/güncelleyin.

### R3. Gerekli Yeni Dokümanların Oluşturulması
Projede eksik olduğu düşünülen mimari özet, kurulum adımları veya API referansları gibi kritik konular için docs/ altında yeni Türkçe `.md` dokümanları oluşturun.

### R4. Kod Tabanındaki Eksiklikler ve Yenilenmesi Gereken Yerler Dokümanı
Kod analiziniz sırasında tespit ettiğiniz eksiklikleri, teknik borçları veya refaktör gerektiren kısımları içeren `docs/kod_eksiklikleri_ve_iyileştirmeler.md` adında özel bir Türkçe doküman hazırlayın.

## Acceptance Criteria

### Dokümantasyon Kalitesi ve Dil Standartları
- [ ] `docs/` klasöründeki tüm dokümanlar tamamen Türkçe dilindedir. İngilizce kalan terimler (kod değişkenleri, özel API isimleri vb. hariç) Türkçeye uygun şekilde açıklanmıştır veya çevrilmiştir.
- [ ] Dökümanlardaki tüm teknik bilgiler (kurulum komutları, sınıf isimleri, mimari katmanlar) projenin güncel kaynak koduyla %100 uyumludur.
- [ ] Kod tabanındaki eksiklikleri, teknik borçları ve geliştirme tavsiyelerini içeren `docs/kod_eksiklikleri_ve_iyileştirmeler.md` dokümanı Türkçe hazırlanmıştır.
- [ ] Hiçbir dokümanda geçici şablon metinler (placeholder), TODO veya tamamlanmamış bölümler bulunmamaktadır.
- [ ] Yeni oluşturulan ve güncellenen dokümanlar temiz bir markdown biçimlendirmesine (başlık hiyerarşisi, kod blokları, listeler) sahiptir.

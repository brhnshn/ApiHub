# ApiHub (DockerPanel) Yönetim Paneli Tasarım Belgesi (DESIGN.md)

Bu belge, **ApiHub** projesinin kullanıcı arayüzü (UI) ve kullanıcı deneyimi (UX) tasarım standartlarını, HTML yapılarını, CSS/Tailwind CSS konfigürasyonlarını, tipografi kurallarını ve görsel bileşen şemalarını standartlaştırmak amacıyla hazırlanmıştır.

---

## 1. Genel Tasarım Felsefesi (UI/UX Felsefesi)

ApiHub, kullanıcılara modern bir bulut ve API yönetim arayüzü sunarken hem premium hem de teknik olarak doyurucu bir görsel dil kullanır:

* **Siber-Glassmorphic Tasarım:** Yumuşak saydamlıklar, hafif arka plan bulanıklıkları (`backdrop-blur-md`) ve mikro neon vurgularla zenginleştirilmiş modern cam kart katmanları.
* **Ferah Tipografi & Yerleşim:** Arayüzdeki veri yoğunluğunu rahatça okutabilmek ve sıkışıklığı önlemek için global yazı boyutu `13px` tabanına oturtulmuş, esnek (responsive) grid sistemleri tercih edilmiştir.
* **Canlı Durum Geri Bildirimleri (Micro-Animations):** Çalışan API'lerin, konteynerlerin veya servislerin anlık durumları animasyonlu nabız (`pulse`) efektleriyle kullanıcıya hissettirilir.
* **Developer Terminal Estetiği:** Sistem logları, Docker çıktıları ve konsol verileri; monospaced yazı tipleri, derin siyah arka planlar ve zümrüt yeşili terminal yazıları içeren retro-modern CLI kutularında sunulur.

---

## 2. Teknoloji ve Tasarım Yığını (Design Stack)

* **Framework & HTML:** Blazor WebAssembly (.NET 8/9) bileşenleri ile yapılandırılmış dinamik HTML5 mimarisi.
* **Bileşen Kütüphanesi:** MudBlazor (Material Design tabanlı form elemanları, modallar ve veri tabloları).
* **CSS Çatısı:** Tailwind CSS (v3+) - Sayfa içi esnek yerleşimler, flexbox/grid sistemleri ve dinamik stil yönetimi.
* **Tipografi (Google Fonts):**
    * *Ana Metin ve Başlıklar:* `Outfit` & `Inter` (Geometrik, modern ve yüksek okunurluklu sans-serif).
    * *Kod ve Terminal Çıktıları:* `Fira Code` & `JetBrains Mono` (Geliştirici dostu, ligatür destekli monospaced).
* **İkon Setleri:**
    * `Material Symbols Outlined` (İnce çizgili, modern ve minimalist ikonlar).
    * `FontAwesome Icons (v6.4.0)` (Sosyal medya ve genel araç ikonları).

---

## 3. Renk Paleti ve Tema Değişkenleri

Uygulamanın CSS değişkenleri (`:root`), sistem altyapısı ve yönetim paneli ile tam uyumlu olacak şekilde Zümrüt Yeşili, Asil Yakut ve Koyu Lacivert tonlarında hibrit bir yapıda kurulmuştur:

| Değişken Adı | Renk Kodu / Değeri | Açıklama |
| :--- | :--- | :--- |
| `--bg-main` | `#f8f9ff` | Açık mavi-gri genel sayfa arka planı |
| `--bg-card` | `#ffffff` | Saf beyaz cam-kart ve içerik paneli arka planı |
| `--bg-sub` | `#eff4ff` | Yan menüler (Sidebar) ve alt konteyner arka planları |
| `--border-color` | `rgba(187, 202, 191, 0.4)` | Yumuşak yeşil-gri (`#bbcabf`) çerçeveler |
| `--text-main` | `#0b1c30` | Koyu lacivert ana metinler (Maksimum okunabilirlik) |
| `--text-secondary`| `#3c4a42` | İkincil metinler (Muted yeşil-slate) |
| `--text-muted` | `#6c7a71` | Soluk açıklama, tarih ve detay yazıları |
| `--accent-primary`| `#006c49` | Birincil Zümrüt Yeşili marka aksan rengi |
| `--accent-success`| `#006c49` | Aktif, çalışan veya sağlıklı durum vurgusu |
| `--accent-danger` | `#be123c` | Durmuş, hatalı veya kritik durum/silme vurgusu |

---

## 4. Tailwind CSS Özel Konfigürasyonu (`tailwind.config.js`)

Panel bileşenlerinin keskin hatlardan kurtulması ve tutarlı bir boşluk hiyerarşisine sahip olması için aşağıdaki özel değerler tanımlanmıştır:

```javascript
module.exports = {
  theme: {
    extend: {
      borderRadius: {
        'DEFAULT': '1rem',   // 16px - Genel kartlar ve aksiyon kutuları
        'lg': '2rem',        // 32px - Dashboard istatistik kartları
        'xl': '3rem',        // 48px - Çok geniş dairesel estetik alanlar
      },
      spacing: {
        'content-padding': '1.5rem', // 24px - Standart iç boşluk
        'sidebar-width': '260px',     // Sol menü sabit genişliği
        'list-width': '400px',        // Filtre panelleri genişliği
        'container-gap': '1rem',      // Kartlar arası standart boşluk
      }
    },
  },
}
```

---

## 5. CSS ve Animasyon Mekanizmaları

### A. Yükleme ve Neon Efektleri (`app.css`)
Giriş ekranlarında ve veri yükleme süreçlerinde kullanılan global animasyonlar:

```css
/* Ağır dönen çarklar/ikonlar için */
.spin-slow {
  animation: spin 3s linear infinite;
}

/* Yumuşak nefes alma efekti */
.pulse-soft {
  animation: pulse-soft-anim 2s ease-in-out infinite;
}

@keyframes pulse-soft-anim {
  0%, 100% { opacity: 1; }
  50% { opacity: 0.4; }
}

/* Siber neon ışık dalgalanması */
.neon-glow {
  animation: neon-glow-anim 2s ease-in-out infinite;
}

@keyframes neon-glow-anim {
  0%, 100% { text-shadow: 0 0 10px rgba(0, 108, 73, 0.3); }
  50% { text-shadow: 0 0 15px rgba(0, 108, 73, 0.7); }
}
```

### B. Canlı Durum Göstergeleri (Micro-Animations)
Konteyner ve API durum noktaları (Status Dots) için mikro efektler:

```css
/* Çalışıyor - Canlı Yeşil Dalga */
.pulse-green {
  position: relative;
  background-color: var(--accent-success);
}
.pulse-green::after {
  content: '';
  position: absolute;
  width: 100%;
  height: 100%;
  top: 0;
  left: 0;
  border-radius: 50%;
  animation: pulse-green-keyframes 1.8s cubic-bezier(0.24, 0, 0.38, 1) infinite;
  box-shadow: 0 0 0 4px rgba(0, 108, 73, 0.5);
}

@keyframes pulse-green-keyframes {
  0% { transform: scale(1); opacity: 1; }
  100% { transform: scale(2.5); opacity: 0; }
}

/* Durduruldu - Sabit Parlayan Kırmızı */
.pulse-red {
  background-color: var(--accent-danger);
  box-shadow: 0 0 8px var(--accent-danger);
}
```

### C. Geliştirici Terminal Tasarımı (`.cyber-terminal`)
Sistem loglarının aktığı CLI panel simülasyonu:

```css
.cyber-terminal {
  background-color: #04020a;
  color: var(--accent-success);
  font-family: 'Fira Code', 'JetBrains Mono', monospace;
  font-size: 12px;
  border: 1px solid var(--border-color);
  box-shadow: inset 0 0 20px rgba(0, 0, 0, 0.9);
  border-radius: 0.5rem;
  padding: 1rem;
  overflow-y: auto;
}
```

### D. Modallar ve Cam Efekti (Overlay Glassmorphism)
Modal pencereler açıldığında arkada kalan alanın siber-glassmorphic tasarımı:

```css
.modal-overlay {
  position: fixed;
  inset: 0;
  background: rgba(11, 28, 48, 0.45);
  backdrop-filter: blur(6px) !important;
  z-index: 50;
}
```

---

## 6. Standart Bileşen Şemaları (Tailwind Boilerplates)

### A. Siber-Glassmorphic Konteyner Kartı
Aşağıdaki HTML şablonu, paneldeki mikro servis veya konteynerlerin gösteriminde kullanılan standart kart yapısıdır:

```html
<div class="bg-white/80 backdrop-blur-md border border-[var(--border-color)] rounded p-content-padding shadow-sm hover:shadow-md transition-all">
  <div class="flex items-center justify-between">
    <h3 class="text-[13px] font-semibold text-[var(--text-main)]">api-gateway-service</h3>
    <span class="w-2.5 h-2.5 rounded-full pulse-green"></span>
  </div>
  <p class="text-xs text-[var(--text-secondary)] mt-2">Port: 8080 | CPU: %1.2</p>
</div>
```

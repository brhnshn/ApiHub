# Explorer 3 Report: Native Projelerin İstenmeyen Yeniden Başlatılmasının Kesin Çözümü (Requirement R2)

## 1. Yönetici Özeti (Executive Summary)
Bu rapor, APIHub servisi yeniden başlatıldığında veya çalışırken Native (yerel) projelerin kontrol dışı / istenmeyen şekilde yeniden başlatılması sorununu (Gereksinim R2) analiz etmekte ve kesin çözümü için bir düzeltme stratejisi sunmaktadır. 
Yapılan incelemelerde iki temel zafiyet tespit edilmiştir:
1. **Başlangıç Durumu Uyumsuzluğu (Startup Status Mismatch)**: APIHub başlatılırken `DatabaseSyncHelper`, Native projelerin veritabanındaki durumlarını işletim sistemindeki gerçek süreç listesiyle (veya PID dosyalarıyla) senkronize etmemektedir. Bu durum, veri tabanında `Running` görünen ancak gerçekte kapalı olan projelerin tespit edilememesine neden olur.
2. **Yarış Durumu ve Bayat Veri Üzerine Yazma (Race Condition & Stale Data Overwrite)**: `MetricBackgroundWorker` watchdog mekanizması, projeleri başlangıçta aldığı `AsNoTracking` listesinden kontrol etmektedir. Kullanıcı UI üzerinden projeyi durdurduğunda veritabanı `Stopped` olur, ancak worker'ın elindeki liste bayat olduğu için watchdog bunu çökme zannedip projeyi zorla yeniden başlatmaktadır. Ayrıca, tüm entity `Modified` olarak güncellendiğinden, aradaki kullanıcı limit güncellemeleri gibi veriler ezilmektedir.

---

## 2. Bulgular ve Kod Analizi (Observations)

### Bulgular 1: `DatabaseSyncHelper.cs`
- **Dosya Yolu**: `src\DockerPanel.API\Helpers\DatabaseSyncHelper.cs`
- **İlgili Satırlar**: 94-141 (Mevcut projelerin senkronizasyon döngüsü)
- **Gözlem**: 
  `DatabaseSyncHelper.SyncExistingSystemDataAsync` metodu, sistem başlangıcında çağrılır ve `projects.conf` dosyasını okuyup veritabanındaki projelerle eşleştirir. Ancak, veritabanında zaten kayıtlı olan Native projeler için işletim sistemi düzeyinde sürecin çalışıp çalışmadığını kontrol eden herhangi bir doğrulama yapmaz:
  ```csharp
  if (project.Type == ProjectType.NativeProject)
  {
      if (config.TryGetValue(project.Name, out var details) && ...)
      {
          // Sadece metadata güncelleniyor, OS çalışma durumu kontrol edilmiyor!
          if (project.Status == ProjectStatus.Running && !project.StartedAt.HasValue) { ... }
          if (project.InternalPort != port || project.ImageOrPath != targetPath) { ... }
      }
      else {
          // Sadece projects.conf'ta yoksa durdurulduya çekiliyor
          project.Status = ProjectStatus.Stopped;
      }
  }
  ```
- **Sonuç**: APIHub veya sunucu yeniden başladığında, veritabanında `Running` olarak kalmış olan durdurulmuş veya çökmüş projeler `Running` olarak kalmaya devam eder.

---

### Bulgular 2: `MetricBackgroundWorker.cs`
- **Dosya Yolu**: `src\DockerPanel.API\Workers\MetricBackgroundWorker.cs`
- **İlgili Satırlar**: 104-107, 197-264 ve 292-296
- **Gözlem**:
  1. **Stale Snapshot (Bayat Veri Listesi)**: Worker, aktif projeleri döngünün başında toplu olarak çeker:
     ```csharp
     var activeProjects = dbContext.Projects
         .AsNoTracking()
         .Where(p => p.Status == ProjectStatus.Running)
         .ToList();
     ```
     Bu liste hafızaya alındıktan sonra, döngü sırayla her bir projeyi işlerken (ki aralarda `Task.Delay(2000)` gibi beklemeler de olabilmektedir), kullanıcı UI üzerinden projeyi durdurursa, veritabanındaki durum `Stopped` olur ve süreç durdurulur. Ancak worker döngüsü bu projeye ulaştığında elindeki eski listeye göre durumun `Running` olması gerektiğini düşünür. Süreci kontrol ettiğinde çalışmadığını görür (`IsProcessRunningAsync` -> `false`) ve watchdog bunu bir çökme olarak algılayıp `StartProcessAsync` çağırarak projeyi **istenmeyen şekilde yeniden başlatır**.
  2. **Güvensiz Toplu Güncelleme (Bulk Overwrite)**: Watchdog bir durum değişikliği yaptığında entity'i veritabanına şu şekilde kaydeder:
     ```csharp
     if (projectStateChanged)
     {
         dbContext.Entry(project).State = EntityState.Modified;
         await dbContext.SaveChangesAsync(stoppingToken);
     }
     ```
     `AsNoTracking` ile yüklenen `project` nesnesi tüm kolonları ile güncellendiği için, worker çalışırken kullanıcının yaptığı port, yol veya kaynak limiti güncellemeleri bayat `project` verisiyle ezilerek kaybolur.

---

### Bulgular 3: `ProcessManagerService.cs` ve `project-manager.sh`
- **Dosya Yolu**: `src\DockerPanel.Infrastructure\Services\ProcessManagerService.cs`
- **İlgili Satırlar**: 631-713 (`IsProcessRunningAsync` metodu)
- **Gözlem**: Durum kontrolü için `sudo -n /usr/local/bin/project-manager.sh status {name}` komutunu çağırır. Script ise `/run/project-manager/{name}.pid` dosyasını kontrol eder ve PID hayattaysa (`kill -0`) veya `ps` listesinde eşleşme bulursa projenin çalıştığı bilgisini döner. Bu mekanizma son derece tutarlıdır ve OS seviyesindeki gerçeği yansıtır.

---

## 3. Mantık Zinciri (Logic Chain)
1. APIHub çöktüğünde veya sunucu reboot edildiğinde, veritabanındaki Native projenin durumu `Running` olarak kalır.
2. APIHub yeniden başladığında `DatabaseSyncHelper.SyncExistingSystemDataAsync` çalışır, ancak OS seviyesinde durmuş olan projenin durumunu veritabanında `Stopped` yapmaz; `Running` olarak bırakır.
3. `MetricBackgroundWorker` döngüsü başlar ve veritabanında durumu `Running` olan bu projeyi `activeProjects` listesine alır.
4. Watchdog kontrol adımı (her 15 saniyede bir) tetiklenir.
5. OS seviyesinde süreç çalışmadığı için `IsProcessRunningAsync` metodu `false` döner.
6. Watchdog, `failures < 3` olduğu için `processManagerService.StartProcessAsync` çağrısını tetikler.
7. Sonuç: **Proje kontrolsüz ve istenmeyen şekilde yeniden başlatılmış olur.**
8. Aynı mantık, sistem çalışırken kullanıcının UI üzerinden projeyi durdurması durumunda da yarış durumuna (race condition) sebep olur ve watchdog projeyi geri ayağa kaldırır.

---

## 4. Önerilen Düzeltme Stratejisi (Proposed Fix Strategy)

### A. `DatabaseSyncHelper.cs` Güncellemesi
Eşitleme sırasında, veritabanındaki her bir Native projenin işletim sisteminde gerçekten çalışıp çalışmadığı `IProcessManagerService` yardımıyla sorgulanmalı ve durum veritabanında eşitlenmelidir.

**Gereken using ifadesi:**
```csharp
using DockerPanel.Domain.Interfaces;
```

**Eşitleme Döngüsü Değişikliği (Satır ~117'den sonra):**
```csharp
                        if (project.Status == ProjectStatus.Running && !project.StartedAt.HasValue)
                        {
                            project.StartedAt = project.CreatedAt;
                            Console.WriteLine($"[Sync] Çalışan Native proje için eksik StartedAt geçmiş CreatedAt ile tamamlandı: {project.Name}");
                        }

                        if (project.InternalPort != port || project.ImageOrPath != targetPath)
                        {
                            project.InternalPort = port;
                            project.ImageOrPath = targetPath;
                            Console.WriteLine($"[Sync] Mevcut Native proje metadata güncellendi: {project.Name} (Port: {port})");
                        }

                        // --- YENİ: OS ve Veritabanı Durum Hizalaması ---
                        var processManager = scope.ServiceProvider.GetService<IProcessManagerService>();
                        if (processManager != null)
                        {
                            bool isRunning = await processManager.IsProcessRunningAsync(project.Name);
                            if (isRunning)
                            {
                                if (project.Status != ProjectStatus.Running)
                                {
                                    project.Status = ProjectStatus.Running;
                                    project.StartedAt = DateTimeOffset.UtcNow;
                                    Console.WriteLine($"[Sync] OS üzerinde aktif çalışan Native proje veritabanında Running yapıldı: {project.Name}");
                                }
                            }
                            else
                            {
                                if (project.Status == ProjectStatus.Running || project.Status == ProjectStatus.Provisioning)
                                {
                                    project.Status = ProjectStatus.Stopped;
                                    project.StartedAt = null;
                                    Console.WriteLine($"[Sync] OS üzerinde çalışmayan Native proje veritabanında Stopped yapıldı: {project.Name}");
                                }
                            }
                        }
```

---

### B. `MetricBackgroundWorker.cs` Güncellemesi
Döngü esnasında yarış durumlarını önlemek için, her bir proje analiz edilmeden hemen önce veritabanından güncel durumu çekilmeli ve veritabanı güncellemeleri diğer kolonları ezmeyecek şekilde izole edilmelidir.

**1. Döngü Başı Yarış Durumu Koruması (Satır ~110'dan sonra):**
```csharp
                    foreach (var project in activeProjects)
                    {
                        try
                        {
                            // YENİ: Watchdog kontrolünden önce veritabanındaki güncel durumu kontrol et (Stale snapshot / Yarış durumu koruması)
                            var dbProject = await dbContext.Projects
                                .AsNoTracking()
                                .FirstOrDefaultAsync(p => p.Id == project.Id, stoppingToken);

                            if (dbProject == null || dbProject.Status != ProjectStatus.Running)
                            {
                                _logger.LogInformation("[Watchdog] Proje {ProjectName} ({ProjectId}) veritabanında artık Running değil. Watchdog atlanıyor.", project.Name, project.Id);
                                continue;
                            }

                            var projectStateChanged = false;
                            double cpu = 0;
```

**2. Güvenli Kolon Bazlı Güncelleme (Satır ~292'den sonra):**
```csharp
                            if (projectStateChanged)
                            {
                                // YENİ: Sadece değişen durum ve başlangıç zamanı kolonlarını güncelleyelim.
                                // Böylece AsNoTracking ile çekilmiş bayat verinin veritabanındaki diğer kolonları (limitler vb.) ezmesi engellenir.
                                var entityToUpdate = await dbContext.Projects.FindAsync(new object[] { project.Id }, stoppingToken);
                                if (entityToUpdate != null)
                                {
                                    entityToUpdate.Status = project.Status;
                                    entityToUpdate.StartedAt = project.StartedAt;
                                    await dbContext.SaveChangesAsync(stoppingToken);
                                }
                            }
```

---

## 5. Doğrulama Metodu (Verification Method)
Değişikliklerin başarısını test etmek için aşağıdaki adımlar izlenebilir:

1. **Başlangıç Senaryosu Doğrulaması (Startup Alignment)**:
   - Bir Native projesini API üzerinden başlatın (Veritabanında `Running` olur).
   - İşletim sistemi üzerinden süreci el ile sonlandırın (`project-manager.sh stop <name>` veya `kill`).
   - APIHub servisini yeniden başlatın (`dotnet run` veya `systemctl restart apihub`).
   - Logları ve veritabanını kontrol edin: Projenin durumu veritabanında otomatik olarak `Stopped` durumuna çekilmeli ve watchdog projeyi yeniden başlatmaya çalışmamalıdır.

2. **Durdurma Sırasındaki Yarış Durumu Doğrulaması (Stop Race Condition)**:
   - Watchdog kontrol aralığının (15 saniye) tetikleneceği zamana yakın bir anda UI üzerinden veya API'den projeyi durdurun (`POST /api/projects/{id}/stop`).
   - Watchdog'un projeyi tekrar ayağa kaldırmaya çalışmadığını ve durumun `Stopped` olarak korunduğunu doğrulayın.

using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using DockerPanel.Domain.Interfaces;
using DockerPanel.Domain.Security;
using DockerPanel.Domain.Entities;

namespace DockerPanel.Infrastructure.Services;

public class ProjectZipDeployService : IProjectZipDeployService
{
    private const string ProjectsRoot = "/opt/dockerpanel/projects";

    public async Task<string> DeployZipAsync(string name, Stream zipStream)
    {
        // 1. Regex Girdi Doğrulama (Directory / Command Injection Önleme)
        InputValidator.ThrowIfInvalidProjectName(name, "Proje adı sadece küçük harf, rakam, tire (-) ve alt çizgi (_) içerebilir!");

        // Windows simülasyonu için veya gerçek Linux yolu için klasör ayarlayalım
        string targetDir = Path.Combine(ProjectsRoot, name);
        
        // Eğer yerel test aşamasındaysak ve root dizin yazılabilir değilse, çalışma dizini içinde oluştur
        if (Path.DirectorySeparatorChar == '\\')
        {
            // Windows üzerinde test ortamı
            targetDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dockerpanel_projects", name);
        }

        // Read existing .env if it exists to prevent losing database/app credentials on redeployment
        string? envContent = null;
        string envPath = Path.Combine(targetDir, ".env");
        if (File.Exists(envPath))
        {
            try
            {
                envContent = await File.ReadAllTextAsync(envPath);
            }
            catch { }
        }

        // Hedef dizini temizle/oluştur (dizinin kendisini silmeyerek izinlerin korunmasını sağlıyoruz)
        if (!Directory.Exists(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }
        else
        {
            CleanDirectoryContents(targetDir);
            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }
        }

        // ZIP çıkarma işlemini güvenli (Zip Slip korumalı) gerçekleştir
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true))
        {
            string destinationFullPath = Path.GetFullPath(targetDir) + Path.DirectorySeparatorChar;

            foreach (var entry in archive.Entries)
            {
                // Windows'ta oluşturulan zip dosyalarındaki ters eğik çizgileri Linux için düz eğik çizgiye çevir
                string entryFullName = entry.FullName.Replace('\\', '/');

                // Boş klasörler
                if (string.IsNullOrEmpty(entry.Name) && (entryFullName.EndsWith("/") || entryFullName.EndsWith("\\")))
                {
                    string dirPath = Path.GetFullPath(Path.Combine(targetDir, entryFullName));
                    if (!dirPath.StartsWith(destinationFullPath, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException($"Güvenlik Uyarısı: Zip Slip (Directory Traversal) teşebbüsü engellendi! Geçersiz Klasör: {entry.FullName}");
                    }
                    Directory.CreateDirectory(dirPath);
                    continue;
                }

                string fileFullPath = Path.GetFullPath(Path.Combine(targetDir, entryFullName));

                // Zip Slip Kontrolü: Çıkarılan dosya kesinlikle hedef klasörün altında olmalı
                if (!fileFullPath.StartsWith(destinationFullPath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Güvenlik Uyarısı: Zip Slip (Directory Traversal) teşebbüsü engellendi! Geçersiz Yol: {entry.FullName}");
                }

                // Ebeveyn klasörün var olduğundan emin ol
                string? parentDir = Path.GetDirectoryName(fileFullPath);
                if (parentDir != null && !Directory.Exists(parentDir))
                {
                    Directory.CreateDirectory(parentDir);
                }

                // Dosyayı diske yaz
                using (var entryStream = entry.Open())
                using (var fileStream = new FileStream(fileFullPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                {
                    await entryStream.CopyToAsync(fileStream);
                }
            }
        }

        // ZIP iç içe klasör unwrap/flattening mantığı
        try
        {
            var files = Directory.GetFiles(targetDir);
            var subdirs = Directory.GetDirectories(targetDir);
            
            var rootFiles = files.Where(f => !Path.GetFileName(f).Equals(".env", StringComparison.OrdinalIgnoreCase)).ToList();
            if (rootFiles.Count == 0 && subdirs.Length == 1)
            {
                var singleSubdir = subdirs[0];
                foreach (var dir in Directory.GetDirectories(singleSubdir))
                {
                    var destDir = Path.Combine(targetDir, Path.GetFileName(dir));
                    if (Directory.Exists(destDir))
                    {
                        Directory.Delete(destDir, true);
                    }
                    Directory.Move(dir, destDir);
                }
                foreach (var file in Directory.GetFiles(singleSubdir))
                {
                    var destFile = Path.Combine(targetDir, Path.GetFileName(file));
                    if (File.Exists(destFile))
                    {
                        File.Delete(destFile);
                    }
                    File.Move(file, destFile);
                }
                Directory.Delete(singleSubdir, true);
            }
        }
        catch (Exception ex)
        {
            try
            {
                SystemLogQueue.Log("warning", $"[Zip Deploy] İç içe klasör ayıklama hatası: {ex.Message}");
            }
            catch {}
        }

        // Restore .env file if it was backed up
        if (envContent != null)
        {
            try
            {
                await File.WriteAllTextAsync(envPath, envContent);
            }
            catch { }
        }

        return targetDir;
    }

    private void CleanDirectoryContents(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;

        // Sürecin durmasından hemen sonra dosya kilitlerinin açılması için kısa bir süre bekleyelim
        System.Threading.Thread.Sleep(500);

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var directory = new DirectoryInfo(path);
                
                // Salt okunur özniteliklerini temizle
                foreach (var file in directory.GetFiles("*", SearchOption.AllDirectories))
                {
                    try
                    {
                        if (file.IsReadOnly) file.IsReadOnly = false;
                    }
                    catch { }
                }

                foreach (var dir in directory.GetDirectories("*", SearchOption.AllDirectories))
                {
                    try
                    {
                        dir.Attributes &= ~FileAttributes.ReadOnly;
                    }
                    catch { }
                }

                // Alt klasörlerdeki tüm dosyaları teker teker silmeyi dene
                foreach (var file in directory.GetFiles("*", SearchOption.AllDirectories))
                {
                    if (file.Name.Equals(".env", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        file.Delete();
                    }
                    catch { }
                }

                // Klasörleri sil (.env dosyasını korumak için doğrudan dizin silmek yerine alt klasörleri siliyoruz)
                foreach (var dir in directory.GetDirectories())
                {
                    try
                    {
                        dir.Delete(true);
                    }
                    catch { }
                }

                // Kök dizindeki kalan dosyaları (.env hariç) temizle
                foreach (var file in directory.GetFiles())
                {
                    if (file.Name.Equals(".env", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        file.Delete();
                    }
                    catch { }
                }

                // Eğer klasör temizlendiyse döngüden çık
                if (IsDirectoryClean(path))
                {
                    return;
                }
            }
            catch
            {
                if (attempt == 3) break;
                System.Threading.Thread.Sleep(300 * attempt);
            }
        }

        // Eğer C# koduyla silinemezse Linux sudo fallback mekanizmasını kullan
        if (Path.DirectorySeparatorChar == '/')
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "sudo",
                    Arguments = $"-n /usr/local/bin/project-manager.sh clean-path \"{path}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var process = System.Diagnostics.Process.Start(psi);
                process?.WaitForExit();
            }
            catch { }
        }

        if (!IsDirectoryClean(path))
        {
            throw new InvalidOperationException($"Klasör temizlenemedi: {path}. Bazı dosyalar kilitli olabilir.");
        }
    }

    private bool IsDirectoryClean(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return true;
            var files = Directory.GetFiles(path);
            var subdirs = Directory.GetDirectories(path);
            var nonEnvFiles = files.Where(f => !Path.GetFileName(f).Equals(".env", StringComparison.OrdinalIgnoreCase)).ToList();
            return nonEnvFiles.Count == 0 && subdirs.Length == 0;
        }
        catch
        {
            return false;
        }
    }

    private void SafeDeleteDirectory(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;

        try
        {
            var directory = new DirectoryInfo(path);
            
            // Clear Read-Only attribute on all files recursively
            foreach (var file in directory.GetFiles("*", SearchOption.AllDirectories))
            {
                try
                {
                    if (file.IsReadOnly)
                    {
                        file.IsReadOnly = false;
                    }
                }
                catch { /* Ignore attribute clear failures */ }
            }

            // Clear Read-Only attribute on all directories recursively
            foreach (var dir in directory.GetDirectories("*", SearchOption.AllDirectories))
            {
                try
                {
                    dir.Attributes &= ~FileAttributes.ReadOnly;
                }
                catch { /* Ignore attribute clear failures */ }
            }

            Directory.Delete(path, true);
        }
        catch (Exception ex)
        {
            if (Path.DirectorySeparatorChar == '/')
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "sudo",
                        Arguments = $"/usr/local/bin/project-manager.sh clean-path \"{path}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var process = System.Diagnostics.Process.Start(psi);
                    process?.WaitForExit();

                    if (!Directory.Exists(path))
                    {
                        return; // Successfully deleted via fallback
                    }
                }
                catch { /* Ignore fallback errors to throw original exception */ }
            }

            throw new InvalidOperationException($"Eski hedef dizin temizlenemedi: {path}. Hata: {ex.Message}", ex);
        }
    }
}

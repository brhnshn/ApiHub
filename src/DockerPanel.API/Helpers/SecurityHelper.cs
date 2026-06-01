using System;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace DockerPanel.API.Helpers;

public static class SecurityHelper
{
    // 1. Regex Girdi Denetimleri (Command/SQL Injection Engelleme)
    public static bool IsValidAppName(string name)
    {
        return Regex.IsMatch(name, "^[a-z0-9_-]+$");
    }

    public static bool IsValidDatabaseIdentifier(string identifier)
    {
        return Regex.IsMatch(identifier, "^[a-zA-Z0-9_]+$");
    }

    public static bool IsValidSubdomainName(string name)
    {
        return name == "*" || Regex.IsMatch(name, "^[a-zA-Z0-9_-]+$");
    }

    public static bool IsValidEmail(string email)
    {
        return Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$");
    }

    // 2. Zip Slip (Directory Traversal) Engelleme Mekanizması
    public static void SafeExtractZip(string zipFilePath, string destinationDirectoryPath)
    {
        var destinationInfo = new DirectoryInfo(destinationDirectoryPath);
        string destinationFullPath = destinationInfo.FullName;

        // Destination dizininin var olduğundan emin ol
        if (!Directory.Exists(destinationFullPath))
        {
            Directory.CreateDirectory(destinationFullPath);
        }

        using var archive = ZipFile.OpenRead(zipFilePath);
        foreach (var entry in archive.Entries)
        {
            // Hedef dosya yolunu tam (absolute) olarak çözümler
            string fileDestPath = Path.GetFullPath(Path.Combine(destinationFullPath, entry.FullName));

            // Klasörün ana çıkış dizininin altında kalıp kalmadığını doğrular
            if (!fileDestPath.StartsWith(destinationFullPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Güvenlik İhlali: Zip Slip (Directory Traversal) saldırısı algılandı! Çıkartma işlemi iptal edildi.");
            }

            if (entry.Name == "")
            {
                // Dizin girdisi
                Directory.CreateDirectory(fileDestPath);
            }
            else
            {
                // Dosya girdisi
                var fileDir = Path.GetDirectoryName(fileDestPath);
                if (fileDir != null && !Directory.Exists(fileDir))
                {
                    Directory.CreateDirectory(fileDir);
                }
                
                entry.ExtractToFile(fileDestPath, overwrite: true);
            }
        }
    }
}

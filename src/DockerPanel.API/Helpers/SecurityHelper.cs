using System;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;

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
        if (name == null) return true;
        var trimmed = name.Trim();
        return trimmed == "" || trimmed == "@" || trimmed == "*" || Regex.IsMatch(trimmed, "^[a-zA-Z0-9_-]+$");
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
        if (!destinationFullPath.EndsWith(Path.DirectorySeparatorChar.ToString()))
        {
            destinationFullPath += Path.DirectorySeparatorChar;
        }

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

    public static string Encrypt(string plainText, string secretKey)
    {
        using var sha256 = SHA256.Create();
        var key = sha256.ComputeHash(Encoding.UTF8.GetBytes(secretKey));
        
        using var aes = Aes.Create();
        aes.Key = key;
        aes.GenerateIV();
        var iv = aes.IV;

        using var encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
        using var ms = new MemoryStream();
        ms.Write(iv, 0, iv.Length); // Prepend IV
        using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
        using (var sw = new StreamWriter(cs))
        {
            sw.Write(plainText);
        }
        return Convert.ToBase64String(ms.ToArray());
    }

    public static string Decrypt(string cipherText, string secretKey)
    {
        var cipherBytes = Convert.FromBase64String(cipherText);
        using var sha256 = SHA256.Create();
        var key = sha256.ComputeHash(Encoding.UTF8.GetBytes(secretKey));

        using var aes = Aes.Create();
        aes.Key = key;

        var iv = new byte[aes.BlockSize / 8];
        Array.Copy(cipherBytes, 0, iv, 0, iv.Length);
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
        using var ms = new MemoryStream(cipherBytes, iv.Length, cipherBytes.Length - iv.Length);
        using var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read);
        using var sr = new StreamReader(cs);
        return sr.ReadToEnd();
    }
}

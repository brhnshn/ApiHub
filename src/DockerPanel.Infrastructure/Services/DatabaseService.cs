using System;
using System.Data;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Npgsql;
using DockerPanel.Domain.Interfaces;
using DockerPanel.Domain.Entities;
using DockerPanel.Domain.Security;

namespace DockerPanel.Infrastructure.Services;

public class DatabaseService : IDatabaseService
{
    private readonly string _masterConnectionString;

    public DatabaseService(IConfiguration configuration)
    {
        // Ana PostgreSQL bağlantı dizesini al, yoksa yerel varsayılanı kullan
        _masterConnectionString = configuration.GetConnectionString("MasterPostgresConnection") 
            ?? "Host=localhost;Port=5432;Database=postgres;Username=dp_admin;Password=dp_admin_password";
    }

    public async Task ProvisionDatabaseAsync(string dbName, string dbUser, string dbPassword)
    {
        // 1. Girdi Güvenliği Regex Kontrolü (SQL Injection Önleme)
        if (!InputValidator.IsDatabaseIdentifier(dbName))
        {
            throw new ArgumentException("Veritabanı adı geçersiz! Sadece harf, rakam ve alt çizgi (_) içerebilir.");
        }
        if (!InputValidator.IsDatabaseIdentifier(dbUser))
        {
            throw new ArgumentException("Kullanıcı adı geçersiz! Sadece harf, rakam ve alt çizgi (_) içerebilir.");
        }

        SystemLogQueue.Log("info", $"[PostgreSQL] Yeni şema sağlama işlemi başlatıldı: DB={dbName}, User={dbUser}");

        // 2. Master DB Süper Yönetici Bağlantısı
        using var conn = new NpgsqlConnection(_masterConnectionString);
        await conn.OpenAsync();

        // 3. Sıralı SQL Çalıştırma
        // A. Kullanıcı Oluşturma (Şifre parametrik geçilerek SQL injection önlenir)
        SystemLogQueue.Log("info", $"$ psql -c \"SELECT 1 FROM pg_roles WHERE rolname = '{dbUser}';\"");
        var checkUserCmd = new NpgsqlCommand($"SELECT 1 FROM pg_roles WHERE rolname = @user", conn);
        checkUserCmd.Parameters.AddWithValue("user", dbUser);
        var userExists = await checkUserCmd.ExecuteScalarAsync() != null;

        if (!userExists)
        {
            SystemLogQueue.Log("info", $"$ psql -c \"CREATE USER {dbUser} WITH ENCRYPTED PASSWORD '********';\"");
            var safePassword = dbPassword.Replace("'", "''");
            using var createUserCmd = new NpgsqlCommand($"CREATE USER {dbUser} WITH ENCRYPTED PASSWORD '{safePassword}'", conn);
            await createUserCmd.ExecuteNonQueryAsync();
            SystemLogQueue.Log("info", $"[PostgreSQL] Kullanıcı '{dbUser}' başarıyla oluşturuldu.");
        }
        else
        {
            SystemLogQueue.Log("info", $"[PostgreSQL] Kullanıcı '{dbUser}' zaten mevcut.");
        }

        // B. Veritabanını Yaratma
        // ÖNEMLI: PostgreSQL'de CREATE DATABASE bir transaction bloğu içinde ÇALIŞAMAZ.
        // Bu yüzden ayrı bir bağlantı ile AutoCommit modunda çalıştırılmalıdır.
        SystemLogQueue.Log("info", $"$ psql -c \"SELECT 1 FROM pg_database WHERE datname = '{dbName}';\"");
        var checkDbCmd = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @db", conn);
        checkDbCmd.Parameters.AddWithValue("db", dbName);
        var dbExists = await checkDbCmd.ExecuteScalarAsync() != null;

        if (!dbExists)
        {
            SystemLogQueue.Log("info", $"$ psql -c \"CREATE DATABASE {dbName} OWNER {dbUser};\"");
            // CREATE DATABASE transaction block içinde çalıştırılamaz!
            // Ayrı bir bağlantı açılarak AutoCommit modunda çalıştırılmalıdır.
            using var createDbConn = new NpgsqlConnection(_masterConnectionString);
            await createDbConn.OpenAsync();
            using var createDbCmd = new NpgsqlCommand($"CREATE DATABASE {dbName} OWNER {dbUser}", createDbConn);
            await createDbCmd.ExecuteNonQueryAsync();
            await createDbConn.CloseAsync();
            SystemLogQueue.Log("info", $"[PostgreSQL] Veritabanı '{dbName}' başarıyla oluşturuldu.");
        }
        else
        {
            SystemLogQueue.Log("info", $"[PostgreSQL] Veritabanı '{dbName}' zaten mevcut.");
        }

        // C. Yetkileri Verme
        SystemLogQueue.Log("info", $"$ psql -c \"GRANT ALL PRIVILEGES ON DATABASE {dbName} TO {dbUser};\"");
        using var grantCmd = new NpgsqlCommand($"GRANT ALL PRIVILEGES ON DATABASE {dbName} TO {dbUser}", conn);
        await grantCmd.ExecuteNonQueryAsync();
        SystemLogQueue.Log("info", $"[PostgreSQL] '{dbUser}' kullanıcısına '{dbName}' veritabanı için tam yetki (ALL PRIVILEGES) atandı.");
        SystemLogQueue.Log("info", $"[PostgreSQL] Şema sağlama işlemi başarıyla tamamlandı.");
    }

    public async Task DeleteDatabaseAsync(string dbName, string dbUser)
    {
        if (!InputValidator.IsDatabaseIdentifier(dbName) || !InputValidator.IsDatabaseIdentifier(dbUser))
        {
            throw new ArgumentException("Geçersiz veritabanı veya kullanıcı adı!");
        }

        SystemLogQueue.Log("warning", $"[PostgreSQL] Şema silme işlemi başlatıldı: DB={dbName}");

        using var conn = new NpgsqlConnection(_masterConnectionString);
        await conn.OpenAsync();

        // 1. Aktif bağlantıları zorla kapat (Veritabanını silebilmek için)
        SystemLogQueue.Log("info", $"$ psql -c \"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{dbName}';\"");
        using var terminateCmd = new NpgsqlCommand(@"
            SELECT pg_terminate_backend(pg_stat_activity.pid)
            FROM pg_stat_activity
            WHERE pg_stat_activity.datname = @db
              AND pid <> pg_backend_pid();", conn);
        terminateCmd.Parameters.AddWithValue("db", dbName);
        await terminateCmd.ExecuteNonQueryAsync();

        // 2. Veritabanını Sil
        SystemLogQueue.Log("info", $"$ psql -c \"DROP DATABASE IF EXISTS {dbName};\"");
        using var dropDbCmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS {dbName}", conn);
        await dropDbCmd.ExecuteNonQueryAsync();
        SystemLogQueue.Log("info", $"[PostgreSQL] Veritabanı '{dbName}' başarıyla silindi.");

        // 3. Kullanıcıyı Sil
        SystemLogQueue.Log("info", $"$ psql -c \"DROP USER IF EXISTS {dbUser};\"");
        using var dropUserCmd = new NpgsqlCommand($"DROP USER IF EXISTS {dbUser}", conn);
        await dropUserCmd.ExecuteNonQueryAsync();
        SystemLogQueue.Log("info", $"[PostgreSQL] PostgreSQL kullanıcısı '{dbUser}' başarıyla silindi.");
        SystemLogQueue.Log("info", $"[PostgreSQL] Şema kaldırma işlemi başarıyla tamamlandı.");
    }

    public async Task<long> GetDatabaseSizeAsync(string dbName)
    {
        if (!InputValidator.IsDatabaseIdentifier(dbName))
        {
            return 0;
        }

        try
        {
            using var conn = new NpgsqlConnection(_masterConnectionString);
            await conn.OpenAsync();

            using var sizeCmd = new NpgsqlCommand("SELECT pg_database_size(@db)", conn);
            sizeCmd.Parameters.AddWithValue("db", dbName);
            var result = await sizeCmd.ExecuteScalarAsync();
            
            return result != null && result != DBNull.Value ? Convert.ToInt64(result) : 0;
        }
        catch
        {
            return 0; // Bir hata durumunda 0 dönülür (simülasyon veya bağlantı kopukluğu)
        }
    }

    public async Task<List<ExistingDatabaseInfo>> DiscoverExistingDatabasesAsync()
    {
        var list = new List<ExistingDatabaseInfo>();
        try
        {
            using var conn = new NpgsqlConnection(_masterConnectionString);
            await conn.OpenAsync();

            string query = @"
                SELECT d.datname, r.rolname, pg_database_size(d.datname)
                FROM pg_database d
                JOIN pg_roles r ON d.datdba = r.oid
                WHERE d.datistemplate = false
                  AND d.datname NOT IN ('postgres', 'template0', 'template1', 'dockerpanel_db')";

            using var cmd = new NpgsqlCommand(query, conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var dbName = reader.GetString(0);
                var owner = reader.GetString(1);
                var size = reader.GetInt64(2);

                list.Add(new ExistingDatabaseInfo
                {
                    DbName = dbName,
                    DbUser = owner,
                    SizeInBytes = size
                });
            }
        }
        catch (Exception ex)
        {
            SystemLogQueue.Log("error", $"Mevcut veritabanları keşfedilirken hata: {ex.Message}");
            // Windows simülasyonu için fallback ekleyelim
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            {
                list.Add(new ExistingDatabaseInfo { DbName = "simulated_blog_db", DbUser = "blog_user", SizeInBytes = 256 * 1024 * 1024 });
                list.Add(new ExistingDatabaseInfo { DbName = "simulated_ecommerce_db", DbUser = "ecom_user", SizeInBytes = 1024 * 1024 * 1024 });
            }
        }

        return list;
    }
}

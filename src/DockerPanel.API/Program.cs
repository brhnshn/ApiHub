using System;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using DockerPanel.API.Helpers;
using DockerPanel.API.Hubs;
using DockerPanel.API.Workers;
using DockerPanel.Domain.Interfaces;
using DockerPanel.Infrastructure.Data;
using DockerPanel.Infrastructure.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
const long MaxZipUploadBytes = 200L * 1024 * 1024;

// Kestrel ve FormOptions limitlerini artır (Büyük ZIP yüklemeleri için)
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = MaxZipUploadBytes; // 200 MB
});

builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = MaxZipUploadBytes; // 200 MB
});

// 1. Veritabanı (PostgreSQL) Yapılandırması
builder.Services.AddDbContext<DockerPanelDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"),
        b => b.MigrationsAssembly("DockerPanel.Infrastructure")));

// 2. Modüler Servis Kayıtları
builder.Services.AddScoped<IProjectContainerService, ProjectContainerService>();
builder.Services.AddScoped<IDeploymentService, DeploymentService>();
builder.Services.AddScoped<IComposeAnalyzerService, ComposeAnalyzerService>();
builder.Services.AddScoped<IComposeDeploymentService, ComposeDeploymentService>();
builder.Services.AddScoped<IGitHubDeploymentService, GitHubDeploymentService>();
builder.Services.AddScoped<IComposeSecurityValidator, ComposeSecurityValidator>();
builder.Services.AddSingleton<IDeploymentJobQueue, DeploymentJobQueue>();
builder.Services.AddScoped<IProcessManagerService, ProcessManagerService>();
builder.Services.AddScoped<IProjectZipDeployService, ProjectZipDeployService>();
builder.Services.AddScoped<INginxService, NginxProxyService>();
builder.Services.AddScoped<IDatabaseService, DatabaseService>();
builder.Services.AddScoped<IMailService, MailService>();
builder.Services.AddScoped<IFirewallService, FirewallService>();
builder.Services.AddScoped<IAuditLogService, AuditLogService>();
builder.Services.AddScoped<IBackupService, BackupService>();
builder.Services.AddScoped<IPushNotificationService, PushNotificationService>();
builder.Services.AddSingleton<EncryptionService>();
builder.Services.AddHttpClient<ICloudflareService, CloudflareService>();

// 3. Real-Time Akış (SignalR) ve Metrik Arka Plan İşçisi
builder.Services.AddSignalR();

// Geliştirme (Development) ortamında arka plan servislerini kapatıyoruz.
// Böylece bilgisayarındaki Docker'a bağlanmaya çalışıp Timeout hatalarına sebep olmaz.
if (!builder.Environment.IsDevelopment())
{
    builder.Services.AddHostedService<MetricBackgroundWorker>();
    builder.Services.AddHostedService<BackupWorker>();
}
builder.Services.AddHostedService<DeploymentCleanupWorker>();
builder.Services.AddHostedService<DeploymentWorker>();
builder.Services.AddHostedService<MailPollingWorker>();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, token) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await context.HttpContext.Response.WriteAsJsonAsync(new
        {
            Message = "Cok fazla istek gonderildi. Lutfen kisa bir sure sonra tekrar deneyin."
        }, cancellationToken: token);
    };

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
    {
        if (!httpContext.Request.Path.StartsWithSegments("/api"))
        {
            return RateLimitPartition.GetNoLimiter("non-api");
        }

        var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 200,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });

    options.AddPolicy("login", httpContext =>
    {
        var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetSlidingWindowLimiter(key, _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            SegmentsPerWindow = 6,
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });

    options.AddPolicy("resource-heavy", httpContext =>
    {
        var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 15,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });
});

builder.Services.AddHealthChecks()
    .AddCheck<DockerHealthCheck>("docker")
    .AddCheck<PostgreSqlHealthCheck>("postgresql")
    .AddCheck<NginxHealthCheck>("nginx");

// 4. JWT Kimlik Doğrulama Yapılandırması
var jwtSettings = builder.Configuration.GetSection("JwtSettings");
var secretKey = jwtSettings["SecretKey"] ?? "DockerPanelVerySecureSuperSecretKey2026!AwesomeDev";
var key = Encoding.UTF8.GetBytes(secretKey);

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.RequireHttpsMetadata = false;
    options.SaveToken = true;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(key),
        ValidateIssuer = true,
        ValidIssuer = jwtSettings["Issuer"] ?? "DockerPanelAPI",
        ValidateAudience = true,
        ValidAudience = jwtSettings["Audience"] ?? "DockerPanelClient",
        ValidateLifetime = true,
        ClockSkew = TimeSpan.Zero
    };
    // SignalR WebSocket bağlantıları HTTP header gönderemedikleri için JWT token'ı
    // URL query string'den (?access_token=...) geçirir. Bu event bunu sağlar.
    options.Events = new Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var accessToken = context.Request.Query["access_token"];
            var path = context.HttpContext.Request.Path;
            if (!string.IsNullOrEmpty(accessToken))
            {
                if (path.StartsWithSegments("/hubs") || 
                    (path.StartsWithSegments("/api/backups") && path.Value != null && path.Value.Contains("/download/")))
                {
                    context.Token = accessToken;
                }
            }
            return Task.CompletedTask;
        }
    };
});

// 5. CORS Politikaları (Blazor WASM İletişimi İçin)
builder.Services.AddCors(options =>
{
    options.AddPolicy("BlazorCorsPolicy", policy =>
    {
        policy.SetIsOriginAllowed(origin => true)
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials();
    });
});

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// 6. Veritabanını Otomatik Migrate Et ve Eşitle
using (var scope = app.Services.CreateScope())
{
    try
    {
        var db = scope.ServiceProvider.GetRequiredService<DockerPanelDbContext>();
        db.Database.Migrate();
        
        // Eşitleme mekanizmasını çalıştır (projects.conf ve nginx vhost'larını veritabanına aktarır)
        await DockerPanel.API.Helpers.DatabaseSyncHelper.SyncExistingSystemDataAsync(app.Services);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Veritabanı migration/sync hatası: {ex.Message}");
    }
}

// HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
    app.UseWebAssemblyDebugging();
}

app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.UseWebSockets();

app.UseCors("BlazorCorsPolicy");

app.UseRateLimiter();

app.UseAuthentication();
app.UseMiddleware<DockerPanel.API.Helpers.ApiKeyMiddleware>();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/api/health", new HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        var payload = new
        {
            Status = report.Status.ToString(),
            DurationMs = report.TotalDuration.TotalMilliseconds,
            Checks = report.Entries.Select(entry => new
            {
                Name = entry.Key,
                Status = entry.Value.Status.ToString(),
                Description = entry.Value.Description,
                Error = entry.Value.Exception?.Message
            })
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }
});
app.MapHub<MetricLogHub>("/hubs/metriclog");
app.MapFallbackToFile("index.html");

app.Run();

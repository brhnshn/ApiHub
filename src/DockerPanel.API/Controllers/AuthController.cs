using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using DockerPanel.Domain.Entities;
using DockerPanel.Domain.Enums;
using DockerPanel.Infrastructure.Data;
using DockerPanel.Domain.Interfaces;


namespace DockerPanel.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly DockerPanelDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly PasswordHasher<User> _passwordHasher;
    private readonly IAuditLogService _auditLogService;

    public AuthController(DockerPanelDbContext context, IConfiguration configuration, IAuditLogService auditLogService)
    {
        _context = context;
        _configuration = configuration;
        _passwordHasher = new PasswordHasher<User>();
        _auditLogService = auditLogService;
    }

    [HttpGet("setup-status")]
    public async Task<IActionResult> GetSetupStatus()
    {
        var hasUsers = await _context.Users.AnyAsync();
        return Ok(new { SetupRequired = !hasUsers });
    }

    [HttpPost("register")]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        // KORUMA: Eğer sistemde zaten herhangi bir kullanıcı varsa yeni kayda kapat
        if (await _context.Users.AnyAsync())
        {
            return BadRequest(new { Message = "Sisteme ilk kurulum zaten tamamlanmış. Dışarıdan yeni kayıt olmaya tamamen kapatılmıştır!" });
        }

        var user = new User
        {
            Username = request.Username,
            Role = UserRole.Administrator, // İlk kayıt her zaman en üst seviye yöneticidir
            CreatedAt = DateTimeOffset.UtcNow
        };

        user.PasswordHash = _passwordHasher.HashPassword(user, request.Password);

        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var ua = Request.Headers["User-Agent"].ToString() ?? "unknown";
        await _auditLogService.LogAsync(user.Id, "UserRegistered", "User", user.Id, "{}", ip, ua);

        return Ok(new { Message = "İlk yönetici kurulumu başarıyla tamamlandı!", Username = user.Username, Role = user.Role.ToString() });
    }

    [HttpPost("login")]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Username == request.Username);
        if (user == null)
        {
            return Unauthorized(new { Message = "Geçersiz kullanıcı adı veya şifre!" });
        }

        var verificationResult = _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (verificationResult == PasswordVerificationResult.Failed)
        {
            return Unauthorized(new { Message = "Geçersiz kullanıcı adı veya şifre!" });
        }

        var token = GenerateJwtToken(user);

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var ua = Request.Headers["User-Agent"].ToString() ?? "unknown";
        await _auditLogService.LogAsync(user.Id, "UserLogin", "User", user.Id, "{}", ip, ua);

        return Ok(new
        {
            Token = token,
            Username = user.Username,
            Role = user.Role.ToString(),
            UserId = user.Id
        });
    }

    private string GenerateJwtToken(User user)
    {
        var jwtSettings = _configuration.GetSection("JwtSettings");
        var secretKey = jwtSettings["SecretKey"] ?? "DockerPanelVerySecureSuperSecretKey2026!AwesomeDev";
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey));

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Role, user.Role.ToString())
        };

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddDays(7),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256Signature),
            Issuer = jwtSettings["Issuer"] ?? "DockerPanelAPI",
            Audience = jwtSettings["Audience"] ?? "DockerPanelClient"
        };

        var tokenHandler = new JwtSecurityTokenHandler();
        var token = tokenHandler.CreateToken(tokenDescriptor);

        return tokenHandler.WriteToken(token);
    }
    [HttpPost("change-password")]
    [Microsoft.AspNetCore.Authorization.Authorize]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized(new { Message = "Geçersiz oturum!" });
        }

        var user = await _context.Users.FindAsync(userId);
        if (user == null)
        {
            return NotFound(new { Message = "Kullanıcı bulunamadı!" });
        }

        var verificationResult = _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.CurrentPassword);
        if (verificationResult == PasswordVerificationResult.Failed)
        {
            return BadRequest(new { Message = "Mevcut şifre yanlış!" });
        }

        if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 6)
        {
            return BadRequest(new { Message = "Yeni şifre en az 6 karakter olmalıdır!" });
        }

        user.PasswordHash = _passwordHasher.HashPassword(user, request.NewPassword);
        await _context.SaveChangesAsync();

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var ua = Request.Headers["User-Agent"].ToString() ?? "unknown";
        await _auditLogService.LogAsync(user.Id, "UserPasswordChanged", "User", user.Id, "{}", ip, ua);

        return Ok(new { Message = "Şifreniz başarıyla güncellendi!" });
    }
}

public class RegisterRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class ChangePasswordRequest
{
    public string CurrentPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DockerPanel.Domain.Interfaces;
using DockerPanel.Domain.Entities;

namespace DockerPanel.Infrastructure.Services;

public class FirewallService : IFirewallService
{
    // Windows simülasyonu için in-memory list
    private static readonly List<FirewallRuleDto> SimulatedRules = new()
    {
        new FirewallRuleDto { Number = 1, Port = "22", Protocol = "tcp", Action = "ALLOW", Direction = "IN", From = "Anywhere" },
        new FirewallRuleDto { Number = 2, Port = "80", Protocol = "tcp", Action = "ALLOW", Direction = "IN", From = "Anywhere" },
        new FirewallRuleDto { Number = 3, Port = "443", Protocol = "tcp", Action = "ALLOW", Direction = "IN", From = "Anywhere" },
        new FirewallRuleDto { Number = 4, Port = "25017", Protocol = "tcp", Action = "ALLOW", Direction = "IN", From = "Anywhere" },
        new FirewallRuleDto { Number = 5, Port = "993", Protocol = "tcp", Action = "ALLOW", Direction = "IN", From = "Anywhere" },
        new FirewallRuleDto { Number = 6, Port = "587", Protocol = "tcp", Action = "ALLOW", Direction = "IN", From = "Anywhere" }
    };

    private static bool _simulatedActive = true;

    private async Task<string> ExecuteCommandAsync(string command, string args)
    {
        SystemLogQueue.Log("info", $"$ {command} {args}");
        var psi = new ProcessStartInfo
        {
            FileName = command,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var timeoutTask = Task.Delay(10000);
        var runTask = Task.Run(() => process.WaitForExit());

        if (await Task.WhenAny(runTask, timeoutTask) == timeoutTask)
        {
            try { process.Kill(); } catch { }
            throw new Exception($"Güvenlik duvarı komut zaman aşımı: {command} {args}");
        }

        string outStr = await process.StandardOutput.ReadToEndAsync();
        if (process.ExitCode != 0)
        {
            string err = await process.StandardError.ReadToEndAsync();
            throw new Exception($"Güvenlik duvarı hatası (Kod: {process.ExitCode}): {err}");
        }

        return outStr;
    }

    public async Task<bool> IsFirewallActiveAsync()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return _simulatedActive;
        }

        try
        {
            string output = await ExecuteCommandAsync("sudo", "-n /usr/sbin/ufw status");
            return output.Contains("Status: active", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            SystemLogQueue.Log("error", $"UFW durumu alınırken hata: {ex}");
            throw;
        }
    }

    public async Task<IEnumerable<FirewallRuleDto>> GetRulesAsync()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return SimulatedRules;
        }

        try
        {
            string output = await ExecuteCommandAsync("sudo", "-n /usr/sbin/ufw status numbered");
            var rules = new List<FirewallRuleDto>();
            
            // Satır satır oku ve regex ile çöz
            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                // [ 1] 22/tcp                     ALLOW IN    Anywhere
                var match = Regex.Match(line, @"^\s*\[\s*(\d+)\s*\]\s*([^\s]+)\s+(ALLOW IN|DENY IN|ALLOW|DENY)\s+(.*)$", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    int number = int.Parse(match.Groups[1].Value);
                    string portProto = match.Groups[2].Value;
                    string actionStr = match.Groups[3].Value.Trim().ToUpper();
                    string from = match.Groups[4].Value.Trim();

                    string port = portProto;
                    string protocol = "any";

                    if (portProto.Contains('/'))
                    {
                        var parts = portProto.Split('/');
                        port = parts[0];
                        protocol = parts[1];
                    }

                    string action = actionStr.Contains("ALLOW") ? "ALLOW" : "DENY";
                    string direction = actionStr.Contains("OUT") ? "OUT" : "IN";

                    rules.Add(new FirewallRuleDto
                    {
                        Number = number,
                        Port = port,
                        Protocol = protocol,
                        Action = action,
                        Direction = direction,
                        From = from
                    });
                }
            }

            return rules;
        }
        catch (Exception ex)
        {
            SystemLogQueue.Log("error", $"UFW kuralları parse edilirken hata: {ex}");
            throw;
        }
    }

    public async Task AddRuleAsync(string port, string protocol, string action)
    {
        // Girdi Kontrolü
        if (!Regex.IsMatch(port, @"^\d+(:\d+)?$"))
        {
            throw new ArgumentException("Geçersiz port formatı! Sadece sayı veya aralık (örn: 8000:8010) olabilir.");
        }
        string cleanProto = protocol.ToLower() == "any" ? "" : $"/{protocol.ToLower()}";
        string actionCmd = action.ToUpper() == "ALLOW" ? "allow" : "deny";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            SystemLogQueue.Log("info", $"$ [Windows Simülasyonu] ufw {actionCmd} {port}{cleanProto}");
            int newNum = SimulatedRules.Count + 1;
            SimulatedRules.Add(new FirewallRuleDto
            {
                Number = newNum,
                Port = port,
                Protocol = string.IsNullOrEmpty(cleanProto) ? "any" : protocol.ToLower(),
                Action = action.ToUpper(),
                Direction = "IN",
                From = "Anywhere"
            });
            SystemLogQueue.Log("info", $"[UFW] Simüle Kural Eklendi: {action.ToUpper()} port {port}/{protocol}");
            return;
        }

        try
        {
            string cmdArgs = $"{actionCmd} {port}{cleanProto}";
            await ExecuteCommandAsync("sudo", $"-n /usr/sbin/ufw {cmdArgs}");
            SystemLogQueue.Log("info", $"[UFW] Kural başarıyla eklendi: {action.ToUpper()} {port}/{protocol}");
        }
        catch (Exception ex)
        {
            SystemLogQueue.Log("error", $"UFW kuralı eklenirken hata: {ex.Message}");
            throw;
        }
    }

    public async Task DeleteRuleAsync(int ruleNumber)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            SystemLogQueue.Log("info", $"$ [Windows Simülasyonu] ufw delete {ruleNumber}");
            var rule = SimulatedRules.Find(r => r.Number == ruleNumber);
            if (rule != null)
            {
                SimulatedRules.Remove(rule);
                // Numaraları yeniden sırala
                for (int i = 0; i < SimulatedRules.Count; i++)
                {
                    SimulatedRules[i].Number = i + 1;
                }
                SystemLogQueue.Log("info", $"[UFW] Simüle Kural {ruleNumber} başarıyla silindi.");
            }
            return;
        }

        try
        {
            // --force flag'i onay istemeden silmek için kullanılır
            await ExecuteCommandAsync("sudo", $"-n /usr/sbin/ufw --force delete {ruleNumber}");
            SystemLogQueue.Log("info", $"[UFW] Kural {ruleNumber} başarıyla kaldırıldı.");
        }
        catch (Exception ex)
        {
            SystemLogQueue.Log("error", $"UFW kuralı {ruleNumber} silinirken hata: {ex.Message}");
            throw;
        }
    }

    public async Task SetFirewallStatusAsync(bool active)
    {
        string cmd = active ? "enable" : "disable";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            SystemLogQueue.Log("info", $"$ [Windows Simülasyonu] ufw {cmd}");
            _simulatedActive = active;
            SystemLogQueue.Log("info", $"[UFW] Simüle UFW durumu değiştirildi: {cmd.ToUpper()}");
            return;
        }

        try
        {
            // --force flag'i enable ederken onay istemesini engeller
            string forceFlag = active ? "--force " : "";
            await ExecuteCommandAsync("sudo", $"-n /usr/sbin/ufw {forceFlag}{cmd}");
            SystemLogQueue.Log("info", $"[UFW] Güvenlik duvarı başarıyla güncellendi: {cmd.ToUpper()}");
        }
        catch (Exception ex)
        {
            SystemLogQueue.Log("error", $"UFW durumu {cmd} yapılırken hata: {ex.Message}");
            throw;
        }
    }
}

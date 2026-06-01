using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using DockerPanel.Domain.Interfaces;

namespace DockerPanel.Infrastructure.Services;

public class CloudflareService : ICloudflareService
{
    private readonly HttpClient _httpClient;

    public CloudflareService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.BaseAddress = new Uri("https://api.cloudflare.com/client/v4/");
    }

    public async Task<string> CreateDnsRecordAsync(string token, string zoneId, string type, string name, string content, bool proxied)
    {
        // HTTP Istek Başlıklarının Hazırlanması
        var request = new HttpRequestMessage(HttpMethod.Post, $"zones/{zoneId}/dns_records");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        
        // JSON Payload Derleme
        var payload = new
        {
            type = type,
            name = name,
            content = content,
            ttl = 3600,
            proxied = proxied
        };

        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        // İstek Gönderimi
        var response = await _httpClient.SendAsync(request);
        var responseString = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Cloudflare API Hatası (HTTP {response.StatusCode}): {responseString}");
        }

        // Yanıt İşleme (Dönen JSON yanıtındaki ID değerini yakala)
        try
        {
            var jsonNode = JsonNode.Parse(responseString);
            var success = jsonNode?["success"]?.GetValue<bool>() ?? false;
            
            if (success)
            {
                var recordId = jsonNode?["result"]?["id"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(recordId))
                {
                    return recordId;
                }
            }

            var errors = jsonNode?["errors"]?.ToString();
            throw new InvalidOperationException($"Cloudflare DNS kaydı oluşturulamadı: {errors}");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Cloudflare API yanıtı ayrıştırılamadı: {responseString}", ex);
        }
    }

    public async Task DeleteDnsRecordAsync(string token, string zoneId, string cloudflareRecordId)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"zones/{zoneId}/dns_records/{cloudflareRecordId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _httpClient.SendAsync(request);
        var responseString = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Cloudflare DNS Silme Hatası (HTTP {response.StatusCode}): {responseString}");
        }

        try
        {
            var jsonNode = JsonNode.Parse(responseString);
            var success = jsonNode?["success"]?.GetValue<bool>() ?? false;

            if (!success)
            {
                var errors = jsonNode?["errors"]?.ToString();
                throw new InvalidOperationException($"Cloudflare DNS kaydı silinemedi: {errors}");
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Cloudflare API yanıtı ayrıştırılamadı: {responseString}", ex);
        }
    }

    public async Task<System.Collections.Generic.List<CloudflareDnsRecordDto>> ListDnsRecordsAsync(string token, string zoneId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"zones/{zoneId}/dns_records?per_page=100");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _httpClient.SendAsync(request);
        var responseString = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Cloudflare DNS Listeleme Hatası (HTTP {response.StatusCode}): {responseString}");
        }

        var list = new System.Collections.Generic.List<CloudflareDnsRecordDto>();
        try
        {
            var jsonNode = JsonNode.Parse(responseString);
            var success = jsonNode?["success"]?.GetValue<bool>() ?? false;

            if (success)
            {
                var result = jsonNode?["result"]?.AsArray();
                if (result != null)
                {
                    foreach (var item in result)
                    {
                        if (item == null) continue;
                        
                        list.Add(new CloudflareDnsRecordDto
                        {
                            Id = item["id"]?.GetValue<string>() ?? "",
                            Type = item["type"]?.GetValue<string>() ?? "",
                            Name = item["name"]?.GetValue<string>() ?? "",
                            Content = item["content"]?.GetValue<string>() ?? "",
                            Ttl = item["ttl"]?.GetValue<int>() ?? 3600,
                            Proxied = item["proxied"]?.GetValue<bool>() ?? false
                        });
                    }
                }
            }
            return list;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Cloudflare API yanıtı ayrıştırılamadı: {responseString}", ex);
        }
    }
}

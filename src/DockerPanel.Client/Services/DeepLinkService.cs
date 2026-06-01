using System;
using System.Web;

namespace DockerPanel.Client.Services;

public class DeepLinkService
{
    public event Action<string>? OnDeepLinkReceived;
    public string? PendingDeepLinkPath { get; set; }

    public void HandleDeepLink(string url)
    {
        try
        {
            var uri = new Uri(url);
            if (uri.Scheme.Equals("apihub", StringComparison.OrdinalIgnoreCase))
            {
                var queryParams = HttpUtility.ParseQueryString(uri.Query);
                var path = queryParams["path"];
                if (!string.IsNullOrEmpty(path))
                {
                    var projectId = queryParams["projectId"];
                    if (!string.IsNullOrEmpty(projectId))
                    {
                        path += $"?projectId={projectId}";
                    }
                    
                    if (OnDeepLinkReceived != null)
                    {
                        OnDeepLinkReceived.Invoke(path);
                    }
                    else
                    {
                        PendingDeepLinkPath = path;
                    }
                }
            }
        }
        catch
        {
            // Ignore malformed links
        }
    }
}

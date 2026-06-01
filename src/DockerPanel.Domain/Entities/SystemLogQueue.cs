using System;
using System.Collections.Concurrent;

namespace DockerPanel.Domain.Entities;

public static class SystemLogQueue
{
    public static ConcurrentQueue<SystemLogLine> Queue { get; } = new();

    public static void Log(string level, string message)
    {
        Queue.Enqueue(new SystemLogLine
        {
            Timestamp = DateTimeOffset.Now,
            Level = level,
            Message = message
        });

        // En fazla 1000 satır log hafızada tutulsun
        while (Queue.Count > 1000)
        {
            Queue.TryDequeue(out _);
        }
    }
}

public class SystemLogLine
{
    public DateTimeOffset Timestamp { get; set; }
    public string Level { get; set; } = "info";
    public string Message { get; set; } = string.Empty;
}

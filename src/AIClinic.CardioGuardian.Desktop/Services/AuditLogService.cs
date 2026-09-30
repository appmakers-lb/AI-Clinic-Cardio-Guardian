using System;
using System.IO;
using System.Text.Json;

namespace AIClinic.CardioGuardian.Desktop.Services;

public sealed class AuditLogService
{
    private readonly string _path;
    private readonly object _gate = new();

    public AuditLogService()
    {
        var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIClinic", "CardioGuardian", "audit");
        Directory.CreateDirectory(dir);
        _path = System.IO.Path.Combine(dir, $"audit-{DateTime.UtcNow:yyyyMMdd}.jsonl");
    }

    public string LogFilePath => _path;

    public void Write(string eventType, object? details = null)
    {
        var evt = new { tsUtc = DateTime.UtcNow, eventType, details };
        var line = JsonSerializer.Serialize(evt);
        lock (_gate) File.AppendAllText(_path, line + Environment.NewLine);
    }
}

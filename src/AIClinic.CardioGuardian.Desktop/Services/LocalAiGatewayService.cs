using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AIClinic.CardioGuardian.Core.Models;
using AIClinic.CardioGuardian.Core.Services;

namespace AIClinic.CardioGuardian.Desktop.Services;

public sealed class LocalAiGatewayService : IDisposable
{
    private readonly HttpClient _http = new()
    {
        BaseAddress = new Uri("http://127.0.0.1:8765"),
        Timeout = TimeSpan.FromMinutes(15)
    };

    private readonly StructuredFindingService _structuredFindingService = new();
    private Process? _gatewayProcess;
    private readonly object _gatewayLogLock = new();
    private readonly Queue<string> _gatewayLog = new();

    public string GatewayStartupLog
    {
        get
        {
            lock (_gatewayLogLock)
                return string.Join(Environment.NewLine, _gatewayLog);
        }
    }

    public async Task<(bool Reachable, bool ModelLoaded, string Message)> EnsureRunningAsync(
        CancellationToken cancellationToken = default)
    {
        var health = await CheckHealthAsync(cancellationToken);
        if (health.Reachable)
            return health;

        var researchDirectory = FindResearchDirectory();
        if (researchDirectory is null)
        {
            return (
                false,
                false,
                "Could not locate the ai-research folder from the running application.");
        }

        if (_gatewayProcess is null || _gatewayProcess.HasExited)
        {
            _gatewayProcess?.Dispose();
            _gatewayProcess = StartGatewayProcess(researchDirectory);

            if (_gatewayProcess is null)
            {
                return (
                    false,
                    false,
                    "Could not start Python. Install/enable Python or run ai-research\\run_gateway.bat once.");
            }
        }

        var timeoutAt = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < timeoutAt)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await Task.Delay(750, cancellationToken);
            health = await CheckHealthAsync(cancellationToken);
            if (health.Reachable)
                return health;

            if (_gatewayProcess.HasExited)
                break;
        }

        var log = GatewayStartupLog;
        var detail = string.IsNullOrWhiteSpace(log)
            ? "The local AI process did not become ready."
            : $"The local AI process did not become ready. Last output: {log}";

        return (false, false, detail);
    }

    private Process? StartGatewayProcess(string researchDirectory)
    {
        foreach (var candidate in new[]
        {
            (FileName: "python.exe", Prefix: Array.Empty<string>()),
            (FileName: "python", Prefix: Array.Empty<string>()),
            (FileName: "py.exe", Prefix: new[] { "-3" })
        })
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = candidate.FileName,
                    WorkingDirectory = researchDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                foreach (var argument in candidate.Prefix)
                    startInfo.ArgumentList.Add(argument);
                startInfo.ArgumentList.Add("research_gateway.py");

                var process = new Process
                {
                    StartInfo = startInfo,
                    EnableRaisingEvents = true
                };

                process.OutputDataReceived += (_, args) => CaptureGatewayLog(args.Data);
                process.ErrorDataReceived += (_, args) => CaptureGatewayLog(args.Data);

                if (!process.Start())
                {
                    process.Dispose();
                    continue;
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                CaptureGatewayLog($"Started local AI gateway with {candidate.FileName}.");
                return process;
            }
            catch (Exception ex)
            {
                CaptureGatewayLog($"{candidate.FileName}: {ex.Message}");
            }
        }

        return null;
    }

    private void CaptureGatewayLog(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        lock (_gatewayLogLock)
        {
            _gatewayLog.Enqueue(line.Trim());
            while (_gatewayLog.Count > 12)
                _gatewayLog.Dequeue();
        }
    }

    private static string? FindResearchDirectory()
    {
        var roots = new[]
        {
            AppContext.BaseDirectory,
            Environment.CurrentDirectory
        };

        foreach (var root in roots)
        {
            var directory = new DirectoryInfo(root);
            for (var level = 0; level < 9 && directory is not null; level++, directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "ai-research");
                if (File.Exists(Path.Combine(candidate, "research_gateway.py")))
                    return candidate;
            }
        }

        return null;
    }

    public async Task<(bool Reachable, bool ModelLoaded, string Message)> CheckHealthAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.GetAsync("/health", cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                return (false, false, $"Local AI service returned {(int)response.StatusCode}: {json}");

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var loaded = root.TryGetProperty("modelLoaded", out var modelLoaded) && modelLoaded.GetBoolean();
            var message = root.TryGetProperty("message", out var msg)
                ? msg.GetString() ?? string.Empty
                : string.Empty;
            return (true, loaded, message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (false, false, "Local AI health check was cancelled.");
        }
        catch (Exception ex)
        {
            return (false, false, ex.Message);
        }
    }

    public async Task<StructuredFindingPackage> AnalyzeSeriesAsync(
        ImagingSeriesInfo series,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(series);

        if (series.FilePaths.Count == 0)
            throw new InvalidOperationException("The selected series has no local DICOM file paths.");

        var request = new
        {
            sourceId = series.Id,
            studyInstanceUid = series.StudyInstanceUid,
            seriesInstanceUid = series.SeriesInstanceUid,
            modality = series.Modality,
            projection = series.Projection,
            seriesDescription = series.SeriesDescription,
            protocolName = string.Empty,
            frameCount = series.TotalFrames,
            estimatedFramesPerSecond = series.EstimatedFramesPerSecond,
            filePaths = series.FilePaths
        };

        using var response = await _http.PostAsJsonAsync(
            "/analyze-series",
            request,
            cancellationToken);

        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Local AI service returned {(int)response.StatusCode}: {json}");

        return _structuredFindingService.Parse(json);
    }


    public async Task<StructuredFindingPackage> AnalyzeCaseAsync(
        IReadOnlyList<ImagingSeriesInfo> series,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(series);

        var usable = series
            .Where(item => item.FilePaths.Count > 0)
            .ToArray();

        if (usable.Length == 0)
            throw new InvalidOperationException("The case has no local DICOM series with file paths.");

        var request = new
        {
            series = usable.Select(item => new
            {
                sourceId = item.Id,
                studyInstanceUid = item.StudyInstanceUid,
                seriesInstanceUid = item.SeriesInstanceUid,
                modality = item.Modality,
                projection = item.Projection,
                seriesDescription = item.SeriesDescription,
                protocolName = string.Empty,
                frameCount = item.TotalFrames,
                estimatedFramesPerSecond = item.EstimatedFramesPerSecond,
                filePaths = item.FilePaths
            }).ToArray()
        };

        using var response = await _http.PostAsJsonAsync(
            "/analyze-case",
            request,
            cancellationToken);

        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Local AI service returned {(int)response.StatusCode}: {json}");

        return _structuredFindingService.Parse(json);
    }

    public void Dispose()
    {
        _http.Dispose();

        try
        {
            if (_gatewayProcess is { HasExited: false })
                _gatewayProcess.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort cleanup only.
        }
        finally
        {
            _gatewayProcess?.Dispose();
            _gatewayProcess = null;
        }
    }
}

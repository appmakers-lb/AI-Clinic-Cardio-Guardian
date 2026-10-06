using System.Net.Http;
using System.Net.Http.Json;
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

    public void Dispose() => _http.Dispose();
}

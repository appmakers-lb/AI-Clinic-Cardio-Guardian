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
        Timeout = TimeSpan.FromSeconds(10)
    };

    private readonly StructuredFindingService _structuredFindingService = new();

    public async Task<(bool Reachable, bool ModelLoaded, string Message)> CheckHealthAsync()
    {
        try
        {
            using var response = await _http.GetAsync("/health");
            var json = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                return (false, false, $"Local AI service returned {(int)response.StatusCode}: {json}");

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var loaded = root.TryGetProperty("modelLoaded", out var modelLoaded) && modelLoaded.GetBoolean();
            var message = root.TryGetProperty("message", out var msg) ? msg.GetString() ?? string.Empty : string.Empty;
            return (true, loaded, message);
        }
        catch (Exception ex)
        {
            return (false, false, ex.Message);
        }
    }

    public async Task<StructuredFindingPackage> AnalyzeSeriesAsync(ImagingSeriesInfo series)
    {
        var request = new
        {
            sourceId = series.Id,
            studyInstanceUid = series.StudyInstanceUid,
            seriesInstanceUid = series.SeriesInstanceUid,
            modality = series.Modality,
            projection = series.Projection,
            frameCount = series.TotalFrames,
            estimatedFramesPerSecond = series.EstimatedFramesPerSecond,
            filePaths = series.FilePaths
        };

        using var response = await _http.PostAsJsonAsync("/analyze-series", request);
        var json = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Local AI service returned {(int)response.StatusCode}: {json}");

        return _structuredFindingService.Parse(json);
    }

    public void Dispose() => _http.Dispose();
}

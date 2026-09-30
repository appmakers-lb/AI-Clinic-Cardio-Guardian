using System.IO;
using System.Text.Json;
using AIClinic.CardioGuardian.Core.Models;

namespace AIClinic.CardioGuardian.Core.Services;

public sealed class StructuredFindingService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public StructuredFindingPackage Parse(string json)
    {
        var package = JsonSerializer.Deserialize<StructuredFindingPackage>(json, Options)
            ?? throw new InvalidDataException("Structured finding package is empty or invalid JSON.");

        if (string.IsNullOrWhiteSpace(package.ModelId) || string.IsNullOrWhiteSpace(package.ModelVersion))
            throw new InvalidDataException("ModelId and ModelVersion are required.");

        return package;
    }

    public IReadOnlyList<GuardianFinding> ToGuardianFindings(StructuredFindingPackage package)
    {
        var result = new List<GuardianFinding>();
        foreach (var item in package.Findings)
        {
            if (!Enum.TryParse<FindingPriority>(item.Priority, true, out var priority))
                priority = FindingPriority.Review;

            var evidence = item.Evidence.Select(x => new EvidenceReference(
                x.SourceId, x.FrameStart, x.FrameEnd, x.Projection, x.Description)).ToArray();

            result.Add(new GuardianFinding
            {
                Id = item.Id,
                Vessel = item.Vessel,
                Segment = item.Segment,
                FindingType = item.FindingType,
                Confidence = item.Confidence,
                Priority = priority,
                Evidence = evidence,
                Explanation = item.Explanation,
                MeasurementSummary = item.MeasurementSummary,
                Source = FindingSource.StructuredResearchModel,
                SourceVersion = $"{package.ModelId}:{package.ModelVersion}"
            });
        }
        return result;
    }
}

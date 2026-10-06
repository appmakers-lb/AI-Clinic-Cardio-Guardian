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
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidDataException("Structured finding package is empty.");

        StructuredFindingPackage package;
        try
        {
            package = JsonSerializer.Deserialize<StructuredFindingPackage>(json, Options)
                ?? throw new InvalidDataException("Structured finding package is empty or invalid JSON.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Structured finding package contains invalid JSON.", ex);
        }

        if (string.IsNullOrWhiteSpace(package.ModelId) || string.IsNullOrWhiteSpace(package.ModelVersion))
            throw new InvalidDataException("ModelId and ModelVersion are required.");

        if (string.Equals(package.ModelId, "NO_MODEL", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("NO_MODEL output cannot be imported as a research-model finding package.");

        if (package.Findings is null)
            throw new InvalidDataException("Findings array is required.");

        return package;
    }

    public IReadOnlyList<GuardianFinding> ToGuardianFindings(StructuredFindingPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        var result = new List<GuardianFinding>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in package.Findings)
        {
            if (string.IsNullOrWhiteSpace(item.Id))
                throw new InvalidDataException("Every structured finding requires an id.");
            if (!seenIds.Add(item.Id))
                throw new InvalidDataException($"Duplicate structured finding id '{item.Id}'.");
            if (string.IsNullOrWhiteSpace(item.Vessel) ||
                string.IsNullOrWhiteSpace(item.Segment) ||
                string.IsNullOrWhiteSpace(item.FindingType))
            {
                throw new InvalidDataException(
                    $"Finding '{item.Id}' requires vessel, segment, and findingType.");
            }

            if (double.IsNaN(item.Confidence) ||
                double.IsInfinity(item.Confidence) ||
                item.Confidence is < 0 or > 1)
            {
                throw new InvalidDataException(
                    $"Finding '{item.Id}' confidence must be between 0 and 1.");
            }

            if (item.Evidence is null || item.Evidence.Count == 0)
                throw new InvalidDataException($"Finding '{item.Id}' requires at least one evidence reference.");

            if (!Enum.TryParse<FindingPriority>(item.Priority, true, out var priority))
                throw new InvalidDataException(
                    $"Finding '{item.Id}' priority '{item.Priority}' is not supported.");

            var evidence = item.Evidence.Select(x =>
            {
                var reference = new EvidenceReference(
                    x.SourceId, x.FrameStart, x.FrameEnd, x.Projection, x.Description)
                {
                    NormalizedCenterX = x.NormalizedCenterX,
                    NormalizedCenterY = x.NormalizedCenterY,
                    NormalizedRadius = x.NormalizedRadius
                };
                try
                {
                    reference.Validate();
                }
                catch (InvalidOperationException ex)
                {
                    throw new InvalidDataException(
                        $"Finding '{item.Id}' contains invalid evidence: {ex.Message}", ex);
                }

                return reference;
            }).ToArray();

            var finding = new GuardianFinding
            {
                Id = item.Id.Trim(),
                Vessel = item.Vessel.Trim(),
                Segment = item.Segment.Trim(),
                FindingType = item.FindingType.Trim(),
                Confidence = item.Confidence,
                Priority = priority,
                Evidence = evidence,
                Explanation = item.Explanation?.Trim(),
                MeasurementSummary = item.MeasurementSummary?.Trim(),
                EstimatedDiameterStenosisPercent = item.EstimatedDiameterStenosisPercent,
                EstimatedDiameterStenosisLowerPercent = item.EstimatedDiameterStenosisLowerPercent,
                EstimatedDiameterStenosisUpperPercent = item.EstimatedDiameterStenosisUpperPercent,
                ReferenceDiameterPixels = item.ReferenceDiameterPixels,
                MinimumLumenDiameterPixels = item.MinimumLumenDiameterPixels,
                LesionLengthPixels = item.LesionLengthPixels,
                ReferenceDiameterMm = item.ReferenceDiameterMm,
                MinimumLumenDiameterMm = item.MinimumLumenDiameterMm,
                LesionLengthMm = item.LesionLengthMm,
                MeasurementQuality = item.MeasurementQuality?.Trim(),
                MeasurementQualityScore = item.MeasurementQualityScore,
                MeasurementFrameCount = item.MeasurementFrameCount,
                MeasurementVariabilityPercent = item.MeasurementVariabilityPercent,
                CalibrationSource = item.CalibrationSource?.Trim(),
                BorderConfidence = item.BorderConfidence,
                LongitudinalPosition = item.LongitudinalPosition,
                ProjectedReferenceSpanPixels = item.ProjectedReferenceSpanPixels,
                FrameQualityScore = item.FrameQualityScore,
                FrameOverlapRisk = item.FrameOverlapRisk,
                InjectionSide = item.InjectionSide?.Trim(),
                LesionGroupId = item.LesionGroupId?.Trim(),
                MultiViewConfirmed = item.MultiViewConfirmed,
                ProjectionCount = item.ProjectionCount,
                SourceSeriesCount = item.SourceSeriesCount,
                CrossViewVariabilityPercent = item.CrossViewVariabilityPercent,
                OcclusionPercent = item.OcclusionPercent,
                TotalOcclusionScore = item.TotalOcclusionScore,
                TotalOcclusionFrameCount = item.TotalOcclusionFrameCount,
                ChronicityEstablished = item.ChronicityEstablished,
                Source = FindingSource.StructuredResearchModel,
                SourceVersion = $"{package.ModelId.Trim()}:{package.ModelVersion.Trim()}"
            };

            try
            {
                finding.Validate();
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidDataException(
                    $"Finding '{item.Id}' failed validation: {ex.Message}", ex);
            }

            result.Add(finding);
        }

        return result;
    }
}

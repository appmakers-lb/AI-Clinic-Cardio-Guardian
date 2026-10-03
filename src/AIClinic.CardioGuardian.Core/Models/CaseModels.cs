using System.Collections.ObjectModel;

namespace AIClinic.CardioGuardian.Core.Models;

public enum CoverageState
{
    Unassessed = 0,
    Partial = 1,
    Incomplete = 2,
    Adequate = 3
}

public enum FindingPriority
{
    Silent = 0,
    Information = 1,
    Review = 2,
    HighPriorityReview = 3
}

public enum FindingSource
{
    StructuredResearchModel = 0,
    ManualResearch = 1
}

public enum FindingStatus
{
    Proposed = 0,
    Confirmed = 1,
    Dismissed = 2
}

public sealed record NormalizedImageRegion(
    double XMin,
    double YMin,
    double XMax,
    double YMax)
{
    public void Validate()
    {
        var values = new[] { XMin, YMin, XMax, YMax };
        if (values.Any(value => double.IsNaN(value) || double.IsInfinity(value) || value is < 0 or > 1))
            throw new InvalidOperationException("Evidence region coordinates must be finite normalized values from 0 to 1.");

        if (XMax <= XMin || YMax <= YMin)
            throw new InvalidOperationException("Evidence region must have positive width and height.");
    }
}

public sealed record EvidenceReference(
    string SourceId,
    int? FrameStart,
    int? FrameEnd,
    string? Projection,
    string? Description,
    NormalizedImageRegion? Region = null)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SourceId))
            throw new InvalidOperationException("Evidence SourceId is required.");

        if (FrameStart is < 0 || FrameEnd is < 0)
            throw new InvalidOperationException("Evidence frame indexes cannot be negative.");

        if (FrameStart.HasValue && FrameEnd.HasValue && FrameEnd.Value < FrameStart.Value)
            throw new InvalidOperationException("Evidence FrameEnd cannot be before FrameStart.");

        Region?.Validate();
    }
}

public sealed class CoronarySegment
{
    public required string Vessel { get; init; }
    public required string Segment { get; init; }
    public CoverageState Coverage { get; private set; } = CoverageState.Unassessed;
    public string? CoverageNote { get; private set; }
    public DateTime? CoverageUpdatedAtUtc { get; private set; }

    internal void SetCoverage(CoverageState coverage, string? note)
    {
        Coverage = coverage;
        CoverageNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        CoverageUpdatedAtUtc = DateTime.UtcNow;
    }
}

public sealed class CineRun
{
    public required string Id { get; init; }
    public required string SourceKind { get; init; }
    public required string DisplayName { get; init; }
    public string? StudyInstanceUid { get; init; }
    public string? SeriesInstanceUid { get; init; }
    public string? Modality { get; init; }
    public string? Projection { get; init; }
    public int FrameCount { get; init; }
    public double FramesPerSecond { get; init; }
    public string? SourcePath { get; init; }
}

public sealed class GuardianFinding
{
    public required string Id { get; init; }
    public required string Vessel { get; init; }
    public required string Segment { get; init; }
    public required string FindingType { get; init; }
    public double Confidence { get; init; }
    public FindingPriority Priority { get; init; } = FindingPriority.Review;
    public FindingSource Source { get; init; } = FindingSource.StructuredResearchModel;
    public string? SourceVersion { get; init; }
    public IReadOnlyList<EvidenceReference> Evidence { get; init; } = Array.Empty<EvidenceReference>();
    public string? Explanation { get; init; }
    public string? MeasurementSummary { get; init; }
    public FindingStatus Status { get; private set; } = FindingStatus.Proposed;
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime? DispositionUpdatedAtUtc { get; private set; }

    public bool HasMultiRunEvidence =>
        Evidence.Select(x => x.SourceId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Skip(1)
            .Any();

    public void Confirm()
    {
        Status = FindingStatus.Confirmed;
        DispositionUpdatedAtUtc = DateTime.UtcNow;
    }

    public void Dismiss()
    {
        Status = FindingStatus.Dismissed;
        DispositionUpdatedAtUtc = DateTime.UtcNow;
    }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id))
            throw new InvalidOperationException("Finding Id is required.");
        if (string.IsNullOrWhiteSpace(Vessel))
            throw new InvalidOperationException("Finding vessel is required.");
        if (string.IsNullOrWhiteSpace(Segment))
            throw new InvalidOperationException("Finding segment is required.");
        if (string.IsNullOrWhiteSpace(FindingType))
            throw new InvalidOperationException("Finding type is required.");
        if (double.IsNaN(Confidence) || double.IsInfinity(Confidence) || Confidence is < 0 or > 1)
            throw new InvalidOperationException("Finding confidence must be between 0 and 1.");
        if (Evidence.Count == 0)
            throw new InvalidOperationException("A finding must contain at least one evidence reference.");

        if (Source == FindingSource.StructuredResearchModel &&
            string.IsNullOrWhiteSpace(SourceVersion))
        {
            throw new InvalidOperationException(
                "Structured research-model findings require a model ID/version.");
        }

        foreach (var evidence in Evidence)
            evidence.Validate();
    }
}

public sealed class CaseState
{
    private readonly List<CoronarySegment> _segments = CreateDefaultSegments();
    private readonly List<CineRun> _cineRuns = new();
    private readonly List<GuardianFinding> _findings = new();

    public IReadOnlyList<CoronarySegment> Segments => _segments;
    public IReadOnlyList<CineRun> CineRuns => _cineRuns;
    public IReadOnlyList<GuardianFinding> Findings => _findings;

    public CoronarySegment GetSegment(string vessel, string segment)
    {
        var match = _segments.FirstOrDefault(x =>
            string.Equals(x.Vessel, vessel, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Segment, segment, StringComparison.OrdinalIgnoreCase));

        return match ?? throw new KeyNotFoundException(
            $"Unknown tracked coronary segment: {vessel} {segment}.");
    }

    public void SetCoverage(
        string vessel,
        string segment,
        CoverageState coverage,
        string? note = null)
    {
        GetSegment(vessel, segment).SetCoverage(coverage, note);
    }

    public IReadOnlyList<CoronarySegment> IncompleteOrUnassessed() =>
        _segments.Where(x => x.Coverage != CoverageState.Adequate).ToArray();

    public void AddCineRun(CineRun run)
    {
        if (string.IsNullOrWhiteSpace(run.Id))
            throw new InvalidOperationException("Cine run Id is required.");

        if (run.FrameCount < 0)
            throw new InvalidOperationException("Cine frame count cannot be negative.");

        if (run.FramesPerSecond < 0 || double.IsNaN(run.FramesPerSecond) || double.IsInfinity(run.FramesPerSecond))
            throw new InvalidOperationException("Cine frame rate is invalid.");

        var existing = _cineRuns.FirstOrDefault(x =>
            string.Equals(x.Id, run.Id, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
            return;

        _cineRuns.Add(run);
    }

    public void AddFinding(GuardianFinding finding)
    {
        finding.Validate();

        if (_findings.Any(x => string.Equals(x.Id, finding.Id, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Finding '{finding.Id}' already exists.");

        foreach (var evidence in finding.Evidence)
        {
            if (!_cineRuns.Any(x =>
                string.Equals(x.Id, evidence.SourceId, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"Finding '{finding.Id}' references unknown evidence source '{evidence.SourceId}'.");
            }
        }

        _findings.Add(finding);
    }

    public IReadOnlyList<GuardianFinding> ActiveFindings() =>
        _findings.Where(x => x.Status != FindingStatus.Dismissed).ToArray();

    private static List<CoronarySegment> CreateDefaultSegments() =>
        new()
        {
            new() { Vessel = "LM", Segment = "main" },
            new() { Vessel = "LAD", Segment = "proximal" },
            new() { Vessel = "LAD", Segment = "mid" },
            new() { Vessel = "LAD", Segment = "distal" },
            new() { Vessel = "LCx", Segment = "proximal" },
            new() { Vessel = "LCx", Segment = "distal" },
            new() { Vessel = "RCA", Segment = "proximal" },
            new() { Vessel = "RCA", Segment = "mid" },
            new() { Vessel = "RCA", Segment = "distal" }
        };
}

public sealed class ImagingSeriesInfo
{
    public required string Id { get; init; }
    public string StudyInstanceUid { get; init; } = string.Empty;
    public string SeriesInstanceUid { get; init; } = string.Empty;
    public string PatientName { get; init; } = string.Empty;
    public string PatientId { get; init; } = string.Empty;
    public string StudyDate { get; init; } = string.Empty;
    public string StudyDescription { get; init; } = string.Empty;
    public string SeriesDescription { get; init; } = string.Empty;
    public string Modality { get; init; } = string.Empty;
    public string Projection { get; init; } = string.Empty;
    public int InstanceCount { get; init; }
    public int TotalFrames { get; init; }
    public double EstimatedFramesPerSecond { get; init; }
    public bool LikelyCoronaryAngiography { get; init; }
    public IReadOnlyList<string> FilePaths { get; init; } = Array.Empty<string>();

    public string DisplayName
    {
        get
        {
            var description = string.IsNullOrWhiteSpace(SeriesDescription)
                ? "DICOM series"
                : SeriesDescription.Trim();
            var modality = string.IsNullOrWhiteSpace(Modality) ? "?" : Modality.Trim();
            return $"{Id} | {modality} | {description} | {TotalFrames} frame(s)";
        }
    }
}

public sealed class DicomImportResult
{
    public required string SourceFolder { get; init; }
    public int FilesScanned { get; init; }
    public int DicomFilesOpened { get; init; }
    public int FilesSkipped { get; init; }
    public IReadOnlyList<ImagingSeriesInfo> Series { get; init; } = Array.Empty<ImagingSeriesInfo>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public sealed class StructuredFindingPackage
{
    public string ModelId { get; init; } = string.Empty;
    public string ModelVersion { get; init; } = string.Empty;
    public string? CaseId { get; init; }
    public string? GeneratedAtUtc { get; init; }
    public IReadOnlyList<StructuredFindingItem> Findings { get; init; } = Array.Empty<StructuredFindingItem>();
    public IReadOnlyList<StructuredCoverageItem> Coverage { get; init; } = Array.Empty<StructuredCoverageItem>();
}

public sealed class StructuredFindingItem
{
    public string Id { get; init; } = string.Empty;
    public string Vessel { get; init; } = string.Empty;
    public string Segment { get; init; } = string.Empty;
    public string FindingType { get; init; } = string.Empty;
    public double Confidence { get; init; }
    public string Priority { get; init; } = "Review";
    public string? Explanation { get; init; }
    public string? MeasurementSummary { get; init; }
    public IReadOnlyList<StructuredEvidenceItem> Evidence { get; init; } = Array.Empty<StructuredEvidenceItem>();
}

public sealed class StructuredEvidenceItem
{
    public string SourceId { get; init; } = string.Empty;
    public int? FrameStart { get; init; }
    public int? FrameEnd { get; init; }
    public string? Projection { get; init; }
    public string? Description { get; init; }
    public NormalizedImageRegion? Region { get; init; }
}

public sealed class StructuredCoverageItem
{
    public string Vessel { get; init; } = string.Empty;
    public string Segment { get; init; } = string.Empty;
    public string State { get; init; } = "Unassessed";
    public string? Note { get; init; }
    public IReadOnlyList<StructuredEvidenceItem> Evidence { get; init; } = Array.Empty<StructuredEvidenceItem>();
}

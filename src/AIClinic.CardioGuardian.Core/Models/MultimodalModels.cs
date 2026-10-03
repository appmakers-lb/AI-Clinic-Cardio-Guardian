namespace AIClinic.CardioGuardian.Core.Models;

public enum ResearchModality
{
    Angiography,
    Ffr,
    Ifr,
    AngiographyDerivedPhysiology,
    Ivus,
    Oct
}

public sealed record ResearchProvenance(
    string SourceId,
    string AdapterId,
    string AdapterVersion,
    ResearchModality Modality)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SourceId))
            throw new InvalidOperationException("Research modality SourceId is required.");
        if (string.IsNullOrWhiteSpace(AdapterId))
            throw new InvalidOperationException("Research modality AdapterId is required.");
        if (string.IsNullOrWhiteSpace(AdapterVersion))
            throw new InvalidOperationException("Research modality AdapterVersion is required.");
    }
}

public sealed record PhysiologyResearchMeasurement(
    string Id,
    string Vessel,
    string Segment,
    string Metric,
    double Value,
    string Unit,
    ResearchProvenance Provenance,
    IReadOnlyList<EvidenceReference> Evidence)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) ||
            string.IsNullOrWhiteSpace(Vessel) ||
            string.IsNullOrWhiteSpace(Segment) ||
            string.IsNullOrWhiteSpace(Metric))
        {
            throw new InvalidOperationException(
                "Physiology research measurement requires id, vessel, segment, and metric.");
        }

        if (double.IsNaN(Value) || double.IsInfinity(Value))
            throw new InvalidOperationException("Physiology research measurement value is invalid.");

        Provenance.Validate();

        if (Evidence.Count == 0)
            throw new InvalidOperationException(
                "Physiology research measurement requires evidence provenance.");

        foreach (var evidence in Evidence)
            evidence.Validate();
    }
}

public sealed record IntravascularResearchEvidence(
    string Id,
    string Vessel,
    string Segment,
    ResearchProvenance Provenance,
    string? MeasurementSummary,
    IReadOnlyList<EvidenceReference> Evidence)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) ||
            string.IsNullOrWhiteSpace(Vessel) ||
            string.IsNullOrWhiteSpace(Segment))
        {
            throw new InvalidOperationException(
                "Intravascular research evidence requires id, vessel, and segment.");
        }

        if (Provenance.Modality is not (ResearchModality.Ivus or ResearchModality.Oct))
            throw new InvalidOperationException(
                "Intravascular research evidence modality must be IVUS or OCT.");

        Provenance.Validate();

        if (Evidence.Count == 0)
            throw new InvalidOperationException(
                "Intravascular research evidence requires evidence references.");

        foreach (var evidence in Evidence)
            evidence.Validate();
    }
}

public sealed record MultimodalDisagreementNote(
    string Vessel,
    string Segment,
    string Description,
    IReadOnlyList<string> SourceIds)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Vessel) ||
            string.IsNullOrWhiteSpace(Segment) ||
            string.IsNullOrWhiteSpace(Description))
        {
            throw new InvalidOperationException(
                "Multimodal disagreement requires vessel, segment, and description.");
        }

        if (SourceIds.Count < 2 ||
            SourceIds.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException(
                "Multimodal disagreement requires at least two explicit source IDs.");
        }
    }
}

using AIClinic.CardioGuardian.Core.Models;

namespace AIClinic.CardioGuardian.Core.Services;

public sealed record QcaResearchMeasurement(
    string Id,
    string Vessel,
    string Segment,
    double? ReferenceDiameterMm,
    double? MinimumLumenDiameterMm,
    double? DiameterStenosisPercent,
    double? LesionLengthMm,
    string? CalibrationSource,
    double? UncertaintyPercent,
    ResearchProvenance Provenance,
    IReadOnlyList<EvidenceReference> Evidence)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) ||
            string.IsNullOrWhiteSpace(Vessel) ||
            string.IsNullOrWhiteSpace(Segment))
        {
            throw new InvalidOperationException(
                "QCA research measurement requires id, vessel, and segment.");
        }

        ValidateNonNegative(ReferenceDiameterMm, nameof(ReferenceDiameterMm));
        ValidateNonNegative(MinimumLumenDiameterMm, nameof(MinimumLumenDiameterMm));
        ValidateNonNegative(LesionLengthMm, nameof(LesionLengthMm));
        ValidateNonNegative(UncertaintyPercent, nameof(UncertaintyPercent));

        if (DiameterStenosisPercent is double stenosis &&
            (double.IsNaN(stenosis) || double.IsInfinity(stenosis) ||
             stenosis is < 0 or > 100))
        {
            throw new InvalidOperationException(
                "QCA diameter stenosis percent must be between 0 and 100.");
        }

        var hasPhysicalMeasurement =
            ReferenceDiameterMm.HasValue ||
            MinimumLumenDiameterMm.HasValue ||
            LesionLengthMm.HasValue;

        if (hasPhysicalMeasurement && string.IsNullOrWhiteSpace(CalibrationSource))
        {
            throw new InvalidOperationException(
                "Physical QCA measurements require an explicit calibration source.");
        }

        Provenance.Validate();

        if (Evidence.Count == 0)
            throw new InvalidOperationException(
                "QCA research measurement requires evidence references.");

        foreach (var evidence in Evidence)
            evidence.Validate();
    }

    private static void ValidateNonNegative(double? value, string name)
    {
        if (value is not double actual)
            return;

        if (double.IsNaN(actual) || double.IsInfinity(actual) || actual < 0)
            throw new InvalidOperationException($"{name} must be finite and non-negative.");
    }
}

public interface IQcaResearchAdapter
{
    string ModelId { get; }
    string ModelVersion { get; }

    Task<IReadOnlyList<QcaResearchMeasurement>> AnalyzeAsync(
        ImagingSeriesInfo series,
        CancellationToken cancellationToken = default);
}

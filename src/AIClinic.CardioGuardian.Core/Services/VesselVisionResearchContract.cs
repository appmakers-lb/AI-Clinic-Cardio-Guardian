using AIClinic.CardioGuardian.Core.Models;

namespace AIClinic.CardioGuardian.Core.Services;

public sealed record NormalizedImagePoint(double X, double Y)
{
    public void Validate()
    {
        if (double.IsNaN(X) || double.IsNaN(Y) ||
            double.IsInfinity(X) || double.IsInfinity(Y) ||
            X is < 0 or > 1 || Y is < 0 or > 1)
        {
            throw new InvalidOperationException(
                "Normalized image coordinates must be finite values between 0 and 1.");
        }
    }
}

public sealed record VesselFrameResearchEvidence(
    string SourceId,
    int FrameIndex,
    string VesselLabel,
    double Confidence,
    string ModelId,
    string ModelVersion,
    IReadOnlyList<NormalizedImagePoint> Centerline)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SourceId) ||
            string.IsNullOrWhiteSpace(VesselLabel) ||
            string.IsNullOrWhiteSpace(ModelId) ||
            string.IsNullOrWhiteSpace(ModelVersion))
        {
            throw new InvalidOperationException(
                "Vessel research evidence requires source, label, model ID, and model version.");
        }

        if (FrameIndex < 0)
            throw new InvalidOperationException("Vessel research frame index cannot be negative.");

        if (double.IsNaN(Confidence) || double.IsInfinity(Confidence) ||
            Confidence is < 0 or > 1)
        {
            throw new InvalidOperationException(
                "Vessel research confidence must be between 0 and 1.");
        }

        foreach (var point in Centerline)
            point.Validate();
    }
}

public interface IVesselVisionResearchAdapter
{
    string ModelId { get; }
    string ModelVersion { get; }

    Task<IReadOnlyList<VesselFrameResearchEvidence>> AnalyzeAsync(
        ImagingSeriesInfo series,
        CancellationToken cancellationToken = default);
}

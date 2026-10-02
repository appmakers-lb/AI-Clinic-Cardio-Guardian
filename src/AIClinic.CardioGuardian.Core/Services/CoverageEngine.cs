using System.IO;
using AIClinic.CardioGuardian.Core.Models;

namespace AIClinic.CardioGuardian.Core.Services;

public sealed record CoverageApplyResult(
    int Applied,
    int Rejected,
    IReadOnlyList<string> Messages);

public sealed class CoverageEngine
{
    public CoverageApplyResult ApplyStructuredCoverage(
        CaseState state,
        StructuredFindingPackage package)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(package);

        var applied = 0;
        var rejected = 0;
        var messages = new List<string>();

        foreach (var item in package.Coverage)
        {
            if (!Enum.TryParse<CoverageState>(item.State, true, out var coverageState))
            {
                rejected++;
                messages.Add(
                    $"{item.Vessel} {item.Segment}: unsupported coverage state '{item.State}'.");
                continue;
            }

            if (coverageState == CoverageState.Unassessed)
            {
                rejected++;
                messages.Add(
                    $"{item.Vessel} {item.Segment}: model output cannot convert a segment back to Unassessed.");
                continue;
            }

            if (item.Evidence is null || item.Evidence.Count == 0)
            {
                rejected++;
                messages.Add(
                    $"{item.Vessel} {item.Segment}: coverage requires evidence references.");
                continue;
            }

            try
            {
                foreach (var evidence in item.Evidence)
                new EvidenceReference(
                    evidence.SourceId,
                    evidence.FrameStart,
                    evidence.FrameEnd,
                    evidence.Projection,
                    evidence.Description).Validate();

                state.SetCoverage(
                    item.Vessel,
                    item.Segment,
                    coverageState,
                    $"Structured research coverage from {package.ModelId}:{package.ModelVersion}. " +
                    (item.Note?.Trim() ?? string.Empty));

                applied++;
            }
            catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
            {
                rejected++;
                messages.Add($"{item.Vessel} {item.Segment}: {ex.Message}");
            }
        }

        return new(applied, rejected, messages);
    }
}

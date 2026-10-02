using AIClinic.CardioGuardian.Core.Models;

namespace AIClinic.CardioGuardian.Core.Services;

public interface IPhysiologyResearchAdapter
{
    string AdapterId { get; }
    string AdapterVersion { get; }

    Task<IReadOnlyList<PhysiologyResearchMeasurement>> LoadAsync(
        string sourcePath,
        CancellationToken cancellationToken = default);
}

public interface IIntravascularResearchAdapter
{
    string AdapterId { get; }
    string AdapterVersion { get; }
    ResearchModality Modality { get; }

    Task<IReadOnlyList<IntravascularResearchEvidence>> LoadAsync(
        string sourcePath,
        CancellationToken cancellationToken = default);
}

public sealed class MultimodalEvidenceService
{
    public IReadOnlyList<string> Validate(
        IEnumerable<PhysiologyResearchMeasurement> physiology,
        IEnumerable<IntravascularResearchEvidence> intravascular,
        IEnumerable<MultimodalDisagreementNote>? disagreements = null)
    {
        var errors = new List<string>();

        foreach (var item in physiology)
        {
            try { item.Validate(); }
            catch (InvalidOperationException ex) { errors.Add($"{item.Id}: {ex.Message}"); }
        }

        foreach (var item in intravascular)
        {
            try { item.Validate(); }
            catch (InvalidOperationException ex) { errors.Add($"{item.Id}: {ex.Message}"); }
        }

        if (disagreements is not null)
        {
            foreach (var item in disagreements)
            {
                try { item.Validate(); }
                catch (InvalidOperationException ex)
                {
                    errors.Add($"{item.Vessel} {item.Segment}: {ex.Message}");
                }
            }
        }

        return errors;
    }
}

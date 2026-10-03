using AIClinic.CardioGuardian.Core.Models;

namespace AIClinic.CardioGuardian.Core.Services;

public sealed record GuardianAlertDecision(
    bool ShouldAlert,
    string Reason,
    bool IsCrossVessel);

public sealed class GuardianPolicy
{
    public GuardianAlertDecision EvaluateAlert(
        GuardianFinding finding,
        bool voiceEnabled,
        string? focusedVessel = null)
    {
        if (!voiceEnabled)
            return new(false, "Voice output is disabled.", IsCrossVessel(finding, focusedVessel));

        if (finding.Status != FindingStatus.Proposed)
            return new(false, $"Finding status is {finding.Status}.", IsCrossVessel(finding, focusedVessel));

        if (finding.Priority != FindingPriority.HighPriorityReview)
            return new(false, $"Priority is {finding.Priority}.", IsCrossVessel(finding, focusedVessel));

        if (finding.Confidence < 0.80)
            return new(false, "Confidence is below the research alert threshold.", IsCrossVessel(finding, focusedVessel));

        if (finding.Evidence.Count == 0)
            return new(false, "No evidence bundle is attached.", IsCrossVessel(finding, focusedVessel));

        return new(
            true,
            IsCrossVessel(finding, focusedVessel)
                ? "High-priority evidence-backed cross-vessel research alert."
                : "High-priority evidence-backed research alert.",
            IsCrossVessel(finding, focusedVessel));
    }

    public bool ShouldVoiceAlert(GuardianFinding finding, bool voiceEnabled) =>
        EvaluateAlert(finding, voiceEnabled).ShouldAlert;

    public string CoverageSummary(CaseState state)
    {
        var gaps = state.IncompleteOrUnassessed();
        if (gaps.Count == 0)
            return "All tracked coronary segments have an assessment state. This is not a clinical clearance statement.";

        return "Incomplete/unassessed tracked segments: " +
               string.Join(", ", gaps.Select(x => $"{x.Vessel} {x.Segment}"));
    }

    public string FindingSummary(CaseState state)
    {
        var active = state.ActiveFindings();
        if (active.Count == 0)
            return "No structured research findings are loaded for this case.";

        return string.Join(Environment.NewLine,
            active.Select(f =>
                $"{f.Vessel} {f.Segment}: {f.FindingType} — {f.Confidence:P0} — {f.Priority}"));
    }

    public string SafeNoModelResponse() =>
        "No validated medical vision model is connected. The application will not infer a stenosis, occlusion, CTO, or treatment decision from raw images by itself.";

    private static bool IsCrossVessel(GuardianFinding finding, string? focusedVessel) =>
        !string.IsNullOrWhiteSpace(focusedVessel) &&
        !string.Equals(finding.Vessel, focusedVessel, StringComparison.OrdinalIgnoreCase);
}

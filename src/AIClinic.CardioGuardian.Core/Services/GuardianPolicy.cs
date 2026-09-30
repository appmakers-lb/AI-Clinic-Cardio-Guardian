using AIClinic.CardioGuardian.Core.Models;

namespace AIClinic.CardioGuardian.Core.Services;

public sealed class GuardianPolicy
{
    public bool ShouldVoiceAlert(GuardianFinding finding, bool voiceEnabled) =>
        voiceEnabled &&
        finding.Status == FindingStatus.Proposed &&
        finding.Priority == FindingPriority.HighPriorityReview &&
        finding.Confidence >= 0.80 &&
        finding.Evidence.Count > 0;

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
            active.Select(f => $"{f.Vessel} {f.Segment}: {f.FindingType} — {f.Confidence:P0} — {f.Priority}"));
    }

    public string SafeNoModelResponse() =>
        "No validated medical vision model is connected. The application will not infer a stenosis, occlusion, CTO, or treatment decision from raw images by itself.";
}

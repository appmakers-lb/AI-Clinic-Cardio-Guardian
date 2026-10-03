using AIClinic.CardioGuardian.Core.Models;

namespace AIClinic.CardioGuardian.Core.Services;

public sealed class GuardianAlertTracker
{
    private readonly Dictionary<string, DateTime> _lastAlertUtc =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly TimeSpan _suppressionWindow;

    public GuardianAlertTracker(TimeSpan? suppressionWindow = null)
    {
        _suppressionWindow = suppressionWindow ?? TimeSpan.FromMinutes(2);
    }

    public bool TryAcquire(
        GuardianFinding finding,
        DateTime utcNow,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(finding);

        var key = BuildKey(finding);
        if (_lastAlertUtc.TryGetValue(key, out var last) &&
            utcNow - last < _suppressionWindow)
        {
            reason = "Duplicate Guardian alert suppressed inside the alert window.";
            return false;
        }

        _lastAlertUtc[key] = utcNow;
        reason = "Guardian alert acquired.";
        return true;
    }

    public void Reset() => _lastAlertUtc.Clear();

    private static string BuildKey(GuardianFinding finding)
    {
        var firstEvidence = finding.Evidence.FirstOrDefault();
        return string.Join("|",
            finding.SourceVersion?.Trim(),
            finding.Vessel.Trim(),
            finding.Segment.Trim(),
            finding.FindingType.Trim(),
            firstEvidence?.SourceId?.Trim(),
            firstEvidence?.FrameStart?.ToString(),
            firstEvidence?.FrameEnd?.ToString());
    }
}

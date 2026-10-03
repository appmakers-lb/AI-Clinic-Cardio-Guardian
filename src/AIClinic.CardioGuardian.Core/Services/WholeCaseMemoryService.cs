using System.Security.Cryptography;
using System.Text;
using AIClinic.CardioGuardian.Core.Models;

namespace AIClinic.CardioGuardian.Core.Services;

public enum FindingAddStatus
{
    Added,
    DuplicateId,
    DuplicateEvidence,
    Invalid
}

public sealed record FindingAddResult(
    GuardianFinding Finding,
    FindingAddStatus Status,
    string Message);

public sealed class WholeCaseMemoryService
{
    public FindingAddResult TryAddFinding(CaseState state, GuardianFinding finding)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(finding);

        try
        {
            finding.Validate();
        }
        catch (InvalidOperationException ex)
        {
            return new(finding, FindingAddStatus.Invalid, ex.Message);
        }

        if (state.Findings.Any(x =>
            string.Equals(x.Id, finding.Id, StringComparison.OrdinalIgnoreCase)))
        {
            return new(finding, FindingAddStatus.DuplicateId,
                $"Finding id '{finding.Id}' already exists.");
        }

        var fingerprint = BuildEvidenceFingerprint(finding);
        if (state.Findings.Any(x =>
            string.Equals(BuildEvidenceFingerprint(x), fingerprint, StringComparison.Ordinal)))
        {
            return new(finding, FindingAddStatus.DuplicateEvidence,
                "An equivalent evidence-linked finding is already present in the whole-case memory.");
        }

        try
        {
            state.AddFinding(finding);
            return new(finding, FindingAddStatus.Added, "Finding added to whole-case memory.");
        }
        catch (InvalidOperationException ex)
        {
            return new(finding, FindingAddStatus.Invalid, ex.Message);
        }
    }

    public IReadOnlyList<FindingAddResult> AddPackage(
        CaseState state,
        IEnumerable<GuardianFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        return findings.Select(x => TryAddFinding(state, x)).ToArray();
    }

    public IReadOnlyDictionary<string, IReadOnlyList<GuardianFinding>> BuildEvidenceIndex(
        CaseState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return state.Findings
            .SelectMany(f => f.Evidence.Select(e => new { Finding = f, Evidence = e }))
            .GroupBy(x => x.Evidence.SourceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<GuardianFinding>)g.Select(x => x.Finding)
                    .DistinctBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);
    }

    public string BuildEvidenceFingerprint(GuardianFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);

        var evidence = finding.Evidence
            .OrderBy(x => x.SourceId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.FrameStart)
            .ThenBy(x => x.FrameEnd)
            .Select(x =>
                $"{Normalize(x.SourceId)}:{x.FrameStart?.ToString() ?? "?"}:{x.FrameEnd?.ToString() ?? "?"}");

        var canonical = string.Join("|",
            Normalize(finding.SourceVersion),
            Normalize(finding.Vessel),
            Normalize(finding.Segment),
            Normalize(finding.FindingType),
            string.Join(",", evidence));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash);
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToUpperInvariant();
}

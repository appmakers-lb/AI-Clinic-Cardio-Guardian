using AIClinic.CardioGuardian.Core.Models;
using AIClinic.CardioGuardian.Core.Services;

static void Assert(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    Console.WriteLine("PASS: " + name);
}

var c = new CaseState();
Assert(c.Segments.Count == 9, "default coronary segment count");

c.SetCoverage("LAD", "mid", CoverageState.Adequate);
Assert(c.GetSegment("LAD", "mid").Coverage == CoverageState.Adequate, "manual coverage update");

c.AddCineRun(new CineRun
{
    Id = "SER-1",
    SourceKind = "DICOM",
    DisplayName = "Test cine",
    FrameCount = 60,
    FramesPerSecond = 15
});
c.AddCineRun(new CineRun
{
    Id = "SER-1",
    SourceKind = "DICOM",
    DisplayName = "Duplicate cine",
    FrameCount = 60,
    FramesPerSecond = 15
});
Assert(c.CineRuns.Count == 1, "duplicate cine run suppressed");

var policy = new GuardianPolicy();
Assert(policy.CoverageSummary(c).Contains("RCA"), "coverage summary lists incomplete/unassessed segments");

var blocked = false;
try
{
    c.AddFinding(new GuardianFinding
    {
        Id = "F-001",
        Vessel = "LAD",
        Segment = "mid",
        FindingType = "suspected_stenosis",
        Confidence = 0.91,
        Priority = FindingPriority.HighPriorityReview,
        Source = FindingSource.StructuredResearchModel,
        SourceVersion = "",
        Evidence = new[] { new EvidenceReference("SER-1", 10, 20, "RAO", "Research evidence") }
    });
}
catch (InvalidOperationException) { blocked = true; }
Assert(blocked, "unversioned structured model finding blocked");

var invalidConfidenceBlocked = false;
try
{
    c.AddFinding(new GuardianFinding
    {
        Id = "F-BAD-CONF",
        Vessel = "LAD",
        Segment = "mid",
        FindingType = "suspected_stenosis",
        Confidence = 1.5,
        Priority = FindingPriority.Review,
        Source = FindingSource.StructuredResearchModel,
        SourceVersion = "test:1",
        Evidence = new[] { new EvidenceReference("SER-1", 1, 2, "RAO", "test") }
    });
}
catch (InvalidOperationException) { invalidConfidenceBlocked = true; }
Assert(invalidConfidenceBlocked, "out-of-range confidence blocked");

var manual = new GuardianFinding
{
    Id = "MAN-1",
    Vessel = "LAD",
    Segment = "mid",
    FindingType = "suspected_stenosis",
    Confidence = 0.9,
    Priority = FindingPriority.Review,
    Source = FindingSource.ManualResearch,
    SourceVersion = "physician-manual-research",
    Evidence = new[] { new EvidenceReference("SER-1", 10, 10, "RAO", "Manual label") }
};
c.AddFinding(manual);
Assert(c.Findings.Count == 1, "manual research annotation accepted with evidence");

var finding = new GuardianFinding
{
    Id = "F-002",
    Vessel = "RCA",
    Segment = "proximal",
    FindingType = "suspected_occlusion",
    Confidence = 0.91,
    Priority = FindingPriority.HighPriorityReview,
    Source = FindingSource.StructuredResearchModel,
    SourceVersion = "research-model:1.0",
    Evidence = new[]
    {
        new EvidenceReference("SER-1", 10, 20, "RAO", "Research evidence"),
        new EvidenceReference("SER-2", 4, 9, "LAO", "Second view")
    }
};
c.AddFinding(finding);

Assert(policy.ShouldVoiceAlert(finding, true), "high-priority evidence-backed alert eligible for voice");
Assert(finding.HasMultiRunEvidence, "multi-run evidence recognized");
Assert(policy.FindingSummary(c).Contains("RCA"), "finding summary includes structured finding");

var crossVessel = policy.EvaluateAlert(finding, true, "LAD");
Assert(crossVessel.ShouldAlert && crossVessel.IsCrossVessel, "cross-vessel Guardian alert recognized");

var service = new StructuredFindingService();
var package = service.Parse("""
{
  "modelId": "test-model",
  "modelVersion": "1.2.3",
  "findings": [
    {
      "id": "F-003",
      "vessel": "LCx",
      "segment": "proximal",
      "findingType": "suspected_stenosis",
      "confidence": 0.82,
      "priority": "Review",
      "explanation": "Synthetic unit-test payload",
      "evidence": [
        { "sourceId": "SER-1", "frameStart": 1, "frameEnd": 5, "projection": "LAO", "description": "test" }
      ]
    }
  ]
}
""");

var converted = service.ToGuardianFindings(package);
Assert(converted.Count == 1 && converted[0].SourceVersion == "test-model:1.2.3", "structured finding import is versioned");

var noModelBlocked = false;
try
{
    service.Parse("""{"modelId":"NO_MODEL","modelVersion":"0","findings":[]}""");
}
catch (InvalidDataException) { noModelBlocked = true; }
Assert(noModelBlocked, "NO_MODEL package import blocked");

var badEvidenceBlocked = false;
try
{
    var bad = service.Parse("""
    {
      "modelId":"test-model",
      "modelVersion":"1",
      "findings":[{
        "id":"F-BAD",
        "vessel":"RCA",
        "segment":"mid",
        "findingType":"suspected_stenosis",
        "confidence":0.8,
        "priority":"Review",
        "evidence":[{"sourceId":"SER-1","frameStart":5,"frameEnd":2,"description":"bad"}]
      }]
    }
    """);
    service.ToGuardianFindings(bad);
}
catch (InvalidDataException) { badEvidenceBlocked = true; }
Assert(badEvidenceBlocked, "invalid evidence frame range blocked");

Console.WriteLine("All core safety tests passed.");

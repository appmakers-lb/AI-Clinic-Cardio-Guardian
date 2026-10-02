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


var memory = new WholeCaseMemoryService();
var duplicateByEvidence = new GuardianFinding
{
    Id = "F-002-NEW-ID",
    Vessel = "RCA",
    Segment = "proximal",
    FindingType = "suspected_occlusion",
    Confidence = 0.91,
    Priority = FindingPriority.HighPriorityReview,
    Source = FindingSource.StructuredResearchModel,
    SourceVersion = "research-model:1.0",
    Evidence = new[]
    {
        new EvidenceReference("SER-1", 10, 20, "RAO", "Same evidence"),
        new EvidenceReference("SER-2", 4, 9, "LAO", "Same second view")
    }
};
var duplicateResult = memory.TryAddFinding(c, duplicateByEvidence);
Assert(duplicateResult.Status == FindingAddStatus.DuplicateEvidence, "whole-case semantic duplicate suppressed");

var evidenceIndex = memory.BuildEvidenceIndex(c);
Assert(evidenceIndex.ContainsKey("SER-1") && evidenceIndex["SER-1"].Count >= 2,
    "whole-case evidence index maps cine to findings");

var coveragePackage = service.Parse("""
{
  "modelId":"coverage-model",
  "modelVersion":"2.0",
  "findings":[],
  "coverage":[{
    "vessel":"RCA",
    "segment":"mid",
    "state":"Partial",
    "note":"Synthetic coverage test",
    "evidence":[{"sourceId":"SER-1","frameStart":1,"frameEnd":6,"description":"test"}]
  }]
}
""");
var coverageEngine = new CoverageEngine();
var coverageResult = coverageEngine.ApplyStructuredCoverage(c, coveragePackage);
Assert(coverageResult.Applied == 1 &&
       c.GetSegment("RCA", "mid").Coverage == CoverageState.Partial,
       "structured coverage applied only with evidence");

var unsafeCoveragePackage = service.Parse("""
{
  "modelId":"coverage-model",
  "modelVersion":"2.0",
  "findings":[],
  "coverage":[{
    "vessel":"RCA",
    "segment":"distal",
    "state":"Adequate",
    "note":"No evidence should be rejected",
    "evidence":[]
  }]
}
""");
var unsafeCoverage = coverageEngine.ApplyStructuredCoverage(c, unsafeCoveragePackage);
Assert(unsafeCoverage.Rejected == 1 &&
       c.GetSegment("RCA", "distal").Coverage == CoverageState.Unassessed,
       "coverage cannot become adequate without evidence");

var alertTracker = new GuardianAlertTracker(TimeSpan.FromMinutes(2));
Assert(alertTracker.TryAcquire(finding, DateTime.UtcNow, out _), "first Guardian alert acquired");
Assert(!alertTracker.TryAcquire(finding, DateTime.UtcNow.AddSeconds(5), out _),
    "duplicate Guardian alert suppressed");

var voiceParser = new VoiceCommandParser();
Assert(voiceParser.Parse("next cine").Intent == VoiceIntent.NextCine,
    "voice next-cine intent parsed");
Assert(voiceParser.Parse("show me why").Intent == VoiceIntent.ShowEvidence,
    "voice evidence intent parsed");
Assert(voiceParser.Parse("do something dangerous").Intent == VoiceIntent.Unknown,
    "unsupported free-form voice command rejected");


var adequateCoveragePackage = service.Parse("""
{
  "modelId":"coverage-model",
  "modelVersion":"2.0",
  "findings":[],
  "coverage":[{
    "vessel":"RCA",
    "segment":"mid",
    "state":"Adequate",
    "note":"Conflicting later observation",
    "evidence":[{"sourceId":"SER-2","frameStart":2,"frameEnd":8,"description":"test"}]
  }]
}
""");
coverageEngine.ApplyStructuredCoverage(c, adequateCoveragePackage);
Assert(c.GetSegment("RCA", "mid").Coverage == CoverageState.Partial,
    "coverage merge stays conservative when partial conflicts with adequate");

var physiology = new PhysiologyResearchMeasurement(
    "PHY-1",
    "LAD",
    "mid",
    "research_ratio",
    0.85,
    "ratio",
    new ResearchProvenance("PHY-SOURCE", "test-adapter", "1.0", ResearchModality.Ffr),
    new[] { new EvidenceReference("SER-1", 1, 3, "RAO", "linked research evidence") });

var ivus = new IntravascularResearchEvidence(
    "IVUS-1",
    "LAD",
    "mid",
    new ResearchProvenance("IVUS-SOURCE", "test-ivus", "1.0", ResearchModality.Ivus),
    "Synthetic research measurement",
    new[] { new EvidenceReference("IVUS-SOURCE", 1, 2, null, "synthetic evidence") });

var multimodal = new MultimodalEvidenceService();
Assert(multimodal.Validate(new[] { physiology }, new[] { ivus }).Count == 0,
    "multimodal research contracts validate with explicit provenance");

Console.WriteLine("All core safety tests passed.");

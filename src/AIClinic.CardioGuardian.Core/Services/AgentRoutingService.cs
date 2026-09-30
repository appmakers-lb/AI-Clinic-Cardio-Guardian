namespace AIClinic.CardioGuardian.Core.Services;

public sealed record AgentAssignment(string AgentId, string Scope, string PromptFile);

public sealed class AgentRoutingService
{
    private static readonly AgentAssignment[] Agents =
    {
        new("A01", "Competitor/product intelligence", "A01_COMPETITOR_PRODUCT.md"),
        new("A02", "Windows platform and packaging", "A02_WINDOWS_PLATFORM.md"),
        new("A03", "Imaging, cine, DICOM/XA, PACS adapters", "A03_IMAGING_DICOM.md"),
        new("A04", "Vessel segmentation and image quality", "A04_VESSEL_VISION.md"),
        new("A05", "Lesion localization and QCA", "A05_LESION_QCA.md"),
        new("A06", "Multi-view lesion identity and case memory", "A06_MULTIVIEW_MEMORY.md"),
        new("A07", "Guardian Mode, coverage, abstention", "A07_GUARDIAN_COVERAGE.md"),
        new("A08", "Grounded voice copilot", "A08_VOICE_COPILOT.md"),
        new("A09", "Physiology, IVUS/OCT fusion", "A09_PHYSIOLOGY_FUSION.md"),
        new("A10", "Human factors and Cath-Lab UX", "A10_HUMAN_FACTORS_UX.md"),
        new("A11", "Security, regulatory, traceability", "A11_SECURITY_REGULATORY.md"),
        new("A12", "Validation and QA gates", "A12_VALIDATION_QA.md")
    };

    public IReadOnlyList<AgentAssignment> GetAssignments() => Agents;
}

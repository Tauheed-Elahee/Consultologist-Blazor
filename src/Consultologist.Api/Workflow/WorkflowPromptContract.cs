namespace Consultologist.Api.Workflow;

/// <summary>
/// The normative prompt/variable contract for specVersion 2 packages.
/// See docs/customizable-workflow/package-format-v2.md.
/// </summary>
public static class WorkflowPromptContract
{
    // Prompt ids (the set is closed in v2)
    public const string ExtractPatientConcepts = "extract-patient-concepts";
    public const string IdentifyProblem = "identify-problem";
    public const string CreateTypicalTrajectory = "create-typical-trajectory";
    public const string CreatePatientTrajectory = "create-patient-trajectory";
    public const string StandardSectionDraft = "standard-section-draft";
    public const string PatientSectionDraft = "patient-section-draft";
    public const string SectionInstructions = "section-instructions";

    public static readonly IReadOnlySet<string> RequiredPromptIds = new HashSet<string>(StringComparer.Ordinal)
    {
        ExtractPatientConcepts,
        IdentifyProblem,
        CreateTypicalTrajectory,
        CreatePatientTrajectory,
        StandardSectionDraft,
        PatientSectionDraft,
        SectionInstructions
    };

    // Variable names
    public const string ConsultDraft = "consult_draft";
    public const string SectionName = "section_name";
    public const string SectionStandard = "section_standard";
    public const string StandardSectionDraftVariable = "standard_section_draft";
    public const string PatientSectionDraftVariable = "patient_section_draft";
    public const string PatientConcepts = "patient_concepts";
    public const string ProblemConcepts = "problem_concepts";
    public const string TypicalTrajectoryConcepts = "typical_trajectory_concepts";
    public const string PatientTrajectoryConcepts = "patient_trajectory_concepts";

    public static readonly IReadOnlySet<string> KnownVariables = new HashSet<string>(StringComparer.Ordinal)
    {
        ConsultDraft,
        SectionName,
        SectionStandard,
        StandardSectionDraftVariable,
        PatientSectionDraftVariable,
        PatientConcepts,
        ProblemConcepts,
        TypicalTrajectoryConcepts,
        PatientTrajectoryConcepts
    };
}

namespace Consultologist.Api.Workflow;

/// <summary>
/// The normative prompt/variable contract for specVersion 2 and 3 packages.
/// See docs/customizable-workflow/package-format-v2.md and package-format-v3.md.
/// </summary>
public static class WorkflowPromptContract
{
    // Prompt ids (the full set is closed in v2; only the analysis set stays closed in v3)
    public const string ExtractPatientConcepts = "extract-patient-concepts";
    public const string IdentifyProblem = "identify-problem";
    public const string CreateTypicalTrajectory = "create-typical-trajectory";
    public const string CreatePatientTrajectory = "create-patient-trajectory";
    public const string StandardSectionDraft = "standard-section-draft";
    public const string PatientSectionDraft = "patient-section-draft";
    public const string SectionInstructions = "section-instructions";

    /// <summary>
    /// The four analysis-stage prompts. Required in every specVersion; closed until the
    /// analysis DAG generalizes (milestone 4).
    /// </summary>
    public static readonly IReadOnlySet<string> AnalysisPromptIds = new HashSet<string>(StringComparer.Ordinal)
    {
        ExtractPatientConcepts,
        IdentifyProblem,
        CreateTypicalTrajectory,
        CreatePatientTrajectory
    };

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

/// <summary>
/// The closed vocabulary of engine binding sources a specVersion-3 section step may
/// bind its template variables to (docs/customizable-workflow/package-format-v3.md).
/// </summary>
public static class WorkflowStepBindingSources
{
    public const string ConsultDraft = "consult_draft";
    public const string SectionName = "section_name";
    public const string SectionStandard = "section_standard";
    public const string PatientTrajectoryConcepts = "patient_trajectory_concepts";
    public const string PreviousStepOutput = "previous_step_output";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        ConsultDraft,
        SectionName,
        SectionStandard,
        PatientTrajectoryConcepts,
        PreviousStepOutput
    };
}

/// <summary>
/// The step list the engine synthesizes for specVersion-2 packages, which predate
/// manifest-declared section steps. Normative in package-format-v3.md — the canonical
/// three-step pipeline v2 compiled into the engine.
/// </summary>
public static class WorkflowSectionStepDefaults
{
    public static readonly IReadOnlyList<WorkflowSectionStepSpec> V2Synthesized = new[]
    {
        new WorkflowSectionStepSpec(
            WorkflowPromptContract.StandardSectionDraft,
            "Drafting section",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [WorkflowPromptContract.SectionName] = WorkflowStepBindingSources.SectionName,
                [WorkflowPromptContract.PatientTrajectoryConcepts] = WorkflowStepBindingSources.PatientTrajectoryConcepts
            }),
        new WorkflowSectionStepSpec(
            WorkflowPromptContract.PatientSectionDraft,
            "Applying patient information",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [WorkflowPromptContract.StandardSectionDraftVariable] = WorkflowStepBindingSources.PreviousStepOutput,
                [WorkflowPromptContract.ConsultDraft] = WorkflowStepBindingSources.ConsultDraft,
                [WorkflowPromptContract.SectionName] = WorkflowStepBindingSources.SectionName
            }),
        new WorkflowSectionStepSpec(
            WorkflowPromptContract.SectionInstructions,
            "Applying section instructions",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [WorkflowPromptContract.PatientSectionDraftVariable] = WorkflowStepBindingSources.PreviousStepOutput,
                [WorkflowPromptContract.SectionName] = WorkflowStepBindingSources.SectionName,
                [WorkflowPromptContract.SectionStandard] = WorkflowStepBindingSources.SectionStandard
            })
    };
}

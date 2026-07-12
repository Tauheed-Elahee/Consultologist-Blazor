namespace Consultologist.Api.Jobs;

/// <summary>
/// The compiled fallback prompts for the four analysis stages — used when a job has no
/// specVersion-2 package (see IWorkflowPromptProvider). Each builder takes the same
/// pre-rendered string values the package templates receive, so the parity tests can
/// compare template output against this baseline directly.
/// </summary>
internal static class ConsultGenerationCompiledPrompts
{
    public static string ExtractPatientConcepts(string consultDraft)
    {
        return ConsultGenerationPreprocessingRunner.SnomedToolGuidance + "\n\n" + $"""
            Extract patient-specific clinical concepts from the draft consult note.

            Output only SNOMED concept bullets in these exact forms:
            - term (type) - id number
            - term [not SNOMED concept]
            - term (type) - id number [not active SNOMED concept]

            Include inactive SNOMED concepts when relevant. Include clinically important findings that are not SNOMED concepts using [not SNOMED concept].
            Do not include commentary, headings, JSON, or non-bullet lines.

            Draft consult note:
            {consultDraft}
            """;
    }

    public static string IdentifyProblem(string patientConcepts)
    {
        return ConsultGenerationPreprocessingRunner.SnomedToolGuidance + "\n\n" + $"""
            Identify the primary disease or problem concept from the validated patient concepts.

            Output only one or more SNOMED concept bullets in these exact forms:
            - term (type) - id number
            - term [not SNOMED concept]
            - term (type) - id number [not active SNOMED concept]

            Prefer the disease/problem driving the oncology consult. Do not include commentary, headings, JSON, or non-bullet lines.

            Validated patient concepts:
            {patientConcepts}
            """;
    }

    public static string CreateTypicalTrajectory(string problemConcepts)
    {
        return ConsultGenerationPreprocessingRunner.SnomedToolGuidance + "\n\n" + $"""
            Build a typical clinical trajectory for the disease/problem concept.

            Output only SNOMED concept bullets in these exact forms:
            - term (type) - id number
            - term [not SNOMED concept]
            - term (type) - id number [not active SNOMED concept]

            Include a concise support phrase after the accepted bullet only when needed by appending " -- support: ...".
            Do not include commentary, headings, JSON, or non-bullet lines.

            Disease/problem concept:
            {problemConcepts}
            """;
    }

    public static string CreatePatientTrajectory(string problemConcepts, string patientConcepts, string typicalTrajectoryConcepts)
    {
        return ConsultGenerationPreprocessingRunner.SnomedToolGuidance + "\n\n" + $"""
            Reconcile a patient-specific trajectory from the validated patient concepts and typical trajectory.

            Output only SNOMED concept bullets in these exact forms:
            - term (type) - id number
            - term [not SNOMED concept]
            - term (type) - id number [not active SNOMED concept]

            Include only patient-specific trajectory details supported by validated patient concepts. Do not add typical trajectory details unless supported by patient concepts.
            Include a concise support phrase after the accepted bullet only when needed by appending " -- support: ...".
            Do not include commentary, headings, JSON, or non-bullet lines.

            Disease/problem concept:
            {problemConcepts}

            Validated patient concepts:
            {patientConcepts}

            Typical trajectory concepts:
            {typicalTrajectoryConcepts}
            """;
    }
}

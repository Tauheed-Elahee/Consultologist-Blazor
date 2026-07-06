using System.Text.Json.Serialization;

namespace Consultologist.Api.Models;

public record ConsultGenerationRequest(
    string ConsultDraft,
    IReadOnlyList<ConsultGenerationSectionRequest> Sections);

public record ConsultGenerationSectionRequest(
    string Id,
    string Name,
    string Standard);

public record ConsultGenerationResponse(
    Dictionary<string, string> GeneratedSections,
    Dictionary<string, string> FailedSections,
    bool Success);

public record ConsultGenerationJobStartResponse(
    string JobId,
    string StatusUrl);

public record ConsultGenerationJobResponse(
    string JobId,
    string AppUserId,
    string Status,
    int TotalSectionCount,
    int CompletedSectionCount,
    int FailedSectionCount,
    Dictionary<string, string> GeneratedSections,
    Dictionary<string, string> FailedSections,
    bool Success,
    int? SchemaVersion = null,
    string? AnalysisStatus = null,
    string? AnalysisError = null,
    [property: JsonIgnore]
    IReadOnlyList<ClinicalConcept>? PatientConcepts = null,
    [property: JsonIgnore]
    IReadOnlyList<ClinicalConcept>? ProblemContext = null,
    [property: JsonIgnore]
    IReadOnlyList<ClinicalConcept>? TypicalTrajectoryConcepts = null,
    [property: JsonIgnore]
    IReadOnlyList<ClinicalConcept>? PatientTrajectoryConcepts = null,
    int? CompletedStageCount = null,
    int? TotalStageCount = null,
    [property: JsonIgnore]
    IReadOnlyList<ConsultGenerationValidationWarning>? ValidationWarnings = null,
    IReadOnlyDictionary<string, ConsultGenerationSectionProseProgress>? SectionProseProgress = null,
    string? RuntimeFailureStage = null,
    string? RuntimeFailureError = null,
    DateTimeOffset? CreatedAtUtc = null,
    DateTimeOffset? StartedAtUtc = null,
    DateTimeOffset? CompletedAtUtc = null,
    IReadOnlyList<JobHistoryEvent>? History = null);

public record JobHistoryEvent(string Kind, string Label, string? Detail, DateTimeOffset OccurredAt);

public record SectionGenerationResult(
    string SectionId,
    string SectionName,
    bool Success,
    string? GeneratedText,
    string? Error);

public sealed record ClinicalConcept(
    string Term,
    string Type,
    string Id,
    bool IsSnomedConcept,
    bool IsActive,
    string Source,
    string? Support = null);

public sealed record ConsultGenerationValidationWarning(
    string Stage,
    int DroppedLineCount,
    string Reason);

public sealed record ConsultGenerationSectionProseProgress(
    string SectionId,
    string SectionName,
    string? ProseStepStatus,
    int CompletedProseStepCount,
    int TotalProseStepCount);

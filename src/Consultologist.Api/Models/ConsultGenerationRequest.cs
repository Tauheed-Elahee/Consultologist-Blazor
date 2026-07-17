using System.Text.Json.Serialization;

namespace Consultologist.Api.Models;

public record ConsultGenerationRequest(
    string ConsultDraft,
    string? WorkflowPackage = null);

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
    int? CompletedStageCount = null,
    int? TotalStageCount = null,
    IReadOnlyDictionary<string, ConsultGenerationSectionProseProgress>? SectionProseProgress = null,
    string? RuntimeFailureStage = null,
    string? RuntimeFailureError = null,
    DateTimeOffset? CreatedAtUtc = null,
    DateTimeOffset? StartedAtUtc = null,
    DateTimeOffset? CompletedAtUtc = null,
    IReadOnlyList<JobHistoryEvent>? History = null,
    string? WorkflowPackage = null,
    string? EffectiveInputHash = null,
    IReadOnlyList<ConsultSectionStepDescriptor>? SectionSteps = null,
    IReadOnlyList<ConsultNodeDescriptor>? Nodes = null,
    IReadOnlyDictionary<string, ConsultGenerationNodeStatusResponse>? NodeOutputs = null,
    IReadOnlyDictionary<string, string>? AgentVersions = null,
    int? EffectiveInputHashVersion = null,
    string? CatalogRef = null,
    string? WorkflowOutputHash = null,
    int? WorkflowOutputHashVersion = null);

/// <summary>
/// The identity and display label of one per-section prose step, snapshotted from the
/// job's workflow package at start.
/// </summary>
public sealed record ConsultSectionStepDescriptor(string Id, string Label);

/// <summary>
/// One node of the job's workflow DAG, snapshotted from the pinned package at start —
/// the orchestrator's whole worldview of the graph (Durable replay never re-reads the
/// registry for shape).
/// </summary>
public sealed record ConsultNodeDescriptor(
    string Id,
    string Label,
    string? PromptId = null,
    IReadOnlyDictionary<string, ConsultNodeBindingDescriptor>? Bindings = null,
    string? OutputContract = null,
    string? FailIfEmpty = null,
    string? ForEach = null,
    string? ConceptSource = null);

public sealed record ConsultNodeBindingDescriptor(string From, string? As = null);

/// <summary>
/// Per-node run status and provenance exposed on the job response — the hashes form
/// the step-level verification chain (dag-improvements #6). Concepts stay off the
/// wire; they live in entity state.
/// </summary>
public sealed record ConsultGenerationNodeStatusResponse(
    string NodeId,
    string Label,
    string Status,
    string? InputHash = null,
    string? OutputHash = null,
    DateTimeOffset? CompletedAtUtc = null,
    string? Error = null);

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

public sealed record ConsultGenerationSectionProseProgress(
    string SectionId,
    string SectionName,
    string? ProseStepStatus,
    int CompletedProseStepCount,
    int TotalProseStepCount);

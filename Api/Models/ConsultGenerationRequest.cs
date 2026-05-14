namespace Api.Models;

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
    string Status,
    int TotalSectionCount,
    int CompletedSectionCount,
    int FailedSectionCount,
    Dictionary<string, string> GeneratedSections,
    Dictionary<string, string> FailedSections,
    bool Success);

public record SectionGenerationResult(
    string SectionId,
    string SectionName,
    bool Success,
    string? GeneratedText,
    string? Error);

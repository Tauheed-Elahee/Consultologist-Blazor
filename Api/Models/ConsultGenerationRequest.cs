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

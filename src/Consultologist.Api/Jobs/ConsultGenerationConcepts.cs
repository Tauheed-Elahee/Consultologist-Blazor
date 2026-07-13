using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Net.ServerSentEvents;
using Consultologist.Api.Agents;
using Consultologist.Api.Auth;
using Consultologist.Api.Models;
using Consultologist.Api.Workflow;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Entities;
using Microsoft.Extensions.Logging;

namespace Consultologist.Api.Jobs;

public static class ConsultGenerationConceptParser
{
    private static readonly Regex ActiveConceptPattern = new(
        @"^- (?<term>.+?) \((?<type>[^)]+)\) - (?<id>\d+)(?<inactive> \[not active SNOMED concept\])?(?<support> -- support: .+)?$",
        RegexOptions.Compiled);

    private static readonly Regex NonSnomedConceptPattern = new(
        @"^- (?<term>.+?) \[not SNOMED concept\](?<support> -- support: .+)?$",
        RegexOptions.Compiled);

    public static ConceptExtractionResult Parse(string rawText, string source, string warningStage)
    {
        var concepts = new List<ClinicalConcept>();
        var droppedLines = new List<string>();

        foreach (var line in rawText.Replace("\r\n", "\n").Split('\n').Select(value => value.Trim()).Where(value => value.Length > 0))
        {
            // Bare lines and "*" bullets are intentionally rejected so agent output cannot drift into ambiguous prose.
            if (!line.StartsWith("- ", StringComparison.Ordinal))
            {
                droppedLines.Add(line);
                continue;
            }

            var activeMatch = ActiveConceptPattern.Match(line);
            if (activeMatch.Success)
            {
                concepts.Add(new ClinicalConcept(
                    activeMatch.Groups["term"].Value.Trim(),
                    activeMatch.Groups["type"].Value.Trim(),
                    activeMatch.Groups["id"].Value.Trim(),
                    true,
                    !activeMatch.Groups["inactive"].Success,
                    source,
                    ExtractSupport(activeMatch.Groups["support"].Value)));
                continue;
            }

            var nonSnomedMatch = NonSnomedConceptPattern.Match(line);
            if (nonSnomedMatch.Success)
            {
                concepts.Add(new ClinicalConcept(
                    nonSnomedMatch.Groups["term"].Value.Trim(),
                    "finding",
                    string.Empty,
                    false,
                    false,
                    source,
                    ExtractSupport(nonSnomedMatch.Groups["support"].Value)));
                continue;
            }

            droppedLines.Add(line);
        }

        foreach (var droppedLine in droppedLines)
        {
            Console.Error.WriteLine($"[SNOMEDParser] Dropped malformed raw line. Stage={warningStage}; Line={droppedLine}");
        }

        var warnings = droppedLines.Count == 0
            ? Array.Empty<ConsultGenerationValidationWarning>()
            : new[]
            {
                new ConsultGenerationValidationWarning(warningStage, droppedLines.Count, "Malformed SNOMED bullet")
            };

        Console.Error.WriteLine($"[SNOMEDParser] Stage={warningStage}; ValidConceptCount={concepts.Count}; DroppedMalformedConceptCount={droppedLines.Count}");
        return new ConceptExtractionResult(concepts, warnings);
    }

    private static string? ExtractSupport(string value)
    {
        const string marker = " -- support: ";
        return string.IsNullOrWhiteSpace(value) ? null : value.Replace(marker, string.Empty, StringComparison.Ordinal).Trim();
    }
}

public static class ConsultGenerationConceptFormatter
{
    public static string Format(IReadOnlyList<ClinicalConcept> concepts)
    {
        return concepts.Count == 0
            ? "(none)"
            : string.Join(Environment.NewLine, concepts.Select(FormatOne));
    }

    private static string FormatOne(ClinicalConcept concept)
    {
        if (!concept.IsSnomedConcept)
        {
            return $"- {concept.Term} [not SNOMED concept]";
        }

        var inactive = concept.IsActive ? string.Empty : " [not active SNOMED concept]";
        return $"- {concept.Term} ({concept.Type}) - {concept.Id}{inactive}";
    }
}

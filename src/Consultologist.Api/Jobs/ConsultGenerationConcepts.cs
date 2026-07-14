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

using System.Net;
using Consultologist.Api.Auth;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace Consultologist.Api.Documents;

/// <summary>
/// Reads a document and hands the text back for the clinician to look at
/// before it becomes a consult (#235, docs/DOCUMENT_INPUT.md § 5).
///
/// A preview, and only a preview: it persists nothing and creates nothing.
/// The authoritative extraction happens again at job start (#238) over the
/// file the request carries, so what the server runs on is what the server
/// read — never what a client asserted. Extraction is deterministic for the
/// same bytes and the same pinned extractor, which is what makes this
/// preview honest rather than indicative.
///
/// The filename never arrives here. The client keeps it for its own label;
/// a filename can itself be PHI ("Smith_John_referral.pdf") and a
/// request-scoped one would land in Functions request logging.
/// </summary>
public sealed class DocumentExtractions
{
    private readonly ILogger<DocumentExtractions> _logger;
    private readonly IAccountAuthorizer _authorizer;

    public DocumentExtractions(ILogger<DocumentExtractions> logger, IAccountAuthorizer authorizer)
    {
        _logger = logger;
        _authorizer = authorizer;
    }

    [Function("CreateDocumentExtraction")]
    public async Task<HttpResponseData> CreateAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", "options", Route = "DocumentExtractions")] HttpRequestData req)
    {
        var cancellationToken = req.FunctionContext.CancellationToken;

        if (string.Equals(req.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            var optionsResponse = req.CreateResponse(HttpStatusCode.OK);
            FunctionCors.Apply(req, optionsResponse);
            return optionsResponse;
        }

        var account = await _authorizer.AuthorizeAsync(req, cancellationToken);

        if (account == null)
        {
            return AccountAuthorizer.CreateUnauthorizedResponse(req);
        }

        if (!AccountAuthorizer.IsActive(account))
        {
            return AccountAuthorizer.CreateForbiddenResponse(req);
        }

        using var buffer = new MemoryStream();
        await req.Body.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();

        var result = await DocumentExtraction.ExtractAsync(bytes, cancellationToken);

        // Lengths and dispositions only: no bytes, no extracted text, no
        // filename — there is no filename to log.
        _logger.LogInformation(
            "Document extraction {Outcome}. Bytes={Bytes}, Pages={Pages}, Characters={Characters}",
            result.Outcome,
            bytes.Length,
            result.PageCount,
            result.Text?.Length ?? 0);

        if (!DocumentExtraction.Succeeded(result))
        {
            return await CreateJsonResponseAsync(
                req,
                StatusFor(result.Outcome),
                new { error = DocumentExtractionCopy.For(result.Outcome), outcome = result.Outcome },
                cancellationToken);
        }

        return await CreateJsonResponseAsync(
            req,
            HttpStatusCode.OK,
            new DocumentExtractionResponse(result.Text!, result.ExtractorId!, result.PageCount),
            cancellationToken);
    }

    /// <summary>
    /// Refusals are not transport errors. The precedent is
    /// ConsultGenerationJobStartError.InputsMismatch, which returns 422 with
    /// the note that the request was well-formed but unsatisfiable — a
    /// scanned PDF is exactly that shape, and so is a corrupt one.
    /// </summary>
    private static HttpStatusCode StatusFor(string outcome) => outcome switch
    {
        DocumentExtractionOutcomes.UnsupportedType => HttpStatusCode.UnsupportedMediaType,
        DocumentExtractionOutcomes.TooLarge => HttpStatusCode.RequestEntityTooLarge,
        _ => HttpStatusCode.UnprocessableEntity
    };

    private static async Task<HttpResponseData> CreateJsonResponseAsync<T>(
        HttpRequestData req,
        HttpStatusCode statusCode,
        T payload,
        CancellationToken cancellationToken)
    {
        var response = req.CreateResponse(statusCode);
        FunctionCors.Apply(req, response);
        await response.WriteAsJsonAsync(payload, cancellationToken);
        return response;
    }
}

public sealed record DocumentExtractionResponse(string Text, string Extractor, int? PageCount);

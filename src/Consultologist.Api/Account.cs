using System.Net;
using System.Text.Json;
using Consultologist.Api.Auth;
using Consultologist.Api.Jobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace Consultologist.Api;

public sealed class Account
{
    private const int MaxSettingKeyLength = 128;
    private const int MaxSettingValueLength = 32_000;
    private const int MaxContentTypeLength = 128;
    private const int DefaultJobsLimit = 20;
    private const int MaxJobsLimit = 50;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly IAccountAuthorizer _authorizer;
    private readonly IAccountSettingsStore _settingsStore;
    private readonly IConsultGenerationJobIndexStore _jobIndexStore;

    public Account(
        IAccountAuthorizer authorizer,
        IAccountSettingsStore settingsStore,
        IConsultGenerationJobIndexStore jobIndexStore)
    {
        _authorizer = authorizer;
        _settingsStore = settingsStore;
        _jobIndexStore = jobIndexStore;
    }

    [Function("AccountMe")]
    public async Task<HttpResponseData> GetMeAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "options", Route = "Account/Me")] HttpRequestData req)
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

        var response = req.CreateResponse(HttpStatusCode.OK);
        FunctionCors.Apply(req, response);
        await response.WriteAsJsonAsync(
            new AccountMeResponse(
                account.AppUserId,
                account.DisplayName,
                account.Email,
                account.Status,
                account.CurrentIdentity,
                account.LinkedIdentities),
            cancellationToken);

        return response;
    }

    [Function("AccountJobsList")]
    public async Task<HttpResponseData> GetJobsAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "Account/Jobs")] HttpRequestData req)
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

        var (limit, continuationToken) = ParseJobsQueryParams(req.Url);

        var (jobs, nextToken) = await _jobIndexStore.ListAsync(
            account.AppUserId,
            limit,
            continuationToken,
            cancellationToken);

        var response = req.CreateResponse(HttpStatusCode.OK);
        FunctionCors.Apply(req, response);
        await response.WriteAsJsonAsync(
            new AccountJobsResponse(
                jobs.Select(j => new AccountJobSummaryResponse(
                    j.JobId,
                    j.Status,
                    j.CreatedAtUtc,
                    j.StartedAtUtc,
                    j.CompletedAtUtc,
                    j.TotalSectionCount,
                    j.CompletedSectionCount,
                    j.FailedSectionCount)).ToArray(),
                nextToken),
            cancellationToken);

        return response;
    }

    [Function("AccountSettingGet")]
    public async Task<HttpResponseData> GetSettingAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "Account/Settings/{key}")] HttpRequestData req,
        string key)
    {
        var cancellationToken = req.FunctionContext.CancellationToken;

        var validationError = ValidateSettingKey(key);
        if (validationError != null)
        {
            return await CreateTextResponseAsync(req, HttpStatusCode.BadRequest, validationError, cancellationToken);
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

        var setting = await _settingsStore.GetAsync(account.AppUserId, key, cancellationToken);
        if (setting == null)
        {
            return CreateNoContentLikeResponse(req, HttpStatusCode.NotFound);
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        FunctionCors.Apply(req, response);
        await response.WriteAsJsonAsync(
            new AccountSettingResponse(setting.Key, setting.Value, setting.ContentType, setting.UpdatedAtUtc),
            cancellationToken);

        return response;
    }

    [Function("AccountSettingSave")]
    public async Task<HttpResponseData> SaveSettingAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "Account/Settings/{key}")] HttpRequestData req,
        string key)
    {
        var cancellationToken = req.FunctionContext.CancellationToken;

        var validationError = ValidateSettingKey(key);
        if (validationError != null)
        {
            return await CreateTextResponseAsync(req, HttpStatusCode.BadRequest, validationError, cancellationToken);
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

        SaveAccountSettingRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<SaveAccountSettingRequest>(
                req.Body,
                JsonOptions,
                cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return await CreateTextResponseAsync(req, HttpStatusCode.BadRequest, "Invalid setting payload.", cancellationToken);
        }

        if (request?.Value == null)
        {
            return await CreateTextResponseAsync(req, HttpStatusCode.BadRequest, "Setting value is required.", cancellationToken);
        }

        if (request.Value.Length > MaxSettingValueLength)
        {
            return await CreateTextResponseAsync(req, HttpStatusCode.RequestEntityTooLarge, "Setting value is too large.", cancellationToken);
        }

        var contentType = string.IsNullOrWhiteSpace(request.ContentType)
            ? "text/plain"
            : request.ContentType.Trim();

        if (contentType.Length > MaxContentTypeLength)
        {
            return await CreateTextResponseAsync(req, HttpStatusCode.BadRequest, "Setting content type is too long.", cancellationToken);
        }

        await _settingsStore.SaveAsync(account.AppUserId, key, request.Value, contentType, cancellationToken);

        var response = req.CreateResponse(HttpStatusCode.NoContent);
        FunctionCors.Apply(req, response);
        return response;
    }

    [Function("AccountSettingDelete")]
    public async Task<HttpResponseData> DeleteSettingAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "Account/Settings/{key}")] HttpRequestData req,
        string key)
    {
        var cancellationToken = req.FunctionContext.CancellationToken;

        var validationError = ValidateSettingKey(key);
        if (validationError != null)
        {
            return await CreateTextResponseAsync(req, HttpStatusCode.BadRequest, validationError, cancellationToken);
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

        await _settingsStore.DeleteAsync(account.AppUserId, key, cancellationToken);

        var response = req.CreateResponse(HttpStatusCode.NoContent);
        FunctionCors.Apply(req, response);
        return response;
    }

    [Function("AccountSettingOptions")]
    public HttpResponseData OptionsSettingAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "options", Route = "Account/Settings/{key}")] HttpRequestData req,
        string key)
    {
        return CreateOptionsResponse(req);
    }

    private static (int Limit, string? ContinuationToken) ParseJobsQueryParams(Uri url)
    {
        var query = url.Query.TrimStart('?');
        var limit = DefaultJobsLimit;
        string? continuationToken = null;

        foreach (var segment in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = segment.IndexOf('=');
            if (eq < 0) continue;

            var key = Uri.UnescapeDataString(segment[..eq]);
            var value = Uri.UnescapeDataString(segment[(eq + 1)..]);

            if (string.Equals(key, "limit", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(value, out var parsed))
            {
                limit = Math.Clamp(parsed, 1, MaxJobsLimit);
            }
            else if (string.Equals(key, "continuationToken", StringComparison.OrdinalIgnoreCase) &&
                     !string.IsNullOrWhiteSpace(value))
            {
                continuationToken = value;
            }
        }

        return (limit, continuationToken);
    }

    private static string? ValidateSettingKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return "Setting key is required.";
        }

        if (key.Length > MaxSettingKeyLength)
        {
            return "Setting key is too long.";
        }

        foreach (var character in key)
        {
            if (char.IsLetterOrDigit(character) ||
                character is '.' or '_' or '-' or ':')
            {
                continue;
            }

            return "Setting key contains unsupported characters.";
        }

        return null;
    }

    private static HttpResponseData CreateOptionsResponse(HttpRequestData req)
    {
        var response = req.CreateResponse(HttpStatusCode.OK);
        FunctionCors.Apply(req, response);
        return response;
    }

    private static HttpResponseData CreateNoContentLikeResponse(HttpRequestData req, HttpStatusCode statusCode)
    {
        var response = req.CreateResponse(statusCode);
        FunctionCors.Apply(req, response);
        return response;
    }

    private static async Task<HttpResponseData> CreateTextResponseAsync(
        HttpRequestData req,
        HttpStatusCode statusCode,
        string message,
        CancellationToken cancellationToken)
    {
        var response = req.CreateResponse(statusCode);
        FunctionCors.Apply(req, response);
        await response.WriteStringAsync(message, cancellationToken);
        return response;
    }
}

public sealed record AccountJobSummaryResponse(
    string JobId,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    int TotalSectionCount,
    int CompletedSectionCount,
    int FailedSectionCount);

public sealed record AccountJobsResponse(
    IReadOnlyList<AccountJobSummaryResponse> Jobs,
    string? ContinuationToken);

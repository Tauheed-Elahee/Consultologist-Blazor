using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.Logging;

namespace Consultologist.Api.Email;

public sealed record GraphMessageRef(
    string Id,
    string? InternetMessageId,
    DateTimeOffset? ReceivedDateTime);

public sealed record GraphInternetMessageHeader(string Name, string Value);

public sealed record GraphMessage(
    string Id,
    string? InternetMessageId,
    string? FromAddress,
    string? BodyText,
    IReadOnlyList<GraphInternetMessageHeader> InternetMessageHeaders);

public interface IGraphMailClient
{
    Task<IReadOnlyList<GraphMessageRef>> ListUnreadInboxMessagesAsync(string mailbox, int top, CancellationToken cancellationToken);

    /// <summary>Full message with text body and internet headers; null on 404.</summary>
    Task<GraphMessage?> GetMessageAsync(string mailbox, string messageId, CancellationToken cancellationToken);

    Task MarkReadAsync(string mailbox, string messageId, CancellationToken cancellationToken);

    /// <summary>Resolves (creating if needed) an Inbox child folder id; memoized per process.</summary>
    Task<string> EnsureInboxChildFolderAsync(string mailbox, string displayName, CancellationToken cancellationToken);

    Task MoveMessageAsync(string mailbox, string messageId, string destinationFolderId, CancellationToken cancellationToken);

    Task SendMailAsync(
        string mailbox,
        string toAddress,
        string subject,
        string textBody,
        CancellationToken cancellationToken,
        GraphMailAttachment? attachment = null);
}

public sealed record GraphMailAttachment(string Name, byte[] Content);

/// <summary>
/// Raw Microsoft Graph REST (repo idiom — no Graph SDK) authenticated as the
/// user-assigned managed identity, whose Mail roles are restricted to the
/// consults mailbox by an Exchange application access policy (docs/ACCOUNTS.md).
/// </summary>
public sealed class GraphMailClient : IGraphMailClient
{
    private const string GraphBase = "https://graph.microsoft.com/v1.0";
    private static readonly string[] GraphScope = { "https://graph.microsoft.com/.default" };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TokenCredential _credential;
    private readonly ILogger<GraphMailClient> _logger;
    private readonly Dictionary<string, string> _folderIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _folderLock = new(1, 1);

    public GraphMailClient(
        IHttpClientFactory httpClientFactory,
        TokenCredential credential,
        ILogger<GraphMailClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _credential = credential;
        _logger = logger;
    }

    public async Task<IReadOnlyList<GraphMessageRef>> ListUnreadInboxMessagesAsync(string mailbox, int top, CancellationToken cancellationToken)
    {
        var url = $"{GraphBase}/users/{Uri.EscapeDataString(mailbox)}/mailFolders/inbox/messages"
            + $"?$filter=isRead eq false&$top={top}&$select=id,internetMessageId,receivedDateTime";

        using var document = await SendAsync(HttpMethod.Get, url, body: null, cancellationToken)
            ?? throw new InvalidOperationException("Graph message listing failed.");

        var refs = new List<GraphMessageRef>();

        foreach (var element in document.RootElement.GetProperty("value").EnumerateArray())
        {
            refs.Add(new GraphMessageRef(
                element.GetProperty("id").GetString()!,
                element.TryGetProperty("internetMessageId", out var imid) ? imid.GetString() : null,
                element.TryGetProperty("receivedDateTime", out var received) && received.ValueKind == JsonValueKind.String
                    ? received.GetDateTimeOffset()
                    : null));
        }

        return refs;
    }

    public async Task<GraphMessage?> GetMessageAsync(string mailbox, string messageId, CancellationToken cancellationToken)
    {
        var url = $"{GraphBase}/users/{Uri.EscapeDataString(mailbox)}/messages/{messageId}"
            + "?$select=id,internetMessageId,from,body,internetMessageHeaders";

        using var document = await SendAsync(
            HttpMethod.Get,
            url,
            body: null,
            cancellationToken,
            preferTextBody: true,
            tolerateNotFound: true);

        if (document == null)
        {
            return null;
        }

        var root = document.RootElement;

        var headers = new List<GraphInternetMessageHeader>();
        if (root.TryGetProperty("internetMessageHeaders", out var headerArray) && headerArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var header in headerArray.EnumerateArray())
            {
                var name = header.TryGetProperty("name", out var n) ? n.GetString() : null;
                var value = header.TryGetProperty("value", out var v) ? v.GetString() : null;
                if (name != null && value != null)
                {
                    headers.Add(new GraphInternetMessageHeader(name, value));
                }
            }
        }

        string? fromAddress = null;
        if (root.TryGetProperty("from", out var from)
            && from.TryGetProperty("emailAddress", out var emailAddress)
            && emailAddress.TryGetProperty("address", out var address))
        {
            fromAddress = address.GetString();
        }

        string? bodyText = null;
        if (root.TryGetProperty("body", out var bodyElement)
            && bodyElement.TryGetProperty("content", out var content))
        {
            bodyText = content.GetString();
        }

        return new GraphMessage(
            root.GetProperty("id").GetString()!,
            root.TryGetProperty("internetMessageId", out var messageInternetId) ? messageInternetId.GetString() : null,
            fromAddress,
            bodyText,
            headers);
    }

    public async Task MarkReadAsync(string mailbox, string messageId, CancellationToken cancellationToken)
    {
        var url = $"{GraphBase}/users/{Uri.EscapeDataString(mailbox)}/messages/{messageId}";
        using var _ = await SendAsync(new HttpMethod("PATCH"), url, "{\"isRead\":true}", cancellationToken, tolerateNotFound: true);
    }

    public async Task<string> EnsureInboxChildFolderAsync(string mailbox, string displayName, CancellationToken cancellationToken)
    {
        var cacheKey = $"{mailbox}|{displayName}";

        await _folderLock.WaitAsync(cancellationToken);
        try
        {
            if (_folderIds.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }

            var listUrl = $"{GraphBase}/users/{Uri.EscapeDataString(mailbox)}/mailFolders/inbox/childFolders"
                + $"?$filter=displayName eq '{displayName.Replace("'", "''")}'";

            using (var listDocument = await SendAsync(HttpMethod.Get, listUrl, body: null, cancellationToken))
            {
                var existing = listDocument?.RootElement.GetProperty("value").EnumerateArray().FirstOrDefault();
                if (existing is { ValueKind: JsonValueKind.Object })
                {
                    var id = existing.Value.GetProperty("id").GetString()!;
                    _folderIds[cacheKey] = id;
                    return id;
                }
            }

            var createUrl = $"{GraphBase}/users/{Uri.EscapeDataString(mailbox)}/mailFolders/inbox/childFolders";
            var createBody = JsonSerializer.Serialize(new { displayName });

            using var created = await SendAsync(HttpMethod.Post, createUrl, createBody, cancellationToken)
                ?? throw new InvalidOperationException($"Could not create mail folder '{displayName}'.");

            var createdId = created.RootElement.GetProperty("id").GetString()!;
            _folderIds[cacheKey] = createdId;
            return createdId;
        }
        finally
        {
            _folderLock.Release();
        }
    }

    public async Task MoveMessageAsync(string mailbox, string messageId, string destinationFolderId, CancellationToken cancellationToken)
    {
        var url = $"{GraphBase}/users/{Uri.EscapeDataString(mailbox)}/messages/{messageId}/move";
        var body = JsonSerializer.Serialize(new { destinationId = destinationFolderId });
        using var _ = await SendAsync(HttpMethod.Post, url, body, cancellationToken, tolerateNotFound: true);
    }

    public async Task SendMailAsync(
        string mailbox,
        string toAddress,
        string subject,
        string textBody,
        CancellationToken cancellationToken,
        GraphMailAttachment? attachment = null)
    {
        var url = $"{GraphBase}/users/{Uri.EscapeDataString(mailbox)}/sendMail";

        // Dictionaries rather than anonymous types: the attachment's mandatory
        // "@odata.type" is not a legal anonymous-property name. Inline
        // attachments cap around 3 MB of request — a consult PDF is far under.
        var message = new Dictionary<string, object?>
        {
            ["subject"] = subject,
            ["body"] = new { contentType = "Text", content = textBody },
            ["toRecipients"] = new[] { new { emailAddress = new { address = toAddress } } }
        };

        if (attachment != null)
        {
            message["attachments"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["@odata.type"] = "#microsoft.graph.fileAttachment",
                    ["name"] = attachment.Name,
                    ["contentType"] = "application/pdf",
                    ["contentBytes"] = Convert.ToBase64String(attachment.Content)
                }
            };
        }

        var body = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["message"] = message,
            ["saveToSentItems"] = true
        });

        var document = await SendAsync(HttpMethod.Post, url, body, cancellationToken);
        document?.Dispose();
    }

    private async Task<JsonDocument?> SendAsync(
        HttpMethod method,
        string url,
        string? body,
        CancellationToken cancellationToken,
        bool preferTextBody = false,
        bool tolerateNotFound = false)
    {
        var token = await _credential.GetTokenAsync(new TokenRequestContext(GraphScope), cancellationToken);

        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        if (preferTextBody)
        {
            request.Headers.Add("Prefer", "outlook.body-content-type=\"text\"");
        }

        if (body != null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);

        using var response = await client.SendAsync(request, cancellationToken);

        if (tolerateNotFound && response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            // Status only — Graph error bodies can echo message metadata.
            _logger.LogWarning(
                "Graph mail request failed. Method={Method}, StatusCode={StatusCode}",
                method.Method,
                (int)response.StatusCode);
            throw new InvalidOperationException($"Graph mail request failed with status {(int)response.StatusCode}.");
        }

        if (response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }
}

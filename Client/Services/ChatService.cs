using System.Net.Http.Json;
using BlazorStaticWebApps.Shared;

namespace BlazorStaticWebApps.Client.Services;

public class ChatService
{
    private readonly HttpClient _httpClient;
    private string? _currentThreadId;

    public ChatService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<ChatResponse> SendMessageAsync(string message)
    {
        try
        {
            var request = new ChatRequest
            {
                Message = message,
                ThreadId = _currentThreadId
            };

            var response = await _httpClient.PostAsJsonAsync("/api/chat", request);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                return new ChatResponse
                {
                    Success = false,
                    Error = $"Request failed: {response.StatusCode} - {errorContent}"
                };
            }

            var chatResponse = await response.Content.ReadFromJsonAsync<ChatResponse>();

            if (chatResponse != null && chatResponse.Success)
            {
                _currentThreadId = chatResponse.ThreadId;
            }

            return chatResponse ?? new ChatResponse
            {
                Success = false,
                Error = "Failed to parse response"
            };
        }
        catch (HttpRequestException ex)
        {
            return new ChatResponse
            {
                Success = false,
                Error = $"Network error: {ex.Message}"
            };
        }
        catch (Exception ex)
        {
            return new ChatResponse
            {
                Success = false,
                Error = $"Unexpected error: {ex.Message}"
            };
        }
    }

    public void ClearConversation()
    {
        _currentThreadId = null;
    }

    public bool HasActiveConversation => !string.IsNullOrEmpty(_currentThreadId);
}

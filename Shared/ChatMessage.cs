namespace BlazorStaticWebApps.Shared;

public class ChatMessage
{
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class ChatRequest
{
    public string Message { get; set; } = string.Empty;
    public string? ThreadId { get; set; }
}

public class ChatResponse
{
    public string Message { get; set; } = string.Empty;
    public string ThreadId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Error { get; set; }
}

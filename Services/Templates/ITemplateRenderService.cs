namespace BlazorWasm.Services.Templates;

public interface ITemplateRenderService
{
    /// <summary>
    /// Renders a Scriban/Liquid template with JSON data
    /// </summary>
    Task<TemplateRenderResult> RenderAsync(string templateContent, string jsonData);
}

public class TemplateRenderResult
{
    public bool Success { get; set; }
    public string? RenderedHtml { get; set; }
    public List<string> Errors { get; set; } = new();
    public TimeSpan RenderDuration { get; set; }
}

using Scriban;
using Scriban.Runtime;
using System.Text.Json;
using BlazorWasm.Extensions.Scriban;

namespace BlazorWasm.Services.Templates;

public class ScribanTemplateRenderService : ITemplateRenderService
{
    private readonly ILogger<ScribanTemplateRenderService> _logger;

    public ScribanTemplateRenderService(ILogger<ScribanTemplateRenderService> logger)
    {
        _logger = logger;
    }

    public async Task<TemplateRenderResult> RenderAsync(
        string templateContent,
        string jsonData)
    {
        var result = new TemplateRenderResult();
        var startTime = DateTime.UtcNow;

        try
        {
            // 1. Parse JSON data into dictionary for Scriban
            _logger.LogInformation("Parsing JSON data");
            var jsonDoc = JsonDocument.Parse(jsonData);
            var dataDict = JsonElementToDictionary(jsonDoc.RootElement);

            // 2. Compile template with Liquid compatibility mode
            _logger.LogInformation("Compiling Scriban template");
            var template = Template.ParseLiquid(templateContent);

            if (template.HasErrors)
            {
                result.Errors.AddRange(
                    template.Messages.Select(m => m.ToString()));
                _logger.LogError("Template compilation errors: {Errors}",
                    string.Join(", ", result.Errors));
                return result;
            }

            // 3. Setup Scriban context with custom functions and Liquid-like behavior
            var scriptObject = new ScriptObject();
            scriptObject.SetValue("EnableRelaxedMemberAccess", true, false);
            ScribanCustomFunctions.Register(scriptObject);
            scriptObject.Import(dataDict);

            var templateContext = new TemplateContext
            {
                StrictVariables = false,  // Don't throw on undefined variables
                EnableRelaxedMemberAccess = true,  // Allow null member access
                EnableRelaxedTargetAccess = true,  // Allow null target access
                MemberRenamer = member => member.Name
            };
            templateContext.PushGlobal(scriptObject);

            // 4. Render template
            _logger.LogInformation("Rendering template");
            result.RenderedHtml = await template.RenderAsync(templateContext);
            result.Success = true;

            _logger.LogInformation("Template rendered successfully, {Length} characters",
                result.RenderedHtml.Length);
        }
        catch (JsonException ex)
        {
            result.Errors.Add($"Invalid JSON data: {ex.Message}");
            _logger.LogError(ex, "JSON parsing failed");
        }
        catch (Exception ex)
        {
            result.Errors.Add($"Rendering failed: {ex.Message}");
            _logger.LogError(ex, "Template rendering failed");
        }
        finally
        {
            result.RenderDuration = DateTime.UtcNow - startTime;
        }

        return result;
    }

    private Dictionary<string, object?> JsonElementToDictionary(JsonElement element)
    {
        var dict = new Dictionary<string, object?>();

        foreach (var property in element.EnumerateObject())
        {
            dict[property.Name] = ConvertJsonElement(property.Value);
        }

        return dict;
    }

    private object? ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => JsonElementToDictionary(element),
            JsonValueKind.Array => element.EnumerateArray()
                .Select(ConvertJsonElement).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l)
                ? l
                : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.ToString()
        };
    }
}

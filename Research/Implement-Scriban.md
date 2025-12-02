# Implementation Plan: Scriban Template Rendering for Consults.razor

**Date**: 2025-12-01  
**Status**: Ready for Implementation  
**Scope**: Minimal implementation focused on single template rendering

---

## Objective

Replace the plain text display of `@aiResponse` (lines 70-72 in Consults.razor) with rendered HTML using Scriban templating engine, while keeping the architecture compatible with the future full template management system described in Migration-Scriban.md.

---

## Current State

### What Exists
- ✅ Scriban 6.5.2 installed in BlazorWasm.csproj
- ✅ JsonSchema.Net 7.2.3 installed in BlazorWasm.csproj
- ✅ Liquid template at `wwwroot/templates/consult_template.liquid`
- ✅ JSON Schema at `wwwroot/schemas/mortigen_render_context.schema.json`
- ✅ AI service returns JSON string in `aiResponse` variable
- ✅ Template loading pattern exists (`LoadConsultTemplateAsync` method)
- ✅ Empty service files created (need implementation):
  - `Services/Templates/ITemplateRenderService.cs`
  - `Services/Templates/ScribanTemplateRenderService.cs`
  - `Extensions/Scriban/ScribanCustomFunctions.cs`

### Current Workflow
1. User selects schema and enters consult draft
2. Schema is loaded from wwwroot
3. AI endpoint called with draft + schema
4. AI returns structured JSON string in `aiResponse`
5. **Currently**: Displayed as plain text in `<pre>` tag

---

## Implementation Approach

### Scope Decision: Minimal Implementation with Future-Proofing

**What We'll Build Now**:
- ✅ Template render service with Scriban
- ✅ Custom Scriban functions (`newline_to_br`)
- ✅ Auto-rendering after AI response
- ✅ Error handling with JSON fallback
- ✅ Service interface compatible with future expansion

**What We'll Build Later** (when needed):
- ⏭ Template storage service (localStorage/database)
- ⏭ Template builder UI (Templates.razor)
- ⏭ Multiple template management
- ⏭ Schema validation service
- ⏭ Template seeder for built-in templates

**Rationale**:
- User needs immediate rendering functionality
- Future template management system is planned
- Current service interface will remain compatible
- Minimal implementation = faster delivery and testing

---

## Architecture

### Component Structure
```
Consults.razor
  ↓ injects
ITemplateRenderService
  ↓ implements
ScribanTemplateRenderService
  ↓ uses
ScribanCustomFunctions (newline_to_br filter)
```

### Data Flow
```
1. User Input → AI Service
   consultDraft → AIService.InvokeAgentAsync()
   → Returns JSON string in aiResponse

2. Auto-Render Trigger
   aiResponse received
   → LoadConsultTemplateAsync() (existing method)
   → RenderConsultTemplateAsync() (new method)

3. Template Rendering
   JSON + Template → TemplateRenderService.RenderAsync()
   → Returns TemplateRenderResult with HTML

4. Display
   Success: Show rendered HTML in FluentCard
   Error: Show error message + collapsible raw JSON
```

---

## Implementation Steps

### Step 1: Implement Custom Scriban Functions

**File**: `Extensions/Scriban/ScribanCustomFunctions.cs`

**Important**: All standard Liquid filters used by the template must be implemented as custom functions in Scriban.

The template uses these filters:
- `newline_to_br` - Custom filter for converting newlines to `<br>` tags
- `default` - Provides fallback values for null/empty (e.g., `{{ value | default: 'fallback' }}`)
- `replace` - Replaces text (e.g., `{{ text | replace: 'old', 'new' }}`)
- `strip` - Trims whitespace
- `join` - Joins arrays with separator
- `downcase` - Converts to lowercase
- `capitalize` - Capitalizes first character
- `date` - Formats dates with strftime format conversion

```csharp
using Scriban;
using Scriban.Runtime;
using System.Globalization;

namespace BlazorWasm.Extensions.Scriban;

public static class ScribanCustomFunctions
{
    /// <summary>
    /// Converts newlines to HTML br tags
    /// Liquid: {{ text | newline_to_br }}
    /// </summary>
    public static string NewlineToBr(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        return text
            .Replace("\r\n", "<br>\n")
            .Replace("\n", "<br>\n")
            .Replace("\r", "<br>\n");
    }

    /// <summary>
    /// Provides a default value if the input is null or empty
    /// Liquid: {{ value | default: 'fallback' }}
    /// </summary>
    public static object? Default(object? value, object? defaultValue)
    {
        if (value == null)
            return defaultValue;

        if (value is string str && string.IsNullOrEmpty(str))
            return defaultValue;

        return value;
    }

    /// <summary>
    /// Replaces occurrences of a string with another string
    /// Liquid: {{ text | replace: 'old', 'new' }}
    /// </summary>
    public static string Replace(string? text, string oldValue, string newValue)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(oldValue))
            return text ?? string.Empty;

        return text.Replace(oldValue, newValue);
    }

    /// <summary>
    /// Removes leading and trailing whitespace
    /// Liquid: {{ text | strip }}
    /// </summary>
    public static string Strip(string? text)
    {
        return text?.Trim() ?? string.Empty;
    }

    /// <summary>
    /// Joins array elements with a separator
    /// Liquid: {{ array | join: ', ' }}
    /// </summary>
    public static string Join(object? array, string separator)
    {
        if (array == null)
            return string.Empty;

        if (array is System.Collections.IEnumerable enumerable and not string)
        {
            var items = new List<string>();
            foreach (var item in enumerable)
            {
                items.Add(item?.ToString() ?? string.Empty);
            }
            return string.Join(separator, items);
        }

        return array.ToString() ?? string.Empty;
    }

    /// <summary>
    /// Converts string to lowercase
    /// Liquid: {{ text | downcase }}
    /// </summary>
    public static string Downcase(string? text)
    {
        return text?.ToLowerInvariant() ?? string.Empty;
    }

    /// <summary>
    /// Capitalizes the first character of a string
    /// Liquid: {{ text | capitalize }}
    /// </summary>
    public static string Capitalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        if (text.Length == 1)
            return text.ToUpperInvariant();

        return char.ToUpperInvariant(text[0]) + text.Substring(1);
    }

    /// <summary>
    /// Formats a date string
    /// Liquid: {{ date | date: "%B %-d, %Y" }}
    /// Note: Converts strftime format to .NET DateTime format
    /// </summary>
    public static string Date(object? date, string format)
    {
        if (date == null)
            return string.Empty;

        DateTime dateTime;
        
        if (date is DateTime dt)
        {
            dateTime = dt;
        }
        else if (date is string dateStr && DateTime.TryParse(dateStr, out var parsed))
        {
            dateTime = parsed;
        }
        else
        {
            return date.ToString() ?? string.Empty;
        }

        // Convert strftime format to .NET format
        var dotnetFormat = format
            .Replace("%B", "MMMM")      // Full month name
            .Replace("%b", "MMM")       // Abbreviated month name
            .Replace("%d", "dd")        // Day with leading zero
            .Replace("%-d", "d")        // Day without leading zero
            .Replace("%Y", "yyyy")      // 4-digit year
            .Replace("%y", "yy")        // 2-digit year
            .Replace("%H", "HH")        // Hour (24-hour)
            .Replace("%M", "mm")        // Minute
            .Replace("%S", "ss")        // Second
            .Replace("%Z", "zzz");      // Timezone

        try
        {
            return dateTime.ToString(dotnetFormat, CultureInfo.InvariantCulture);
        }
        catch
        {
            return dateTime.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Register all custom functions with Scriban template context
    /// </summary>
    public static void Register(ScriptObject scriptObject)
    {
        scriptObject.Import("newline_to_br", new Func<string?, string>(NewlineToBr));
        scriptObject.Import("default", new Func<object?, object?, object?>(Default));
        scriptObject.Import("replace", new Func<string?, string, string, string>(Replace));
        scriptObject.Import("strip", new Func<string?, string>(Strip));
        scriptObject.Import("join", new Func<object?, string, string>(Join));
        scriptObject.Import("downcase", new Func<string?, string>(Downcase));
        scriptObject.Import("capitalize", new Func<string?, string>(Capitalize));
        scriptObject.Import("date", new Func<object?, string, string>(Date));
    }
}
```

---

### Step 2: Implement Template Render Service Interface

**File**: `Services/Templates/ITemplateRenderService.cs`

```csharp
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
```

---

### Step 3: Implement Template Render Service

**File**: `Services/Templates/ScribanTemplateRenderService.cs`

```csharp
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

            // 3. Setup Scriban context with custom functions
            var scriptObject = new ScriptObject();
            ScribanCustomFunctions.Register(scriptObject);
            scriptObject.Import(dataDict);

            var templateContext = new TemplateContext();
            templateContext.MemberRenamer = member => member.Name;
            templateContext.StrictVariables = false; // Allow null member access (Liquid-like behavior)
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
```

---

### Step 4: Register Service in Program.cs

**File**: `Program.cs`

**Location**: Add after line 28 (after the HttpClient registration for IAIEndpointService)

```csharp
// Register template rendering service
builder.Services.AddScoped<ITemplateRenderService, ScribanTemplateRenderService>();
```

---

### Step 5: Modify Consults.razor

**File**: `Pages/Consults.razor`

#### 5.1: Add using directive and injection (top of file)

After existing `@inject IAIEndpointService AIService` line, add:

```razor
@using BlazorWasm.Services.Templates
@inject ITemplateRenderService TemplateRenderService
```

#### 5.2: Add private fields in @code section

```csharp
private TemplateRenderResult? renderResult;
private bool isRenderLoading;
private string? renderError;
```

#### 5.3: Create new rendering method

```csharp
private async Task RenderConsultTemplateAsync()
{
    renderError = null;
    renderResult = null;

    if (string.IsNullOrEmpty(aiResponse) || string.IsNullOrEmpty(renderedTemplateContent))
    {
        renderError = "Template and AI response required for rendering";
        return;
    }

    try
    {
        isRenderLoading = true;
        renderResult = await TemplateRenderService.RenderAsync(
            renderedTemplateContent, 
            aiResponse);
    }
    catch (Exception ex)
    {
        renderError = $"Error rendering template: {ex.Message}";
    }
    finally
    {
        isRenderLoading = false;
    }
}
```

#### 5.4: Modify CreateAIRequestAsync method

**Update existing method** to auto-render after AI response:

```csharp
private async Task CreateAIRequestAsync()
{
    aiRequestError = null;
    aiResponse = null;
    renderResult = null; // Clear previous render

    if (!string.IsNullOrEmpty(consultDraft) && !string.IsNullOrEmpty(renderedSchemaContent))
    {
        try
        {
            isAiRequestLoading = true;
            aiResponse = await AIService.InvokeAgentAsync(consultDraft, renderedSchemaContent);
            
            // AUTO-RENDER: Load template and render after getting AI response
            if (!string.IsNullOrEmpty(aiResponse))
            {
                await LoadConsultTemplateAsync();
                await RenderConsultTemplateAsync();
            }
        }
        catch (Exception ex)
        {
            aiRequestError = $"Error calling agent: {ex.Message}";
        }
        finally
        {
            isAiRequestLoading = false;
        }
    }
    else
    {
        aiRequestError = "Require both Consult Draft and loaded Schema";
    }
}
```

#### 5.5: Replace display section (lines 70-72)

**REMOVE** the existing code:
```razor
@if (!string.IsNullOrEmpty(aiResponse))
{
	<div>
		<pre style="white-space: pre-wrap; word-wrap: break-word;">@aiResponse</pre>
	</div>
}
```

**REPLACE WITH**:
```razor
@if (isRenderLoading)
{
    <FluentProgressRing />
    <FluentLabel>Rendering template...</FluentLabel>
}
@if (!string.IsNullOrEmpty(renderError))
{
    <FluentMessageBar Intent="MessageIntent.Error">@renderError</FluentMessageBar>
}
@if (renderResult != null && !renderResult.Success)
{
    <FluentMessageBar Intent="MessageIntent.Error" Title="Template Rendering Errors">
        <ul style="margin: 0; padding-left: 20px;">
            @foreach (var error in renderResult.Errors)
            {
                <li>@error</li>
            }
        </ul>
    </FluentMessageBar>
    
    <details style="margin-top: 16px;">
        <summary style="cursor: pointer; color: #666;">Show Raw AI Response (JSON)</summary>
        <pre style="white-space: pre-wrap; word-wrap: break-word; font-size: 12px; margin-top: 8px; padding: 12px; background: #f5f5f5; border: 1px solid #ddd;">@aiResponse</pre>
    </details>
}
@if (renderResult?.Success == true && !string.IsNullOrEmpty(renderResult.RenderedHtml))
{
    <FluentCard>
        <FluentStack Orientation="Orientation.Horizontal">
            <FluentLabel Typo="Typography.H5" Style="flex: 1;">Consultation Note</FluentLabel>
            <FluentBadge Appearance="Appearance.Success">
                Rendered in @renderResult.RenderDuration.TotalMilliseconds ms
            </FluentBadge>
        </FluentStack>

        <div style="border: 1px solid #ccc; padding: 24px; background: white; margin-top: 16px; max-height: 600px; overflow-y: auto;">
            @((MarkupString)renderResult.RenderedHtml)
        </div>
    </FluentCard>
}
```

---

## Error Handling Strategy

### Template Parse Errors
- Display error messages in FluentMessageBar
- Show raw JSON in collapsible `<details>` section
- Log errors with detailed information

### JSON Parse Errors
- Catch `JsonException` specifically
- Display user-friendly error message
- Include raw response in collapsible section
- Log full exception details

### Missing/Invalid Data
- Scriban handles gracefully (null values render as empty)
- Template continues rendering with available data
- No errors thrown for missing optional fields

### Custom Function Errors
- Functions check for null/empty input
- Return empty string for invalid input
- No exceptions thrown from custom functions

---

## Testing Strategy

### Manual Test Cases

1. **Successful Render**
   - Input: Valid consult draft
   - Expected: AI returns JSON, template renders as HTML
   - Verify: All sections display correctly with formatting

2. **Error Handling - Invalid JSON**
   - Input: Manually break JSON structure in AI response
   - Expected: Error message + collapsible raw JSON
   - Verify: User can see what went wrong

3. **Error Handling - Missing Template**
   - Input: Rename template file temporarily
   - Expected: Error message about missing template
   - Verify: Graceful error display

4. **Edge Cases - Minimal JSON**
   - Input: JSON with only required fields
   - Expected: Template renders with available data
   - Verify: No errors for missing optional fields

5. **Edge Cases - Full JSON**
   - Input: JSON with all optional fields populated
   - Expected: Complete consultation note
   - Verify: All conditional sections display

6. **Performance**
   - Input: Typical consultation JSON (~5-10KB)
   - Expected: Render time < 200ms
   - Verify: Performance badge shows acceptable time

---

## Performance Considerations

### Current Implementation
- Template compilation: ~10-50ms (first compile)
- JSON parsing: ~5-20ms (typical 5-10KB JSON)
- Rendering: ~20-100ms (typical consult note)
- **Total expected**: < 200ms for complete cycle

### Future Optimization (when needed)
- Add template caching (hash-based dictionary)
- Reuse compiled templates across renders
- Avoid recompilation on every render

For current single-template use case, caching is not critical.

---

## Migration Path to Full Template System

When implementing the full template management system from Migration-Scriban.md:

### Phase 1 (Current) ✅
- Single hardcoded template path
- Direct template loading from wwwroot
- ITemplateRenderService interface established

### Phase 2 (Future)
- Add `ITemplateStorageService`
- Implement localStorage-based storage
- Support multiple templates

### Phase 3 (Future)
- Build Templates.razor (template builder UI)
- Add schema validation service
- Implement template seeder

### Phase 4 (Future)
- Migrate from localStorage to database
- Add versioning and audit trail
- Multi-user template sharing

**Key**: The `ITemplateRenderService` interface stays the same across all phases. Only the data source changes.

---

## Success Criteria

1. ✅ AI response JSON successfully renders as formatted HTML
2. ✅ Template filters (`newline_to_br`, etc.) work correctly
3. ✅ Auto-rendering triggers after AI response
4. ✅ Error messages display with collapsible raw JSON fallback
5. ✅ No breaking changes to existing AI workflow
6. ✅ Performance acceptable (< 200ms render time)
7. ✅ Code is compatible with future template management system

---

## Files Modified/Created

### New Files (3)
1. `Extensions/Scriban/ScribanCustomFunctions.cs` - Custom Scriban functions
2. `Services/Templates/ITemplateRenderService.cs` - Service interface and result model
3. `Services/Templates/ScribanTemplateRenderService.cs` - Core rendering implementation

### Modified Files (2)
1. `Program.cs` - Service registration (1 line added)
2. `Pages/Consults.razor` - Integration and UI updates (~50 lines changed)

---

## Implementation Checklist

- [ ] Implement `Extensions/Scriban/ScribanCustomFunctions.cs`
- [ ] Implement `Services/Templates/ITemplateRenderService.cs`
- [ ] Implement `Services/Templates/ScribanTemplateRenderService.cs`
- [ ] Update `Program.cs` with service registration
- [ ] Update `Pages/Consults.razor` - add using/inject
- [ ] Update `Pages/Consults.razor` - add private fields
- [ ] Update `Pages/Consults.razor` - add RenderConsultTemplateAsync method
- [ ] Update `Pages/Consults.razor` - modify CreateAIRequestAsync method
- [ ] Update `Pages/Consults.razor` - replace display section
- [ ] Build and verify no compilation errors
- [ ] Test successful render with valid data
- [ ] Test error handling with invalid JSON
- [ ] Test performance with typical consultation note
- [ ] Verify compatibility with future expansion plans

---

## Notes

- Template stays at `wwwroot/templates/consult_template.liquid` (no rename needed)
- Scriban's Liquid compatibility mode handles existing template syntax
- Service interface designed for future expansion to full template system
- Auto-rendering provides seamless user experience
- Collapsible JSON fallback aids debugging without cluttering UI

---

## Liquid Compatibility Configuration

### Key Scriban Settings for Liquid-like Behavior

To make Scriban behave like Liquid (relaxed/forgiving with null values), the following configuration is critical:

```csharp
var templateContext = new TemplateContext();
templateContext.MemberRenamer = member => member.Name;
templateContext.StrictVariables = false; // CRITICAL: Allow null member access
templateContext.PushGlobal(scriptObject);
```

**Why `StrictVariables = false` is necessary:**

- **Liquid behavior**: `{{ enc.datetime }}` on a null `enc` returns null/empty, template continues
- **Scriban default**: `{{ enc.datetime }}` on a null `enc` throws error: "Cannot get the member enc.datetime for a null object"
- **With `StrictVariables = false`**: Matches Liquid behavior - null member access returns null instead of throwing

This is essential for templates with optional fields and conditional checks like:
```liquid
{%- if enc.datetime -%}
  {{ enc.datetime | date: "%B %-d, %Y" }}
{%- endif -%}
```

### Custom Liquid Filters Required

All standard Liquid filters used by the template must be manually implemented as custom functions:

1. **`default`** - Provides fallback values (e.g., `{{ side | default: 'right' }}`)
2. **`replace`** - String replacement (e.g., `{{ stage_group | replace: 'Stage ', '' }}`)
3. **`strip`** - Trim whitespace
4. **`join`** - Join arrays (e.g., `{{ diagnosis | join: ', ' }}`)
5. **`downcase`** - Lowercase conversion
6. **`capitalize`** - Capitalize first letter
7. **`date`** - Date formatting with strftime→.NET format conversion
8. **`newline_to_br`** - Custom filter to convert newlines to `<br>` tags

**Important**: If the template uses a Liquid filter and it's not implemented in `ScribanCustomFunctions.cs`, you'll get a runtime error: "The function `[filter_name]` was not found"

### Debugging Missing Filters

If you encounter "function not found" errors:

1. Check the error message for the filter name: `error : The function 'xyz' was not found`
2. Search the template for usage: `grep '| xyz' wwwroot/templates/consult_template.liquid`
3. Implement the filter in `ScribanCustomFunctions.cs`
4. Register it in the `Register()` method
5. Rebuild and test

# Liquid to Scriban Migration & Template Builder Implementation Plan
## Unified Dynamic Template System - Blazor WebAssembly

**Last Updated**: 2025-11-26
**Architecture**: Fully dynamic - all templates use JSON Schema + Scriban
**No C# Models Required**: Everything is data-driven

---

## Executive Summary

Building a **no-code template builder** where ALL templates (built-in and user-created) use:
- **JSON Schemas** (define data structure)
- **Scriban Templates** (define HTML output)
- **Dynamic JSON data** (no C# object models)
- **AI-powered data generation** (user text → structured JSON)

### Why Unified Dynamic Approach?

✅ **Single System**: No dual architecture (C# models vs dynamic)
✅ **True No-Code**: Users create templates without touching code
✅ **Immediate Deployment**: New templates work instantly (no recompilation)
✅ **Consistent Workflow**: All templates managed the same way
✅ **AI Integration**: Schema guides AI to generate structured data
✅ **Flexible Storage**: Templates are just data (database, files, etc.)

---

## System Architecture Overview

### Core Data Flow

```
┌─────────────────────────────────────────────────────┐
│ 1. Templates.razor (Template Builder)              │
│    User creates:                                     │
│    • JSON Schema (defines data structure)           │
│    • Scriban Template (defines HTML output)         │
│    → Store in database/localStorage                 │
└─────────────────────────────────────────────────────┘
                    ↓
┌─────────────────────────────────────────────────────┐
│ 2. Consults.razor (Template Consumer)              │
│    User workflow:                                    │
│    a. Select template                               │
│    b. Enter free-form text notes                    │
│    c. System sends: Schema + Text → AI Endpoint     │
│    d. AI returns: Structured JSON                   │
│    e. System validates: JSON against Schema         │
│    f. System renders: Scriban Template + JSON → HTML│
└─────────────────────────────────────────────────────┘
```

### AI Endpoint Integration Detail

```
User Input (Free Text):
┌────────────────────────────────────────┐
│ "52 year old female with stage 2       │
│  breast cancer, ER+, PR+, HER2-.       │
│  Discussing treatment options..."      │
└────────────────────────────────────────┘
         ↓
AI Endpoint Request:
{
  "schema": { /* JSON Schema structure */ },
  "userInput": "52 year old female with..."
}
         ↓
AI Endpoint Response (Structured JSON):
{
  "front_matter": {
    "patient": {
      "age_years": 52,
      "sex": "female",
      "pronoun": { "nom": "she", "gen": "her", ... }
    },
    "staging": { "stage_group": "Stage 2" },
    "receptors": {
      "ER": "positive",
      "PR": "positive",
      "HER2": "negative"
    }
  }
}
         ↓
Template Rendering:
Scriban Template + JSON Data → HTML Output
```

---

## Core Models

### UserTemplate Model (Unified for All Templates)

```csharp
public class UserTemplate
{
    // Identity
    public Guid Id { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    public string Category { get; set; }  // e.g., "Medical", "Legal", "Custom"

    // Template Content (stored as strings)
    public string JsonSchema { get; set; }      // JSON Schema as string
    public string ScribanTemplate { get; set; }  // Scriban template as string

    // Metadata
    public bool IsBuiltIn { get; set; }     // Prevent deletion of built-in templates
    public string CreatedBy { get; set; }   // User ID from Azure AD
    public DateTime CreatedAt { get; set; }
    public DateTime ModifiedAt { get; set; }
    public int Version { get; set; }        // For version control

    // Optional: Template settings
    public Dictionary<string, string>? Settings { get; set; }  // Custom settings
}
```

### Supporting Models

```csharp
public class TemplateRenderResult
{
    public bool Success { get; set; }
    public string? RenderedHtml { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public TimeSpan RenderDuration { get; set; }
}

public class ValidationResult
{
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; } = new();
    public string? SchemaPath { get; set; }
}

public class AIGenerationRequest
{
    public string JsonSchema { get; set; }
    public string UserInput { get; set; }
    public Dictionary<string, object>? AdditionalContext { get; set; }
}

public class AIGenerationResult
{
    public bool Success { get; set; }
    public string? GeneratedJson { get; set; }
    public List<string> Errors { get; set; } = new();
    public TimeSpan GenerationDuration { get; set; }
}
```

---

## Service Architecture

### Service Interfaces

```csharp
// Core template rendering
public interface ITemplateRenderService
{
    Task<TemplateRenderResult> RenderAsync(
        string scribanTemplate,
        string jsonData);
}

// Template storage and management
public interface ITemplateStorageService
{
    Task<IEnumerable<UserTemplate>> GetAllTemplatesAsync();
    Task<IEnumerable<UserTemplate>> GetTemplatesByUserAsync(string userId);
    Task<UserTemplate?> GetTemplateByIdAsync(Guid id);
    Task<UserTemplate> SaveTemplateAsync(UserTemplate template);
    Task<bool> DeleteTemplateAsync(Guid id);
    Task<IEnumerable<UserTemplate>> GetBuiltInTemplatesAsync();
}

// JSON Schema validation
public interface ISchemaValidationService
{
    Task<ValidationResult> ValidateJsonAsync(
        string jsonData,
        string jsonSchema);
}

// AI endpoint integration
public interface IAIEndpointService
{
    Task<AIGenerationResult> GenerateStructuredDataAsync(
        string jsonSchema,
        string userInput,
        Dictionary<string, object>? additionalContext = null);
}

// Seed built-in templates
public interface ITemplateSeederService
{
    Task SeedBuiltInTemplatesAsync();
    Task<bool> HasBeenSeededAsync();
}
```

---

## Implementation Phases

## PHASE 1: Foundation Services (Week 1)

### 1.1 Setup Dependencies

Add to `BlazorWasm.csproj`:
```xml
<PackageReference Include="Scriban" Version="6.5.2" />
<PackageReference Include="Json.Schema.Net" Version="7.2.3" />
```

### 1.2 Create Custom Scriban Functions

**File**: `Extensions/Scriban/ScribanCustomFunctions.cs`

```csharp
using Scriban;
using Scriban.Runtime;

namespace ConsultologistBlazor.Extensions.Scriban;

public static class ScribanCustomFunctions
{
    /// <summary>
    /// Converts newlines to HTML br tags
    /// </summary>
    public static string NewlineToBr(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        return text
            .Replace("\r\n", "<br>\n")
            .Replace("\n", "<br>\n")
            .Replace("\r", "<br>\n");
    }

    /// <summary>
    /// Register all custom functions with Scriban
    /// </summary>
    public static void Register(ScriptObject scriptObject)
    {
        scriptObject.Import("newline_to_br", new Func<string, string>(NewlineToBr));

        // Add more custom functions here as needed
        // scriptObject.Import("custom_function", new Func<...>(...));
    }
}
```

### 1.3 Implement Template Render Service

**File**: `Services/Templates/ScribanTemplateRenderService.cs`

```csharp
using Scriban;
using Scriban.Runtime;
using System.Text.Json;
using ConsultologistBlazor.Extensions.Scriban;

namespace ConsultologistBlazor.Services.Templates;

public class ScribanTemplateRenderService : ITemplateRenderService
{
    private readonly Dictionary<string, Template> _compiledCache = new();
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    public async Task<TemplateRenderResult> RenderAsync(
        string scribanTemplate,
        string jsonData)
    {
        var result = new TemplateRenderResult();
        var startTime = DateTime.UtcNow;

        try
        {
            // 1. Parse JSON data into dictionary for Scriban
            var jsonDoc = JsonDocument.Parse(jsonData);
            var dataDict = JsonElementToDictionary(jsonDoc.RootElement);

            // 2. Get or compile template (with caching)
            var template = await GetOrCompileTemplateAsync(scribanTemplate);

            if (template.HasErrors)
            {
                result.Errors.AddRange(
                    template.Messages.Select(m => m.ToString()));
                return result;
            }

            // 3. Setup Scriban context with custom functions
            var scriptObject = new ScriptObject();
            ScribanCustomFunctions.Register(scriptObject);
            scriptObject.Import(dataDict);

            var templateContext = new TemplateContext();
            templateContext.PushGlobal(scriptObject);

            // 4. Render template
            result.RenderedHtml = await template.RenderAsync(templateContext);
            result.Success = true;
        }
        catch (JsonException ex)
        {
            result.Errors.Add($"Invalid JSON data: {ex.Message}");
        }
        catch (Exception ex)
        {
            result.Errors.Add($"Rendering failed: {ex.Message}");
        }
        finally
        {
            result.RenderDuration = DateTime.UtcNow - startTime;
        }

        return result;
    }

    private async Task<Template> GetOrCompileTemplateAsync(string scribanTemplate)
    {
        var templateHash = GetTemplateHash(scribanTemplate);

        if (_compiledCache.TryGetValue(templateHash, out var cached))
            return cached;

        await _cacheLock.WaitAsync();
        try
        {
            // Double-check after acquiring lock
            if (_compiledCache.TryGetValue(templateHash, out cached))
                return cached;

            // Use Liquid compatibility mode for easier migration
            var template = Template.ParseLiquid(scribanTemplate);
            _compiledCache[templateHash] = template;

            return template;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private string GetTemplateHash(string template)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(template));
        return Convert.ToBase64String(hash);
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
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.ToString()
        };
    }
}
```

### 1.4 Implement Template Storage Service (localStorage)

**File**: `Services/Templates/TemplateStorageService.cs`

```csharp
using Microsoft.JSInterop;
using System.Text.Json;

namespace ConsultologistBlazor.Services.Templates;

public class TemplateStorageService : ITemplateStorageService
{
    private readonly IJSRuntime _jsRuntime;
    private const string StorageKey = "consultologist_templates";

    public TemplateStorageService(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
    }

    public async Task<IEnumerable<UserTemplate>> GetAllTemplatesAsync()
    {
        try
        {
            var json = await _jsRuntime.InvokeAsync<string>(
                "localStorage.getItem", StorageKey);

            if (string.IsNullOrEmpty(json))
                return new List<UserTemplate>();

            return JsonSerializer.Deserialize<List<UserTemplate>>(json)
                ?? new List<UserTemplate>();
        }
        catch (JSException)
        {
            // localStorage not available or error
            return new List<UserTemplate>();
        }
    }

    public async Task<IEnumerable<UserTemplate>> GetTemplatesByUserAsync(string userId)
    {
        var all = await GetAllTemplatesAsync();
        return all.Where(t => t.CreatedBy == userId || t.IsBuiltIn);
    }

    public async Task<UserTemplate?> GetTemplateByIdAsync(Guid id)
    {
        var all = await GetAllTemplatesAsync();
        return all.FirstOrDefault(t => t.Id == id);
    }

    public async Task<UserTemplate> SaveTemplateAsync(UserTemplate template)
    {
        var templates = (await GetAllTemplatesAsync()).ToList();

        var existing = templates.FirstOrDefault(t => t.Id == template.Id);
        if (existing != null)
        {
            // Update existing
            templates.Remove(existing);
            template.ModifiedAt = DateTime.UtcNow;
            template.Version++;
        }
        else
        {
            // Create new
            template.Id = Guid.NewGuid();
            template.CreatedAt = DateTime.UtcNow;
            template.ModifiedAt = DateTime.UtcNow;
            template.Version = 1;
        }

        templates.Add(template);

        var json = JsonSerializer.Serialize(templates, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        await _jsRuntime.InvokeVoidAsync(
            "localStorage.setItem", StorageKey, json);

        return template;
    }

    public async Task<bool> DeleteTemplateAsync(Guid id)
    {
        var templates = (await GetAllTemplatesAsync()).ToList();
        var template = templates.FirstOrDefault(t => t.Id == id);

        if (template == null)
            return false;

        if (template.IsBuiltIn)
            throw new InvalidOperationException("Cannot delete built-in templates");

        templates.Remove(template);

        var json = JsonSerializer.Serialize(templates, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        await _jsRuntime.InvokeVoidAsync(
            "localStorage.setItem", StorageKey, json);

        return true;
    }

    public async Task<IEnumerable<UserTemplate>> GetBuiltInTemplatesAsync()
    {
        var all = await GetAllTemplatesAsync();
        return all.Where(t => t.IsBuiltIn);
    }
}
```

**Note**: localStorage is for MVP. For production, migrate to:
- Azure Cosmos DB
- Azure SQL Database
- Azure Table Storage

### 1.5 Implement Schema Validation Service

**File**: `Services/Validation/SchemaValidationService.cs`

```csharp
using Json.Schema;
using System.Text.Json.Nodes;

namespace ConsultologistBlazor.Services.Validation;

public class SchemaValidationService : ISchemaValidationService
{
    public async Task<ValidationResult> ValidateJsonAsync(
        string jsonData,
        string jsonSchema)
    {
        var result = new ValidationResult { IsValid = true };

        try
        {
            // Parse schema
            var schema = JsonSchema.FromText(jsonSchema);

            // Parse data
            var instance = JsonNode.Parse(jsonData);

            // Validate
            var evaluationResults = schema.Evaluate(instance);

            if (!evaluationResults.IsValid)
            {
                result.IsValid = false;
                result.Errors = ExtractErrors(evaluationResults);
                result.SchemaPath = evaluationResults.InstanceLocation?.ToString();
            }
        }
        catch (Exception ex)
        {
            result.IsValid = false;
            result.Errors.Add($"Validation error: {ex.Message}");
        }

        return await Task.FromResult(result);
    }

    private List<string> ExtractErrors(EvaluationResults results)
    {
        var errors = new List<string>();

        if (results.HasErrors)
        {
            foreach (var error in results.Errors ?? Enumerable.Empty<ErrorMessage>())
            {
                errors.Add($"{error.Key}: {error.Value}");
            }
        }

        return errors.Any() ? errors : new List<string> { "Validation failed" };
    }
}
```

### 1.6 Implement AI Endpoint Service

**File**: `Services/AI/AIEndpointService.cs`

```csharp
namespace ConsultologistBlazor.Services.AI;

public class AIEndpointService : IAIEndpointService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AIEndpointService> _logger;

    public AIEndpointService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<AIEndpointService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<AIGenerationResult> GenerateStructuredDataAsync(
        string jsonSchema,
        string userInput,
        Dictionary<string, object>? additionalContext = null)
    {
        var result = new AIGenerationResult();
        var startTime = DateTime.UtcNow;

        try
        {
            var endpointUrl = _configuration["AIEndpoint:Url"]
                ?? throw new InvalidOperationException("AI endpoint URL not configured");

            var apiKey = _configuration["AIEndpoint:ApiKey"];

            // Prepare request
            var request = new AIGenerationRequest
            {
                JsonSchema = jsonSchema,
                UserInput = userInput,
                AdditionalContext = additionalContext
            };

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpointUrl)
            {
                Content = JsonContent.Create(request)
            };

            // Add API key if configured
            if (!string.IsNullOrEmpty(apiKey))
            {
                httpRequest.Headers.Add("Authorization", $"Bearer {apiKey}");
            }

            // Send request
            _logger.LogInformation("Sending AI generation request");
            var response = await _httpClient.SendAsync(httpRequest);
            response.EnsureSuccessStatusCode();

            // Parse response
            result.GeneratedJson = await response.Content.ReadAsStringAsync();
            result.Success = true;

            _logger.LogInformation("AI generation successful");
        }
        catch (HttpRequestException ex)
        {
            result.Errors.Add($"AI endpoint error: {ex.Message}");
            _logger.LogError(ex, "AI endpoint request failed");
        }
        catch (Exception ex)
        {
            result.Errors.Add($"Unexpected error: {ex.Message}");
            _logger.LogError(ex, "AI generation failed");
        }
        finally
        {
            result.GenerationDuration = DateTime.UtcNow - startTime;
        }

        return result;
    }
}
```

### 1.7 Register Services in Program.cs

**File**: `Program.cs` (add to existing file)

```csharp
// Template services
builder.Services.AddScoped<ITemplateRenderService, ScribanTemplateRenderService>();
builder.Services.AddScoped<ITemplateStorageService, TemplateStorageService>();
builder.Services.AddScoped<ISchemaValidationService, SchemaValidationService>();
builder.Services.AddScoped<IAIEndpointService, AIEndpointService>();
builder.Services.AddScoped<ITemplateSeederService, TemplateSeederService>();

// Initialize templates on startup
var host = builder.Build();
var seeder = host.Services.GetRequiredService<ITemplateSeederService>();
await seeder.SeedBuiltInTemplatesAsync();
await host.RunAsync();
```

**Configuration**: Add to `wwwroot/appsettings.json`:

```json
{
  "AIEndpoint": {
    "Url": "https://your-ai-endpoint.azure.com/api/generate",
    "ApiKey": "your-api-key-here",
    "Timeout": 30
  }
}
```

---

## PHASE 2: Migrate Built-in Oncology Template (Week 1)

### 2.1 Convert Liquid to Scriban

The existing template at `wwwroot/example-template/templates/consult_template.liquid` needs minimal changes because Scriban supports Liquid syntax via `Template.ParseLiquid()`.

**Key Changes Needed**:
1. `{%- elsif -%}` → `{{ else if }}`
2. Filters work as-is in Liquid mode
3. Custom `newline_to_br` filter already implemented

**File**: `wwwroot/templates/consult_template.scriban`

Copy the existing Liquid template and make syntax adjustments. Most of the template can remain unchanged when using `Template.ParseLiquid()`.

### 2.2 Create Template Seeder Service

**File**: `Services/Templates/TemplateSeederService.cs`

```csharp
namespace ConsultologistBlazor.Services.Templates;

public class TemplateSeederService : ITemplateSeederService
{
    private readonly ITemplateStorageService _storage;
    private readonly HttpClient _httpClient;
    private readonly ILogger<TemplateSeederService> _logger;

    public TemplateSeederService(
        ITemplateStorageService storage,
        HttpClient httpClient,
        ILogger<TemplateSeederService> logger)
    {
        _storage = storage;
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<bool> HasBeenSeededAsync()
    {
        var builtIn = await _storage.GetBuiltInTemplatesAsync();
        return builtIn.Any();
    }

    public async Task SeedBuiltInTemplatesAsync()
    {
        if (await HasBeenSeededAsync())
        {
            _logger.LogInformation("Built-in templates already seeded");
            return;
        }

        _logger.LogInformation("Seeding built-in templates");

        await SeedOncologyConsultTemplateAsync();

        // Add more built-in templates here

        _logger.LogInformation("Built-in templates seeded successfully");
    }

    private async Task SeedOncologyConsultTemplateAsync()
    {
        try
        {
            // Load schema
            var schema = await _httpClient.GetStringAsync("/schemas/mortigen_render_context.schema.json");

            // Load template
            var template = await _httpClient.GetStringAsync("/templates/consult_template.scriban");

            var builtInTemplate = new UserTemplate
            {
                Name = "Oncology Consultation Note",
                Description = "Medical oncology consultation note template for breast cancer patients",
                Category = "Medical - Oncology",
                IsBuiltIn = true,
                JsonSchema = schema,
                ScribanTemplate = template,
                CreatedBy = "System",
                CreatedAt = DateTime.UtcNow,
                ModifiedAt = DateTime.UtcNow,
                Version = 1
            };

            await _storage.SaveTemplateAsync(builtInTemplate);

            _logger.LogInformation("Oncology consultation template seeded");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to seed oncology consultation template");
        }
    }
}
```

---

## PHASE 3: Consults.razor - Template Consumer UI (Week 2)

### Complete Implementation

**File**: `Pages/Consults.razor`

```razor
@page "/consults"
@using System.Text.Json
@inject ITemplateStorageService TemplateStorage
@inject ITemplateRenderService TemplateRenderer
@inject ISchemaValidationService SchemaValidator
@inject IAIEndpointService AIEndpoint
@attribute [Authorize]

<PageTitle>Create Consultation Note</PageTitle>

<FluentStack Orientation="Orientation.Vertical" VerticalGap="24">
    <FluentLabel Typo="Typography.H3">Create Consultation Note</FluentLabel>

    <!-- Template Selection -->
    <FluentSelect @bind-Value="selectedTemplateId"
                  Label="Select Template"
                  Style="width: 100%;">
        <FluentOption Value="">-- Select a template --</FluentOption>
        @foreach (var template in templates)
        {
            <FluentOption Value="@template.Id.ToString()">
                @template.Name
                @if (template.IsBuiltIn)
                {
                    <FluentBadge Appearance="Appearance.Accent">Built-in</FluentBadge>
                }
            </FluentOption>
        }
    </FluentSelect>

    @if (!string.IsNullOrEmpty(selectedTemplateId))
    {
        <!-- Data Input Section -->
        <FluentCard>
            <FluentLabel Typo="Typography.H5">Input Data</FluentLabel>

            <FluentTabs>
                <FluentTab Label="Enter Text (AI Generation)">
                    <FluentStack Orientation="Orientation.Vertical" VerticalGap="12">
                        <FluentLabel>
                            Enter your consultation notes below. The AI will structure the data according to the template schema.
                        </FluentLabel>

                        <FluentTextArea @bind-Value="userTextInput"
                                       Placeholder="Enter your consultation notes here..."
                                       Style="width: 100%; height: 300px;"
                                       Label="Consultation Notes"/>

                        <FluentButton Appearance="Appearance.Accent"
                                     OnClick="GenerateFromAI"
                                     Disabled="@(string.IsNullOrWhiteSpace(userTextInput) || isGenerating)">
                            @if (isGenerating)
                            {
                                <FluentProgressRing Style="width: 16px; height: 16px;"/>
                                <span>Generating...</span>
                            }
                            else
                            {
                                <span>Generate Structured Data with AI</span>
                            }
                        </FluentButton>
                    </FluentStack>
                </FluentTab>

                <FluentTab Label="Paste JSON">
                    <FluentStack Orientation="Orientation.Vertical" VerticalGap="12">
                        <FluentLabel>
                            Paste pre-structured JSON data that matches the template schema.
                        </FluentLabel>

                        <FluentTextArea @bind-Value="jsonInput"
                                       Placeholder='{"patient": {"name": "..."}, ...}'
                                       Style="width: 100%; height: 300px; font-family: monospace;"
                                       Label="JSON Data"/>
                    </FluentStack>
                </FluentTab>
            </FluentTabs>

            <!-- Action Buttons -->
            <FluentStack Orientation="Orientation.Horizontal" HorizontalGap="12">
                <FluentButton Appearance="Appearance.Neutral"
                             OnClick="ValidateData"
                             Disabled="@(string.IsNullOrWhiteSpace(jsonInput))">
                    Validate JSON
                </FluentButton>

                <FluentButton Appearance="Appearance.Accent"
                             OnClick="RenderTemplate"
                             Disabled="@(string.IsNullOrWhiteSpace(jsonInput))">
                    Render Preview
                </FluentButton>

                <FluentButton Appearance="Appearance.Neutral"
                             OnClick="ClearAll">
                    Clear All
                </FluentButton>
            </FluentStack>
        </FluentCard>

        <!-- Validation Errors -->
        @if (validationResult != null && !validationResult.IsValid)
        {
            <FluentMessageBar Intent="MessageIntent.Error" Title="Validation Errors">
                <ul style="margin: 0; padding-left: 20px;">
                    @foreach (var error in validationResult.Errors)
                    {
                        <li>@error</li>
                    }
                </ul>
            </FluentMessageBar>
        }

        <!-- AI Generation Errors -->
        @if (aiGenerationResult != null && !aiGenerationResult.Success)
        {
            <FluentMessageBar Intent="MessageIntent.Error" Title="AI Generation Errors">
                <ul style="margin: 0; padding-left: 20px;">
                    @foreach (var error in aiGenerationResult.Errors)
                    {
                        <li>@error</li>
                    }
                </ul>
            </FluentMessageBar>
        }

        <!-- Render Success -->
        @if (renderResult?.Success == true)
        {
            <FluentCard>
                <FluentStack Orientation="Orientation.Horizontal">
                    <FluentLabel Typo="Typography.H5" Style="flex: 1;">HTML Preview</FluentLabel>
                    <FluentBadge Appearance="Appearance.Success">
                        Rendered in @renderResult.RenderDuration.TotalMilliseconds ms
                    </FluentBadge>
                </FluentStack>

                <div style="border: 1px solid #ccc; padding: 24px; background: white; margin-top: 16px; max-height: 600px; overflow-y: auto;">
                    @((MarkupString)renderResult.RenderedHtml!)
                </div>

                <FluentStack Orientation="Orientation.Horizontal" HorizontalGap="12" Style="margin-top: 16px;">
                    <FluentButton Appearance="Appearance.Neutral" OnClick="ExportHtml">
                        <FluentIcon Icon="Icons.Regular.Size16.ArrowDownload"/> Export HTML
                    </FluentButton>
                    <FluentButton Appearance="Appearance.Neutral" OnClick="PrintPreview">
                        <FluentIcon Icon="Icons.Regular.Size16.Print"/> Print
                    </FluentButton>
                    <FluentButton Appearance="Appearance.Neutral" OnClick="CopyToClipboard">
                        <FluentIcon Icon="Icons.Regular.Size16.Copy"/> Copy HTML
                    </FluentButton>
                </FluentStack>
            </FluentCard>
        }

        <!-- Render Errors -->
        @if (renderResult != null && !renderResult.Success)
        {
            <FluentMessageBar Intent="MessageIntent.Error" Title="Rendering Errors">
                <ul style="margin: 0; padding-left: 20px;">
                    @foreach (var error in renderResult.Errors)
                    {
                        <li>@error</li>
                    }
                </ul>
            </FluentMessageBar>
        }
    }
</FluentStack>

@code {
    private List<UserTemplate> templates = new();
    private string? selectedTemplateId;
    private string? userTextInput;
    private string? jsonInput;
    private bool isGenerating;

    private ValidationResult? validationResult;
    private AIGenerationResult? aiGenerationResult;
    private TemplateRenderResult? renderResult;

    protected override async Task OnInitializedAsync()
    {
        templates = (await TemplateStorage.GetAllTemplatesAsync()).ToList();
    }

    private async Task GenerateFromAI()
    {
        if (string.IsNullOrWhiteSpace(userTextInput) || string.IsNullOrEmpty(selectedTemplateId))
            return;

        var template = templates.FirstOrDefault(t => t.Id.ToString() == selectedTemplateId);
        if (template == null)
            return;

        isGenerating = true;
        aiGenerationResult = null;
        StateHasChanged();

        try
        {
            aiGenerationResult = await AIEndpoint.GenerateStructuredDataAsync(
                template.JsonSchema,
                userTextInput);

            if (aiGenerationResult.Success)
            {
                jsonInput = aiGenerationResult.GeneratedJson;

                // Auto-validate after generation
                await ValidateData();
            }
        }
        finally
        {
            isGenerating = false;
            StateHasChanged();
        }
    }

    private async Task ValidateData()
    {
        if (string.IsNullOrWhiteSpace(jsonInput) || string.IsNullOrEmpty(selectedTemplateId))
            return;

        var template = templates.FirstOrDefault(t => t.Id.ToString() == selectedTemplateId);
        if (template == null)
            return;

        validationResult = await SchemaValidator.ValidateJsonAsync(
            jsonInput,
            template.JsonSchema);
    }

    private async Task RenderTemplate()
    {
        // Validate first
        await ValidateData();

        if (validationResult?.IsValid != true)
            return;

        var template = templates.FirstOrDefault(t => t.Id.ToString() == selectedTemplateId);
        if (template == null)
            return;

        renderResult = await TemplateRenderer.RenderAsync(
            template.ScribanTemplate,
            jsonInput!);
    }

    private void ClearAll()
    {
        userTextInput = null;
        jsonInput = null;
        validationResult = null;
        aiGenerationResult = null;
        renderResult = null;
    }

    private async Task ExportHtml()
    {
        if (renderResult?.RenderedHtml == null)
            return;

        // Create download
        var fileName = $"consultation-{DateTime.Now:yyyyMMdd-HHmmss}.html";
        var bytes = System.Text.Encoding.UTF8.GetBytes(renderResult.RenderedHtml);
        var base64 = Convert.ToBase64String(bytes);

        await JSRuntime.InvokeVoidAsync("downloadFile", fileName, base64);
    }

    private async Task PrintPreview()
    {
        if (renderResult?.RenderedHtml == null)
            return;

        await JSRuntime.InvokeVoidAsync("printHtml", renderResult.RenderedHtml);
    }

    private async Task CopyToClipboard()
    {
        if (renderResult?.RenderedHtml == null)
            return;

        await JSRuntime.InvokeVoidAsync("navigator.clipboard.writeText", renderResult.RenderedHtml);
    }
}
```

**Add JavaScript helpers** in `wwwroot/index.html`:

```html
<script>
    window.downloadFile = function(filename, base64Content) {
        const link = document.createElement('a');
        link.download = filename;
        link.href = 'data:text/html;base64,' + base64Content;
        document.body.appendChild(link);
        link.click();
        document.body.removeChild(link);
    };

    window.printHtml = function(htmlContent) {
        const printWindow = window.open('', '', 'height=600,width=800');
        printWindow.document.write(htmlContent);
        printWindow.document.close();
        printWindow.print();
    };
</script>
```

---

## PHASE 4: Templates.razor - Template Builder UI (Week 2-3)

### Complete Implementation

**File**: `Pages/Templates.razor`

```razor
@page "/templates"
@inject ITemplateStorageService TemplateStorage
@inject ITemplateRenderService TemplateRenderer
@inject ISchemaValidationService SchemaValidator
@attribute [Authorize]

<PageTitle>Template Builder</PageTitle>

<FluentStack Orientation="Orientation.Vertical" VerticalGap="24">
    <FluentStack Orientation="Orientation.Horizontal">
        <FluentLabel Typo="Typography.H3" Style="flex: 1;">Template Builder</FluentLabel>
        <FluentButton Appearance="Appearance.Accent" OnClick="CreateNewTemplate">
            <FluentIcon Icon="Icons.Regular.Size16.Add"/> New Template
        </FluentButton>
        <FluentButton Appearance="Appearance.Neutral" OnClick="ImportTemplate">
            <FluentIcon Icon="Icons.Regular.Size16.ArrowUpload"/> Import
        </FluentButton>
    </FluentStack>

    <!-- Template List -->
    @if (!templates.Any())
    {
        <FluentCard>
            <FluentLabel>No templates found. Create your first template to get started!</FluentLabel>
        </FluentCard>
    }
    else
    {
        @foreach (var template in templates.OrderByDescending(t => t.ModifiedAt))
        {
            <FluentCard>
                <FluentStack Orientation="Orientation.Horizontal">
                    <div style="flex: 1;">
                        <FluentStack Orientation="Orientation.Horizontal" HorizontalGap="8">
                            <FluentLabel Typo="Typography.H5">@template.Name</FluentLabel>
                            @if (template.IsBuiltIn)
                            {
                                <FluentBadge Appearance="Appearance.Accent">Built-in</FluentBadge>
                            }
                            @if (template.Category != null)
                            {
                                <FluentBadge>@template.Category</FluentBadge>
                            }
                        </FluentStack>
                        <p style="color: #666;">@template.Description</p>
                        <small style="color: #999;">
                            Modified: @template.ModifiedAt.ToString("MMM dd, yyyy HH:mm") | Version: @template.Version
                        </small>
                    </div>
                    <FluentStack Orientation="Orientation.Vertical" VerticalGap="8">
                        <FluentButton Appearance="Appearance.Neutral"
                                     OnClick="() => EditTemplate(template)">
                            <FluentIcon Icon="Icons.Regular.Size16.Edit"/> Edit
                        </FluentButton>
                        <FluentButton Appearance="Appearance.Neutral"
                                     OnClick="() => DuplicateTemplate(template)">
                            <FluentIcon Icon="Icons.Regular.Size16.Copy"/> Duplicate
                        </FluentButton>
                        <FluentButton Appearance="Appearance.Neutral"
                                     OnClick="() => ExportTemplate(template)">
                            <FluentIcon Icon="Icons.Regular.Size16.ArrowDownload"/> Export
                        </FluentButton>
                        @if (!template.IsBuiltIn)
                        {
                            <FluentButton Appearance="Appearance.Neutral"
                                         OnClick="() => DeleteTemplate(template)">
                                <FluentIcon Icon="Icons.Regular.Size16.Delete"/> Delete
                            </FluentButton>
                        }
                    </FluentStack>
                </FluentStack>
            </FluentCard>
        }
    }
</FluentStack>

<!-- Template Editor Dialog -->
@if (editingTemplate != null)
{
    <FluentDialog @bind-Open="showEditor"
                  Modal="true"
                  Style="width: 95vw; height: 95vh;">
        <FluentDialogHeader>
            <FluentLabel Typo="Typography.H4">
                @(editingTemplate.Id == Guid.Empty ? "Create Template" : "Edit Template")
            </FluentLabel>
        </FluentDialogHeader>

        <FluentDialogBody Style="height: 100%; overflow: auto;">
            <FluentStack Orientation="Orientation.Vertical" VerticalGap="16">
                <!-- Template Metadata -->
                <FluentTextField @bind-Value="editingTemplate.Name"
                                Label="Template Name"
                                Required
                                Style="width: 100%;"/>

                <FluentTextField @bind-Value="editingTemplate.Description"
                                Label="Description"
                                Style="width: 100%;"/>

                <FluentTextField @bind-Value="editingTemplate.Category"
                                Label="Category"
                                Placeholder="e.g., Medical, Legal, Custom"
                                Style="width: 100%;"/>

                <!-- Schema and Template Editors -->
                <FluentGrid>
                    <FluentGridItem xs="6">
                        <FluentStack Orientation="Orientation.Vertical" VerticalGap="8">
                            <FluentLabel Typo="Typography.H6">JSON Schema</FluentLabel>
                            <FluentTextArea @bind-Value="editingTemplate.JsonSchema"
                                           Placeholder='{"$schema": "...", "type": "object", ...}'
                                           Style="width: 100%; height: 500px; font-family: monospace; font-size: 12px;"/>
                            <FluentButton Appearance="Appearance.Neutral"
                                         OnClick="ValidateSchema"
                                         Style="width: fit-content;">
                                Validate Schema
                            </FluentButton>
                        </FluentStack>
                    </FluentGridItem>

                    <FluentGridItem xs="6">
                        <FluentStack Orientation="Orientation.Vertical" VerticalGap="8">
                            <FluentLabel Typo="Typography.H6">Scriban Template</FluentLabel>
                            <FluentTextArea @bind-Value="editingTemplate.ScribanTemplate"
                                           Placeholder='<h2>{{ title }}</h2><p>{{ content }}</p>'
                                           Style="width: 100%; height: 500px; font-family: monospace; font-size: 12px;"/>
                            <FluentButton Appearance="Appearance.Neutral"
                                         OnClick="ShowScribanHelp"
                                         Style="width: fit-content;">
                                Scriban Syntax Help
                            </FluentButton>
                        </FluentStack>
                    </FluentGridItem>
                </FluentGrid>

                <!-- Test Section -->
                <FluentCard>
                    <FluentLabel Typo="Typography.H6">Test Template</FluentLabel>
                    <FluentTextArea @bind-Value="sampleJson"
                                   Placeholder='{"patient": {"name": "John Doe"}, ...}'
                                   Label="Sample JSON Data"
                                   Style="width: 100%; height: 150px; font-family: monospace;"/>

                    <FluentButton Appearance="Appearance.Accent"
                                 OnClick="TestTemplate"
                                 Style="margin-top: 8px;">
                        Test Render
                    </FluentButton>

                    @if (testResult?.Success == true)
                    {
                        <div style="border: 1px solid #ccc; padding: 16px; background: white; margin-top: 16px; max-height: 300px; overflow-y: auto;">
                            @((MarkupString)testResult.RenderedHtml!)
                        </div>
                    }

                    @if (testResult != null && !testResult.Success)
                    {
                        <FluentMessageBar Intent="MessageIntent.Error" Style="margin-top: 8px;">
                            <ul style="margin: 0; padding-left: 20px;">
                                @foreach (var error in testResult.Errors)
                                {
                                    <li>@error</li>
                                }
                            </ul>
                        </FluentMessageBar>
                    }
                </FluentCard>
            </FluentStack>
        </FluentDialogBody>

        <FluentDialogFooter>
            <FluentButton Appearance="Appearance.Neutral" OnClick="CancelEdit">
                Cancel
            </FluentButton>
            <FluentButton Appearance="Appearance.Accent"
                         OnClick="SaveTemplate"
                         Disabled="@(string.IsNullOrWhiteSpace(editingTemplate.Name))">
                Save Template
            </FluentButton>
        </FluentDialogFooter>
    </FluentDialog>
}

<!-- Scriban Help Dialog -->
@if (showScribanHelpDialog)
{
    <FluentDialog @bind-Open="showScribanHelpDialog"
                  Modal="true"
                  Style="width: 800px;">
        <FluentDialogHeader>
            <FluentLabel Typo="Typography.H4">Scriban Syntax Reference</FluentLabel>
        </FluentDialogHeader>

        <FluentDialogBody>
            <FluentStack Orientation="Orientation.Vertical" VerticalGap="16">
                <div>
                    <strong>Variables:</strong>
                    <pre style="background: #f5f5f5; padding: 8px; overflow-x: auto;">{{ variable_name }}
{{ patient.name }}
{{ patient.age }}</pre>
                </div>

                <div>
                    <strong>Conditionals:</strong>
                    <pre style="background: #f5f5f5; padding: 8px; overflow-x: auto;">{{ if patient.age > 50 }}
    Senior patient
{{ else if patient.age > 18 }}
    Adult patient
{{ else }}
    Minor patient
{{ end }}</pre>
                </div>

                <div>
                    <strong>Loops:</strong>
                    <pre style="background: #f5f5f5; padding: 8px; overflow-x: auto;">{{ for medication in medications }}
    &lt;li&gt;{{ medication.name }} {{ medication.dose }}&lt;/li&gt;
{{ end }}</pre>
                </div>

                <div>
                    <strong>Custom Functions:</strong>
                    <pre style="background: #f5f5f5; padding: 8px; overflow-x: auto;">{{ text | newline_to_br }}</pre>
                </div>

                <div>
                    <strong>Null Coalescing:</strong>
                    <pre style="background: #f5f5f5; padding: 8px; overflow-x: auto;">{{ patient.name ?? "Unknown" }}</pre>
                </div>
            </FluentStack>
        </FluentDialogBody>

        <FluentDialogFooter>
            <FluentButton Appearance="Appearance.Accent" OnClick="() => showScribanHelpDialog = false">
                Close
            </FluentButton>
        </FluentDialogFooter>
    </FluentDialog>
}

@code {
    private List<UserTemplate> templates = new();
    private UserTemplate? editingTemplate;
    private bool showEditor;
    private bool showScribanHelpDialog;
    private string? sampleJson;
    private TemplateRenderResult? testResult;

    [Inject] private IJSRuntime JSRuntime { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        await LoadTemplates();
    }

    private async Task LoadTemplates()
    {
        templates = (await TemplateStorage.GetAllTemplatesAsync()).ToList();
    }

    private void CreateNewTemplate()
    {
        editingTemplate = new UserTemplate
        {
            Id = Guid.Empty,
            Name = "New Template",
            Description = "",
            Category = "",
            JsonSchema = @"{
  ""$schema"": ""http://json-schema.org/draft-07/schema#"",
  ""type"": ""object"",
  ""properties"": {
    ""title"": { ""type"": ""string"" },
    ""content"": { ""type"": ""string"" }
  },
  ""required"": [""title"", ""content""]
}",
            ScribanTemplate = @"<h2>{{ title }}</h2>
<p>{{ content }}</p>",
            IsBuiltIn = false
        };
        sampleJson = @"{
  ""title"": ""Sample Title"",
  ""content"": ""Sample content goes here.""
}";
        showEditor = true;
    }

    private void EditTemplate(UserTemplate template)
    {
        // Clone the template for editing
        editingTemplate = new UserTemplate
        {
            Id = template.Id,
            Name = template.Name,
            Description = template.Description,
            Category = template.Category,
            JsonSchema = template.JsonSchema,
            ScribanTemplate = template.ScribanTemplate,
            IsBuiltIn = template.IsBuiltIn,
            CreatedBy = template.CreatedBy,
            CreatedAt = template.CreatedAt,
            ModifiedAt = template.ModifiedAt,
            Version = template.Version
        };
        sampleJson = null;
        testResult = null;
        showEditor = true;
    }

    private async Task SaveTemplate()
    {
        if (editingTemplate == null || string.IsNullOrWhiteSpace(editingTemplate.Name))
            return;

        await TemplateStorage.SaveTemplateAsync(editingTemplate);
        await LoadTemplates();

        showEditor = false;
        editingTemplate = null;
    }

    private void CancelEdit()
    {
        showEditor = false;
        editingTemplate = null;
        testResult = null;
    }

    private async Task DuplicateTemplate(UserTemplate template)
    {
        var duplicate = new UserTemplate
        {
            Id = Guid.Empty,
            Name = $"{template.Name} (Copy)",
            Description = template.Description,
            Category = template.Category,
            JsonSchema = template.JsonSchema,
            ScribanTemplate = template.ScribanTemplate,
            IsBuiltIn = false
        };

        await TemplateStorage.SaveTemplateAsync(duplicate);
        await LoadTemplates();
    }

    private async Task DeleteTemplate(UserTemplate template)
    {
        if (template.IsBuiltIn)
            return;

        // TODO: Add confirmation dialog
        await TemplateStorage.DeleteTemplateAsync(template.Id);
        await LoadTemplates();
    }

    private async Task ExportTemplate(UserTemplate template)
    {
        var json = JsonSerializer.Serialize(template, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        var fileName = $"{template.Name.Replace(" ", "_")}_template.json";
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        var base64 = Convert.ToBase64String(bytes);

        await JSRuntime.InvokeVoidAsync("downloadFile", fileName, base64);
    }

    private async Task ImportTemplate()
    {
        // TODO: Implement file picker and JSON import
        await Task.CompletedTask;
    }

    private async Task ValidateSchema()
    {
        if (editingTemplate == null)
            return;

        try
        {
            var schema = JsonSchema.FromText(editingTemplate.JsonSchema);
            // Schema is valid if no exception thrown
            await JSRuntime.InvokeVoidAsync("alert", "Schema is valid!");
        }
        catch (Exception ex)
        {
            await JSRuntime.InvokeVoidAsync("alert", $"Invalid schema: {ex.Message}");
        }
    }

    private async Task TestTemplate()
    {
        if (editingTemplate == null || string.IsNullOrWhiteSpace(sampleJson))
            return;

        // Validate first
        var validation = await SchemaValidator.ValidateJsonAsync(
            sampleJson,
            editingTemplate.JsonSchema);

        if (!validation.IsValid)
        {
            testResult = new TemplateRenderResult
            {
                Success = false,
                Errors = validation.Errors
            };
            return;
        }

        // Render
        testResult = await TemplateRenderer.RenderAsync(
            editingTemplate.ScribanTemplate,
            sampleJson);
    }

    private void ShowScribanHelp()
    {
        showScribanHelpDialog = true;
    }
}
```

---

## Complete File Structure

```
Consultologist-Blazor/
├── Services/
│   ├── Templates/
│   │   ├── ITemplateRenderService.cs
│   │   ├── ScribanTemplateRenderService.cs
│   │   ├── ITemplateStorageService.cs
│   │   ├── TemplateStorageService.cs
│   │   ├── ITemplateSeederService.cs
│   │   └── TemplateSeederService.cs
│   ├── Validation/
│   │   ├── ISchemaValidationService.cs
│   │   └── SchemaValidationService.cs
│   └── AI/
│       ├── IAIEndpointService.cs
│       └── AIEndpointService.cs
├── Models/
│   ├── Templates/
│   │   ├── UserTemplate.cs
│   │   ├── TemplateRenderResult.cs
│   │   ├── AIGenerationRequest.cs
│   │   └── AIGenerationResult.cs
│   └── Validation/
│       └── ValidationResult.cs
├── Extensions/
│   └── Scriban/
│       └── ScribanCustomFunctions.cs
├── Pages/
│   ├── Templates.razor (template builder)
│   └── Consults.razor (template consumer)
├── wwwroot/
│   ├── templates/
│   │   └── consult_template.scriban (built-in)
│   ├── schemas/
│   │   └── mortigen_render_context.schema.json (built-in)
│   ├── index.html (add JS helpers)
│   └── appsettings.json (AI endpoint config)
├── BlazorWasm.csproj (Scriban + Json.Schema.Net)
└── Program.cs (register services)
```

---

## Implementation Timeline

### Week 1: Foundation
- ✅ Add Scriban and Json.Schema.Net packages
- ✅ Implement all core services
- ✅ Convert oncology template to Scriban
- ✅ Seed built-in templates
- ✅ Test basic rendering

### Week 2: Core UI
- ✅ Implement Consults.razor (consumer)
- ✅ Implement Templates.razor (builder - basic)
- ✅ Test end-to-end workflow
- ✅ AI endpoint integration

### Week 3: Enhanced UI
- ✅ Advanced template editor features
- ✅ Syntax highlighting (optional)
- ✅ Better validation feedback
- ✅ Export/import templates

### Week 4: Polish & Features
- ✅ Form builder from schema (optional)
- ✅ Template versioning
- ✅ Performance optimization
- ✅ Comprehensive error handling

### Week 5: Production
- ✅ Database migration (localStorage → Azure)
- ✅ Multi-user support
- ✅ Testing
- ✅ Documentation

---

## Success Criteria

✅ **Unified System**: All templates use JSON Schema + Scriban (no C# models)
✅ **Built-in Template**: Oncology template migrated and functional
✅ **Template Builder**: Users can create/edit templates in Templates.razor
✅ **Template Consumer**: Users can generate notes in Consults.razor
✅ **AI Integration**: User text → AI → structured JSON workflow works
✅ **Validation**: JSON validation against schemas functional
✅ **Rendering**: Templates render correctly to HTML
✅ **Storage**: Templates persist (localStorage MVP, database production)
✅ **Performance**: Rendering < 500ms for typical templates
✅ **Error Handling**: User-friendly error messages

---

## Next Steps

1. **Start Phase 1**: Add packages, create service interfaces
2. **Implement Core Services**: Rendering, storage, validation
3. **Convert Oncology Template**: Liquid → Scriban
4. **Build Consults.razor**: Basic consumer UI
5. **Build Templates.razor**: Basic builder UI
6. **Test Workflow**: Create template → use in consult
7. **Integrate AI Endpoint**: User text → structured JSON
8. **Iterate & Enhance**: Based on feedback

---

**Architecture**: Fully dynamic template system
**Storage**: localStorage (MVP) → Database (production)
**AI Workflow**: User text + Schema → AI → JSON → Template → HTML
**No C# Models**: Everything is data-driven& Template Builder Unified Dynamic Template System - Last Updated202511-26  
ArchitectureFullydynamicalltemplatesuseSchema+ Scriban  
**No C# Models Required**: Everything is data-drivenExecutiveSummaryBuildinga**no-codetemplatebuilder** where ALL templates (built-in and user-created) use:
- **JSON Schemas** (define data structure)
- **Templates**(define HTML output)
- **Dynamic JSON data** (no C# object models)
- **AI-powered data generation** (user text → structured JSON)###WhyUnifiedDynamicApproach?✅ **Single System**: No dual architecture (C# models vs dynamic)  
✅ **True No-Code**: Users create templates without touching code  
✅ **Immediate Deployment**: New templates work instantly (no recompilation)  
✅ **Consistent Workflow**: All templates managed the same way  
✅ **AI Integration**: Schema guides AI to generate structured data  
✅ **Flexible Storage**: Templates are just data (database, files, etc.)  
SystemArchitectureOverview### Core Data Flow
┌─────────────────────────────────────────────────────┐
│ 1. Templates.razor (Template Builder)              │
│    User creates:                                     │
│    • JSON Schema (defines data structure)           │
│    • Scriban Template (defines HTML output)         │
│    → Store in database/localStorage                 │
└─────────────────────────────────────────────────────┘
                    ↓
┌─────────────────────────────────────────────────────┐
│ 2. Consults.razor (Template Consumer)              │
│    User workflow:                                    │
│    a. Select template                               │
│    b. Enter free-form text notes                    │
│    c. System sends: Schema + Text → AI Endpoint     │
│    d. AI returns: Structured JSON                   │
│    e. System validates: JSON against Schema         │
│    f. System renders: Scriban Template + JSON → HTML│
└─────────────────────────────────────────────────────┘
AIEndpointIntegration Detail```
User Input (Free Text):
┌────────────────────────────────────────┐
│ "52 year old female with stage 2       │
│  breast cancer, ER+, PR+, HER2-.       │
│  Discussing treatment options..."      │
└────────────────────────────────────────┘
         ↓
AI Endpoint Request:
  "schema":{/* JSON Schema structure */ }
  "userInput":"52yearoldfemalewith..."         ↓AIEndpointResponse (Structured JSON):  "front_matter": {
    "patient": {
      "age_years": 52,
      "sex": "female",
      "pronoun": { "nom": "she", "gen": "her", ... }
    },
    "staging": { "stage_group": "Stage 2" },
    "receptors": {
      "ER": "positive",
      "PR": "positive",
      "HER2": "negative"
  }
         ↓
Template Rendering:
Scriban Template + JSON Data → HTML Output
CoreModelsUserTemplateModel(UnifiedforAllTemplates)UserTemplate    // Identity
    public Guid Id { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    public string Category { get; set; }  // e.g., "Medical", "Legal", "Custom"
    
    // Template Content (stored as strings)
    public string JsonSchema { get; set; }      // JSON Schema as string
    public string ScribanTemplate { get; set; }  // Scriban template as string
    
    // Metadata
    public bool IsBuiltIn { get; set; }     // Prevent deletion of built-in templates
    public string CreatedBy { get; set; }   // User ID from Azure AD
    public DateTime CreatedAt { get; set; }
    public DateTime ModifiedAt { get; set; }
    public int Version { get; set; }        // For version control
    
    // Optional: Template settings
    public Dictionary<string, string>? Settings { get; set; }  // Custom settings
### Supporting Models
    public TimeSpan RenderDuration { get; set; }
classValidationResult    public bool IsValid { get; set; }
    public List<string> Errors { get; set; } = new();
    public string? SchemaPath { get; set; }
publicclassAIGenerationRequest    public string JsonSchema { get; set; }
    public string UserInput { get; set; }
    public Dictionary<string, object>? AdditionalContext { get; set; }
}

public class AIGenerationResult
{
    public bool Success { get; set; }
    public string? GeneratedJson { get; set; }
    public List<string> Errors { get; set; } = new();
    public TimeSpan GenerationDuration { get; set; }
ServiceArchitectureServiceInterfaces```csharp
//Corerendering
publicinterfaceITemplateRenderService
{
    Task<TemplateRenderResult>RenderAsync(
        stringscribanTemplate, 
        stringjsonData);
}// Template storage and management
public interface ITemplateStorageService
{
    Task<IEnumerable<UserTemplate>> GetAllTemplatesAsync();
    Task<IEnumerable<UserTemplate>> GetTemplatesByUserAsync(string userId);
    Task<UserTemplate?> GetTemplateByIdAsync(Guid id);
    Task<UserTemplate> SaveTemplateAsync(UserTemplate template);
    Task<bool> DeleteTemplateAsync(Guid id);
    Task<IEnumerable<UserTemplate>> GetBuiltInTemplatesAsync();
}

// JSON Schema validation
public interface ISchemaValidationService
{
    Task<ValidationResult> ValidateJsonAsync(
        string jsonData, 
        string jsonSchema);
}

// AI endpoint integration
public interface IAIEndpointService
{
    Task<AIGenerationResult> GenerateStructuredDataAsync(
        string jsonSchema, 
        string userInput,
        Dictionary<string, object>? additionalContext = null);
}

// Seed built-in templates
public interface ITemplateSeederService
{
    Task SeedBuiltInTemplatesAsync();
    Task<bool> HasBeenSeededAsync();
}
```

---

## Implementation Phases

## PHASE 1: Foundation Services (Week 1)

### 1.1 Setup Dependencies

Add to `BlazorWasm.csproj`:
```xml
<PackageReference Include="Scriban" Version="6.5.2" />
<PackageReference Include="Json.Schema.Net" Version="7.2.3" />
```

### 1.2 Create Custom Scriban Functions

**File**: `Extensions/Scriban/ScribanCustomFunctions.cs`
namespace ConsultologistBlazor.Extensions.Scriban;

    /// <summary>
    /// Converts newlines to HTML br tags
    /// </summary>
            
    
    /// <summary>
    /// Register all custom functions with Scriban
    /// </summary>        
        // Add more custom functions here as needed
        // scriptObject.Import("custom_function", new Func<...>(...));
###1.3 Implement Template Render

File**: `Services/Templates/ScribanTemplateRenderService.cs`using System.Text.Json;
using ConsultologistBlazor.Extensions.Scriban;

namespace ConsultologistBlazor.Services.Templates;
    private readonly Dictionary<string, Template> _compiledCache = new();
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    
    public async Task<TemplateRenderResult> RenderAsync(
        string scribanTemplate, 
        string jsonData)
        var startTime = DateTime.UtcNow;
        
            // 1. Parse JSON data into dictionary for Scriban
            var jsonDoc = JsonDocument.Parse(jsonData);
            var dataDict = JsonElementToDictionary(jsonDoc.RootElement);
            
            // 2. Get or compile template (with caching)
            var template = await GetOrCompileTemplateAsync(scribanTemplate);
            
            
            // 3. Setup Scriban context with custom functionsdataDict
            
            
            // 4. Render template        }
        catch (JsonException ex)
        {
            result.Errors.Add($"Invalid JSON data: {ex.Message}");
        finally
        {
            result.RenderDuration = DateTime.UtcNow - startTime;
        }
        
        return result;
    }
    
    private async Task<Template> GetOrCompileTemplateAsync(string scribanTemplate)
    {
        var templateHash = GetTemplateHash(scribanTemplate);
        
        if (_compiledCache.TryGetValue(templateHash, out var cached))
            return cached;
        
        await _cacheLock.WaitAsync();
        try
        {
            // Double-check after acquiring lock
            if (_compiledCache.TryGetValue(templateHash, out cached))
                return cached;
            
            // Use Liquid compatibility mode for easier migration
            var template = Template.ParseLiquid(scribanTemplate);
            _compiledCache[templateHash] = template;
            
            return template;
        }
        finally
        {
            _cacheLock.Release();
        }
    }
    
    private string GetTemplateHash(string template)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(template));
        return Convert.ToBase64String(hash);
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
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.ToString()
        };
    }
}
```
### 1.4 Implement Template Storage Service (localStorage)

**File**: `Services/Templates/TemplateStorageService.cs`

```csharp
using Microsoft.JSInterop;
using System.Text.Json;

namespace ConsultologistBlazor.Services.Templates;

public class TemplateStorageService : ITemplateStorageService
{
    private readonly IJSRuntime _jsRuntime;
    private const string StorageKey = "consultologist_templates";
    
    public TemplateStorageService(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
    }
    
    public async Task<IEnumerable<UserTemplate>> GetAllTemplatesAsync()
    {
        try
        {
            var json = await _jsRuntime.InvokeAsync<string>(
                "localStorage.getItem", StorageKey);
                
            if (string.IsNullOrEmpty(json))
                return new List<UserTemplate>();
                
            return JsonSerializer.Deserialize<List<UserTemplate>>(json) 
                ?? new List<UserTemplate>();
        }
        catch (JSException)
        {
            // localStorage not available or error
            return new List<UserTemplate>();
        }
    }
    
    public async Task<IEnumerable<UserTemplate>> GetTemplatesByUserAsync(string userId)
    {
        var all = await GetAllTemplatesAsync();
        return all.Where(t => t.CreatedBy == userId || t.IsBuiltIn);
    }
    
    public async Task<UserTemplate?> GetTemplateByIdAsync(Guid id)
    {
        var all = await GetAllTemplatesAsync();
        return all.FirstOrDefault(t => t.Id == id);
    }
    
    public async Task<UserTemplate> SaveTemplateAsync(UserTemplate template)
    {
        var templates = (await GetAllTemplatesAsync()).ToList();
        
        var existing = templates.FirstOrDefault(t => t.Id == template.Id);
        if (existing != null)
        {
            // Update existing
            templates.Remove(existing);
            template.ModifiedAt = DateTime.UtcNow;
            template.Version++;
        }
        else
        {
            // Create new
            template.Id = Guid.NewGuid();
            template.CreatedAt = DateTime.UtcNow;
            template.ModifiedAt = DateTime.UtcNow;
            template.Version = 1;
        }
        
        templates.Add(template);
        
        var json = JsonSerializer.Serialize(templates, new JsonSerializerOptions 
        { 
            WriteIndented = true 
        });
        
        await _jsRuntime.InvokeVoidAsync(
            "localStorage.setItem", StorageKey, json);
            
        return template;
    }
    
    public async Task<bool> DeleteTemplateAsync(Guid id)
    {
        var templates = (await GetAllTemplatesAsync()).ToList();
        var template = templates.FirstOrDefault(t => t.Id == id);
        
        if (template == null)
            return false;
            
        if (template.IsBuiltIn)
            throw new InvalidOperationException("Cannot delete built-in templates");
        
        templates.Remove(template);
        
        var json = JsonSerializer.Serialize(templates, new JsonSerializerOptions 
        { 
            WriteIndented = true 
        });
        
        await _jsRuntime.InvokeVoidAsync(
            "localStorage.setItem", StorageKey, json);
            
        return true;
    }
    
    public async Task<IEnumerable<UserTemplate>> GetBuiltInTemplatesAsync()
    {
        var all = await GetAllTemplatesAsync();
        return all.Where(t => t.IsBuiltIn);
    }
}
```

**Note**: localStorage is for MVP. For production, migrate to:
- Azure Cosmos DB
- Azure SQL Database
- Azure Table Storage

### 1.5 Implement Schema Validation Service

**File**: `Services/Validation/SchemaValidationService.cs`

```csharp
using Json.Schema;
using System.Text.Json.Nodes;

namespace ConsultologistBlazor.Services.Validation;

public class SchemaValidationService : ISchemaValidationService
{
    public async Task<ValidationResult> ValidateJsonAsync(
        string jsonData, 
        string jsonSchema)
    {
        var result = new ValidationResult { IsValid = true };
        
        try
        {
            // Parse schema
            var schema = JsonSchema.FromText(jsonSchema);
            
            // Parse data
            var instance = JsonNode.Parse(jsonData);
            
            // Validate
            var evaluationResults = schema.Evaluate(instance);
            
            if (!evaluationResults.IsValid)
            {
                result.IsValid = false;
                result.Errors = ExtractErrors(evaluationResults);
                result.SchemaPath = evaluationResults.InstanceLocation?.ToString();
            }
        }
        catch (Exception ex)
        {
            result.IsValid = false;
            result.Errors.Add($"Validation error: {ex.Message}");
        }
        
        return await Task.FromResult(result);
    }
    
    private List<string> ExtractErrors(EvaluationResults results)
    {
        var errors = new List<string>();
        
        if (results.HasErrors)
        {
            foreach (var error in results.Errors ?? Enumerable.Empty<ErrorMessage>())
            {
                errors.Add($"{error.Key}: {error.Value}");
            }
        }
        
        return errors.Any() ? errors : new List<string> { "Validation failed" };
    }
}
```

### 1.6 Implement AI Endpoint Service

**File**: `Services/AI/AIEndpointService.cs`

```csharp
namespace ConsultologistBlazor.Services.AI;

public class AIEndpointService : IAIEndpointService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AIEndpointService> _logger;
    
    public AIEndpointService(
        HttpClient httpClient, 
        IConfiguration configuration,
        ILogger<AIEndpointService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }
    
    public async Task<AIGenerationResult> GenerateStructuredDataAsync(
        string jsonSchema, 
        string userInput,
        Dictionary<string, object>? additionalContext = null)
    {
        var result = new AIGenerationResult();
        var startTime = DateTime.UtcNow;
        
        try
        {
            var endpointUrl = _configuration["AIEndpoint:Url"] 
                ?? throw new InvalidOperationException("AI endpoint URL not configured");
            
            var apiKey = _configuration["AIEndpoint:ApiKey"];
            
            // Prepare request
            var request = new AIGenerationRequest
            {
                JsonSchema = jsonSchema,
                UserInput = userInput,
                AdditionalContext = additionalContext
            };
            
            var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpointUrl)
            {
                Content = JsonContent.Create(request)
            };
            
            // Add API key if configured
            if (!string.IsNullOrEmpty(apiKey))
            {
                httpRequest.Headers.Add("Authorization", $"Bearer {apiKey}");
            }
            
            // Send request
            _logger.LogInformation("Sending AI generation request");
            var response = await _httpClient.SendAsync(httpRequest);
            response.EnsureSuccessStatusCode();
            
            // Parse response
            result.GeneratedJson = await response.Content.ReadAsStringAsync();
            result.Success = true;
            
            _logger.LogInformation("AI generation successful");
        }
        catch (HttpRequestException ex)
        {
            result.Errors.Add($"AI endpoint error: {ex.Message}");
            _logger.LogError(ex, "AI endpoint request failed");
        }
        catch (Exception ex)
        {
            result.Errors.Add($"Unexpected error: {ex.Message}");
            _logger.LogError(ex, "AI generation failed");
        }
        finally
        {
            result.GenerationDuration = DateTime.UtcNow - startTime;
        }
        
17Register Services in Programcs**File**:`Programcs`(addtoexistingfile)```csharp
// Template services
builder.Services.AddScopedITemplateRenderService, ScribanTemplateRenderService();
builder.Services.AddScopedITemplateStorageService, TemplateStorageService();
builder.Services.AddScoped<ISchemaValidationService, SchemaValidationService>();
builder.Services.AddScoped<IAIEndpointService, AIEndpointService>();
builder.Services.AddScoped<ITemplateSeederService, TemplateSeederService>();//Initialize templates on startup
var host  builderBuild();
varseeder hostServices.GetRequiredServiceITemplateSeederService();
await seeder.SeedBuiltInTemplatesAsync();
await host.RunAsync();
```**Configuration**Addto`wwwrootappsettingsjson`:```json
{
  "AIEndpoint": {
    "Url": "https://your-ai-endpoint.azure.com/api/generate",
    "ApiKey": "your-api-key-here",
    "Timeout": 30
  }
}
```
---## PHASE 2: Migrate Built-in Oncology Template (Week 1)

### 2.1 Convert Liquid to Scriban

The existing template at `wwwroot/example-template/templates/consult_template.liquid` needs minimal changes because Scriban supports Liquid syntax via `Template.ParseLiquid()`.

**Key Changes Needed**:
1. `{%- elsif -%}` → `{{ else if }}`
2. Filters work as-is in Liquid mode
3. Custom `newline_to_br` filter already implemented

**File**: `wwwroot/templates/consult_template.scriban`

Copy the existing Liquid template and make syntax adjustments. Most of the template can remain unchanged when using `Template.ParseLiquid()`.

### 2.2 Create Template Seeder Service

**File**: `Services/Templates/TemplateSeederService.cs`

```csharp
namespace ConsultologistBlazor.Services.Templates;

public class TemplateSeederService : ITemplateSeederService
{
    private readonly ITemplateStorageService _storage;
    private readonly HttpClient _httpClient;
    private readonly ILogger<TemplateSeederService> _logger;
    
    public TemplateSeederService(
        ITemplateStorageService storage,
        HttpClient httpClient,
        ILogger<TemplateSeederService> logger)
        _storage = storage;
        _httpClient = httpClient;
        _logger = logger;
    
    public async Task<bool> HasBeenSeededAsync()
varbuiltIn  await _storageGetBuiltInTemplatesAsync();return builtIn.Any();    
    public async Task SeedBuiltInTemplatesAsync()
ifHasBeenSeededAsync
        {
            _loggerLogInformation"Built-in templates already seeded"        }
        
        _logger.LogInformation("Seeding built-in templates");
        
        await SeedOncologyConsultTemplateAsync();
        
        // Add more built-in templates here
        
        _logger.LogInformation("Built-in templates seeded successfully");
    }
    
    private async Task SeedOncologyConsultTemplateAsync()
    {
// Load schema
            schema_httpClientGetStringAsync"/schemas/mortigen_render_context.schema.json"
            
            //Loadtemplatevar template = await _httpClient.GetStringAsync("/templates/consult_template.scriban");
            
            var builtInTemplateUserTemplate                Name = "Oncology Consultation Note",
                Description = "Medical oncology consultation note template for breast cancer patients",
                Category = "Medical - Oncology",
                IsBuiltIn = true,
                JsonSchema = schema,
                ScribanTemplate = template,
                CreatedBy = "System",
                CreatedAt = DateTime.UtcNow,
                ModifiedAt = DateTime.UtcNow,
                Version = 1
            
            await _storage.SaveTemplateAsync(builtInTemplate);
            
            _logger.LogInformation("Oncology consultation template seeded");
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to seed oncology consultation template");
        }
PHASE 3: Consultsrazor-Template Consumer UI (Week 2)CompleteImplementation**File**`Pages/Consults.razor````razor@page"consults"@using System.Text.Json
@inject ITemplateStorageService TemplateStorage
@inject ITemplateRenderService TemplateRenderer
@inject ISchemaValidationService SchemaValidator
@inject IAIEndpointService AIEndpoint
@attributeAuthorize<PageTitle>CreateConsultationNote</PageTitle><FluentStack Orientation="Orientation.Vertical" VerticalGap="24">
    <FluentLabel Typo="Typography.H3">Create Consultation Note</FluentLabel>
    
    <!-- Template Selection -->
    <FluentSelect @bind-Value="selectedTemplateId" 
                  Label="Select Template"
                  Style="width: 100%;">
        <FluentOption Value="">-- Select a template --</FluentOption>
        @foreach (var template in templates)
        {
            <FluentOption Value="@template.Id.ToString()">
                @template.Name
                @if (template.IsBuiltIn)
                {
                    <FluentBadge Appearance="Appearance.Accent">Built-in</FluentBadge>
                }
            </FluentOption>
        }
    </FluentSelect>
    
    @if (!string.IsNullOrEmpty(selectedTemplateId))
    {
        <!-- Data Input Section -->
        <FluentCard>
            <FluentLabel Typo="Typography.H5">Input Data</FluentLabel>
            
            <FluentTabs>
                <FluentTab Label="Enter Text (AI Generation)">
                    <FluentStack Orientation="Orientation.Vertical" VerticalGap="12">
                        <FluentLabel>
                            Enter your consultation notes below. The AI will structure the data according to the template schema.
                        </FluentLabel>
                        
                        <FluentTextArea @bind-Value="userTextInput"
                                       Placeholder="Enter your consultation notes here..."
                                       Style="width: 100%; height: 300px;"
                                       Label="Consultation Notes"/>
                        
                        <FluentButton Appearance="Appearance.Accent"
                                     OnClick="GenerateFromAI"
                                     Disabled="@(string.IsNullOrWhiteSpace(userTextInput) || isGenerating)">
                            @if (isGenerating)
                            {
                                <FluentProgressRing Style="width: 16px; height: 16px;"/> 
                                <span>Generating...</span>
                            }
                            else
                            {
                                <span>Generate Structured Data with AI</span>
                            }
                        </FluentButton>
                    </FluentStack>
                </FluentTab>
                
                <FluentTab Label="Paste JSON">
                    <FluentStack Orientation="Orientation.Vertical" VerticalGap="12">
                        <FluentLabel>
                            Paste pre-structured JSON data that matches the template schema.
                        </FluentLabel>
                        
                        <FluentTextArea @bind-Value="jsonInput"
                                       Placeholder='{"patient": {"name": "..."}, ...}'
                                       Style="width: 100%; height: 300px; font-family: monospace;"
                                       Label="JSON Data"/>
                    </FluentStack>
                </FluentTab>
            </FluentTabs>
            
            <!-- Action Buttons -->
            <FluentStack Orientation="Orientation.Horizontal" HorizontalGap="12">
                <FluentButton Appearance="Appearance.Neutral"
                             OnClick="ValidateData"
                             Disabled="@(string.IsNullOrWhiteSpace(jsonInput))">
                    Validate JSON
                </FluentButton>
                
                <FluentButton Appearance="Appearance.Accent"
                             OnClick="RenderTemplate"
                             Disabled="@(string.IsNullOrWhiteSpace(jsonInput))">
                    Render Preview
                </FluentButton>
                
                <FluentButton Appearance="Appearance.Neutral"
                             OnClick="ClearAll">
                    Clear All
                </FluentButton>
            </FluentStack>
        </FluentCard>
        
        <!-- Validation Errors -->
        @if (validationResult != null && !validationResult.IsValid)
        {
            <FluentMessageBar Intent="MessageIntent.Error" Title="Validation Errors">
                <ul style="margin: 0; padding-left: 20px;">
                    @foreach (var error in validationResult.Errors)
                    {
                        <li>@error</li>
                    }
                </ul>
            </FluentMessageBar>
        }
        
        <!-- AI Generation Errors -->
        @if (aiGenerationResult != null && !aiGenerationResult.Success)
        {
            <FluentMessageBar Intent="MessageIntent.Error" Title="AI Generation Errors">
                <ul style="margin: 0; padding-left: 20px;">
                    @foreach (var error in aiGenerationResult.Errors)
                    {
                        <li>@error</li>
                    }
                </ul>
            </FluentMessageBar>
        }
        
        <!-- Render Success -->
        @if (renderResult?.Success == true)
        {
            <FluentCard>
                <FluentStack Orientation="Orientation.Horizontal">
                    <FluentLabel Typo="Typography.H5" Style="flex: 1;">HTML Preview</FluentLabel>
                    <FluentBadge Appearance="Appearance.Success">
                        Rendered in @renderResult.RenderDuration.TotalMilliseconds ms
                    </FluentBadge>
                </FluentStack>
                
                <div style="border: 1px solid #ccc; padding: 24px; background: white; margin-top: 16px; max-height: 600px; overflow-y: auto;">
                    @((MarkupString)renderResult.RenderedHtml!)
                </div>
                
                <FluentStack Orientation="Orientation.Horizontal" HorizontalGap="12" Style="margin-top: 16px;">
                    <FluentButton Appearance="Appearance.Neutral" OnClick="ExportHtml">
                        <FluentIcon Icon="Icons.Regular.Size16.ArrowDownload"/> Export HTML
                    </FluentButton>
                    <FluentButton Appearance="Appearance.Neutral" OnClick="PrintPreview">
                        <FluentIcon Icon="Icons.Regular.Size16.Print"/> Print
                    </FluentButton>
                    <FluentButton Appearance="Appearance.Neutral" OnClick="CopyToClipboard">
                        <FluentIcon Icon="Icons.Regular.Size16.Copy"/> Copy HTML
                    </FluentButton>
                </FluentStack>
            </FluentCard>
        }
        
        <!-- Render Errors -->
        @if (renderResult != null && !renderResult.Success)
        {
            <FluentMessageBar Intent="MessageIntent.Error" Title="Rendering Errors">
                <ul style="margin: 0; padding-left: 20px;">
                    @foreach (var error in renderResult.Errors)
                    {
                        <li>@error</li>
                    }
                </ul>
            </FluentMessageBar>
        }
    }
</FluentStack>
@code {
    private List<UserTemplate> templates = new();
    private string? selectedTemplateId;
    private string? userTextInput;
    private string? jsonInput;
    private bool isGenerating;
    
    private ValidationResult? validationResult;
    private AIGenerationResult? aiGenerationResult;
    private TemplateRenderResult? renderResult;
    
    protected override async Task OnInitializedAsync()
    {
        templates = (await TemplateStorage.GetAllTemplatesAsync()).ToList();
    }
    
    private async Task GenerateFromAI()
    {
        if (string.IsNullOrWhiteSpace(userTextInput) || string.IsNullOrEmpty(selectedTemplateId))
            return;
        
        var template = templates.FirstOrDefault(t => t.Id.ToString() == selectedTemplateId);
        if (template == null)
            return;
        
        isGenerating = true;
        aiGenerationResult = null;
        StateHasChanged();
        
        try
        {
            aiGenerationResult = await AIEndpoint.GenerateStructuredDataAsync(
                template.JsonSchema,
                userTextInput);
            
            if (aiGenerationResult.Success)
            {
                jsonInput = aiGenerationResult.GeneratedJson;
                
                // Auto-validate after generation
                await ValidateData();
            }
        }
        finally
        {
            isGenerating = false;
            StateHasChanged();
        }
    }
    
    private async Task ValidateData()
    {
        if (string.IsNullOrWhiteSpace(jsonInput) || string.IsNullOrEmpty(selectedTemplateId))
            return;
        
        var template = templates.FirstOrDefault(t => t.Id.ToString() == selectedTemplateId);
        if (template == null)
            return;
        
        validationResult = await SchemaValidator.ValidateJsonAsync(
            jsonInput,
            template.JsonSchema);
    }
    
    private async Task RenderTemplate()
    {
        // Validate first
        await ValidateData();
        
        if (validationResult?.IsValid != true)
            return;
        
        var template = templates.FirstOrDefault(t => t.Id.ToString() == selectedTemplateId);
        if (template == null)
            return;
        
        renderResult = await TemplateRenderer.RenderAsync(
            template.ScribanTemplate,
            jsonInput!);
    }
    
    private void ClearAll()
    {
        userTextInput = null;
        jsonInput = null;
        validationResult = null;
        aiGenerationResult = null;
        renderResult = null;
    }
    
    private async Task ExportHtml()
    {
        if (renderResult?.RenderedHtml == null)
            return;
        
        // Create download
        var fileName = $"consultation-{DateTime.Now:yyyyMMdd-HHmmss}.html";
        var bytes = System.Text.Encoding.UTF8.GetBytes(renderResult.RenderedHtml);
        var base64 = Convert.ToBase64String(bytes);
        
        await JSRuntime.InvokeVoidAsync("downloadFile", fileName, base64);
    }
    
    private async Task PrintPreview()
    {
        if (renderResult?.RenderedHtml == null)
            return;
        
        await JSRuntime.InvokeVoidAsync("printHtml", renderResult.RenderedHtml);
    }
    
    private async Task CopyToClipboard()
    {
        if (renderResult?.RenderedHtml == null)
            return;
        
        await JSRuntime.InvokeVoidAsync("navigator.clipboard.writeText", renderResult.RenderedHtml);
    }
}
```
**AddJavaScripthelpers** in `wwwroot/index.html````html
<script>
    window.downloadFile = function(filename, base64Content) {
        const link = document.createElement('a');
        link.download = filename;
        link.href = 'data:text/html;base64,' + base64Content;
        document.body.appendChild(link);
        link.click();
        document.body.removeChild(link);
    };
    
    window.printHtml = function(htmlContent) {
        const printWindow = window.open('', '', 'height=600,width=800');
        printWindow.document.write(htmlContent);
        printWindow.document.close();
        printWindow.print();
    };
</script>
```
PHASE 4: Templatesrazor-TemplateBuilderUI (Week 2-3)CompleteImplementationFile`Pages/Templates.razor````razor@page"/templates"@injectITemplateStorageServiceTemplateStorage
@injectITemplateRenderServiceTemplateRenderer
@injectISchemaValidationServiceSchemaValidator
@attribute[Authorize]<PageTitle>TemplateBuilder</PageTitle><FluentStack Orientation="Orientation.Vertical" VerticalGap="24">
    <FluentStack Orientation="Orientation.Horizontal">
        <FluentLabel Typo="Typography.H3" Style="flex: 1;">Template Builder</FluentLabel>
        <FluentButton Appearance="Appearance.Accent" OnClick="CreateNewTemplate">
            <FluentIcon Icon="Icons.Regular.Size16.Add"/> New Template
        </FluentButton>
        <FluentButton Appearance="Appearance.Neutral" OnClick="ImportTemplate">
            <FluentIcon Icon="Icons.Regular.Size16.ArrowUpload"/> Import
        </FluentButton>
    </FluentStack>
    
    <!-- Template List -->
    @if (!templates.Any())
    {
        <FluentCard>
            <FluentLabel>No templates found. Create your first template to get started!</FluentLabel>
        </FluentCard>
    }
    else
    {
        @foreach (var template in templates.OrderByDescending(t => t.ModifiedAt))
        {
            <FluentCard>
                <FluentStack Orientation="Orientation.Horizontal">
                    <div style="flex: 1;">
                        <FluentStack Orientation="Orientation.Horizontal" HorizontalGap="8">
                            <FluentLabel Typo="Typography.H5">@template.Name</FluentLabel>
                            @if (template.IsBuiltIn)
                            {
                                <FluentBadge Appearance="Appearance.Accent">Built-in</FluentBadge>
                            }
                            @if (template.Category != null)
                            {
                                <FluentBadge>@template.Category</FluentBadge>
                            }
                        </FluentStack>
                        <p style="color: #666;">@template.Description</p>
                        <small style="color: #999;">
                            Modified: @template.ModifiedAt.ToString("MMM dd, yyyy HH:mm") | Version: @template.Version
                        </small>
                    </div>
                    <FluentStack Orientation="Orientation.Vertical" VerticalGap="8">
                        <FluentButton Appearance="Appearance.Neutral" 
                                     OnClick="() => EditTemplate(template)">
                            <FluentIcon Icon="Icons.Regular.Size16.Edit"/> Edit
                        </FluentButton>
                        <FluentButton Appearance="Appearance.Neutral" 
                                     OnClick="() => DuplicateTemplate(template)">
                            <FluentIcon Icon="Icons.Regular.Size16.Copy"/> Duplicate
                        </FluentButton>
                        <FluentButton Appearance="Appearance.Neutral" 
                                     OnClick="() => ExportTemplate(template)">
                            <FluentIcon Icon="Icons.Regular.Size16.ArrowDownload"/> Export
                        </FluentButton>
                        @if (!template.IsBuiltIn)
                        {
                            <FluentButton Appearance="Appearance.Neutral" 
                                         OnClick="() => DeleteTemplate(template)">
                                <FluentIcon Icon="Icons.Regular.Size16.Delete"/> Delete
                            </FluentButton>
                        }
                    </FluentStack>
                </FluentStack>
            </FluentCard>
        }
    }
</FluentStack>

<!-- Template Editor Dialog -->
@if (editingTemplate != null)
{
    <FluentDialog @bind-Open="showEditor" 
                  Modal="true"
                  Style="width: 95vw; height: 95vh;">
        <FluentDialogHeader>
            <FluentLabel Typo="Typography.H4">
                @(editingTemplate.Id == Guid.Empty ? "Create Template" : "Edit Template")
            </FluentLabel>
        </FluentDialogHeader>
        
        <FluentDialogBody Style="height: 100%; overflow: auto;">
            <FluentStack Orientation="Orientation.Vertical" VerticalGap="16">
                <!-- Template Metadata -->
                <FluentTextField @bind-Value="editingTemplate.Name" 
                                Label="Template Name"
                                Required
                                Style="width: 100%;"/>
                
                <FluentTextField @bind-Value="editingTemplate.Description" 
                                Label="Description"
                                Style="width: 100%;"/>
                
                <FluentTextField @bind-Value="editingTemplate.Category" 
                                Label="Category"
                                Placeholder="e.g., Medical, Legal, Custom"
                                Style="width: 100%;"/>
                
                <!-- Schema and Template Editors -->
                <FluentGrid>
                    <FluentGridItem xs="6">
                        <FluentStack Orientation="Orientation.Vertical" VerticalGap="8">
                            <FluentLabel Typo="Typography.H6">JSON Schema</FluentLabel>
                            <FluentTextArea @bind-Value="editingTemplate.JsonSchema"
                                           Placeholder='{"$schema": "...", "type": "object", ...}'
                                           Style="width: 100%; height: 500px; font-family: monospace; font-size: 12px;"/>
                            <FluentButton Appearance="Appearance.Neutral" 
                                         OnClick="ValidateSchema"
                                         Style="width: fit-content;">
                                Validate Schema
                            </FluentButton>
                        </FluentStack>
                    </FluentGridItem>
                    
                    <FluentGridItem xs="6">
                        <FluentStack Orientation="Orientation.Vertical" VerticalGap="8">
                            <FluentLabel Typo="Typography.H6">Scriban Template</FluentLabel>
                            <FluentTextArea @bind-Value="editingTemplate.ScribanTemplate"
                                           Placeholder='<h2>{{ title }}</h2><p>{{ content }}</p>'
                                           Style="width: 100%; height: 500px; font-family: monospace; font-size: 12px;"/>
                            <FluentButton Appearance="Appearance.Neutral" 
                                         OnClick="ShowScribanHelp"
                                         Style="width: fit-content;">
                                Scriban Syntax Help
                            </FluentButton>
                        </FluentStack>
                    </FluentGridItem>
                </FluentGrid>
                
                <!-- Test Section -->
                <FluentCard>
                    <FluentLabel Typo="Typography.H6">Test Template</FluentLabel>
                    <FluentTextArea @bind-Value="sampleJson"
                                   Placeholder='{"patient": {"name": "John Doe"}, ...}'
                                   Label="Sample JSON Data"
                                   Style="width: 100%; height: 150px; font-family: monospace;"/>
                    
                    <FluentButton Appearance="Appearance.Accent" 
                                 OnClick="TestTemplate"
                                 Style="margin-top: 8px;">
                        Test Render
                    </FluentButton>
                    
                    @if (testResult?.Success == true)
                    {
                        <div style="border: 1px solid #ccc; padding: 16px; background: white; margin-top: 16px; max-height: 300px; overflow-y: auto;">
                            @((MarkupString)testResult.RenderedHtml!)
                        </div>
                    }
                    
                    @if (testResult != null && !testResult.Success)
                    {
                        <FluentMessageBar Intent="MessageIntent.Error" Style="margin-top: 8px;">
                            <ul style="margin: 0; padding-left: 20px;">
                                @foreach (var error in testResult.Errors)
                                {
                                    <li>@error</li>
                                }
                            </ul>
                        </FluentMessageBar>
                    }
                </FluentCard>
            </FluentStack>
        </FluentDialogBody>
        
        <FluentDialogFooter>
            <FluentButton Appearance="Appearance.Neutral" OnClick="CancelEdit">
                Cancel
            </FluentButton>
            <FluentButton Appearance="Appearance.Accent" 
                         OnClick="SaveTemplate"
                         Disabled="@(string.IsNullOrWhiteSpace(editingTemplate.Name))">
                Save Template
            </FluentButton>
        </FluentDialogFooter>
    </FluentDialog>
}

<!-- Scriban Help Dialog -->
@if (showScribanHelpDialog)
{
    <FluentDialog @bind-Open="showScribanHelpDialog" 
                  Modal="true"
                  Style="width: 800px;">
        <FluentDialogHeader>
            <FluentLabel Typo="Typography.H4">Scriban Syntax Reference</FluentLabel>
        </FluentDialogHeader>
        
        <FluentDialogBody>
            <FluentStack Orientation="Orientation.Vertical" VerticalGap="16">
                <div>
                    <strong>Variables:</strong>
                    <pre style="background: #f5f5f5; padding: 8px; overflow-x: auto;">{{ variable_name }}
{{ patient.name }}
{{ patient.age }}</pre>
                </div>
                
                <div>
                    <strong>Conditionals:</strong>
                    <pre style="background: #f5f5f5; padding: 8px; overflow-x: auto;">{{ if patient.age > 50 }}
    Senior patient
{{ else if patient.age > 18 }}
    Adult patient
{{ else }}
    Minor patient
{{ end }}</pre>
                </div>
                
                <div>
                    <strong>Loops:</strong>
                    <pre style="background: #f5f5f5; padding: 8px; overflow-x: auto;">{{ for medication in medications }}
    &lt;li&gt;{{ medication.name }} {{ medication.dose }}&lt;/li&gt;
{{ end }}</pre>
                </div>
                
                <div>
                    <strong>Custom Functions:</strong>
                    <pre style="background: #f5f5f5; padding: 8px; overflow-x: auto;">{{ text | newline_to_br }}</pre>
                </div>
                
                <div>
                    <strong>Null Coalescing:</strong>
                    <pre style="background: #f5f5f5; padding: 8px; overflow-x: auto;">{{ patient.name ?? "Unknown" }}</pre>
                </div>
            </FluentStack>
        </FluentDialogBody>
        
        <FluentDialogFooter>
            <FluentButton Appearance="Appearance.Accent" OnClick="() => showScribanHelpDialog = false">
                Close
            </FluentButton>
        </FluentDialogFooter>
    </FluentDialog>
}

@code {
    private List<UserTemplate> templates = new();
    private UserTemplate? editingTemplate;
    private bool showEditor;
    private bool showScribanHelpDialog;
    private string? sampleJson;
    private TemplateRenderResult? testResult;
    
    [Inject] private IJSRuntime JSRuntime { get; set; } = default!;
    
    protected override async Task OnInitializedAsync()
    {
        await LoadTemplates();
    }
    
    private async Task LoadTemplates()
    {
        templates = (await TemplateStorage.GetAllTemplatesAsync()).ToList();
    }
    
    private void CreateNewTemplate()
    {
        editingTemplate = new UserTemplate
        {
            Id = Guid.Empty,
            Name = "New Template",
            Description = "",
            Category = "",
            JsonSchema = @"{
  ""$schema"": ""http://json-schema.org/draft-07/schema#"",
  ""type"": ""object"",
  ""properties"": {
    ""title"": { ""type"": ""string"" },
    ""content"": { ""type"": ""string"" }
  },
  ""required"": [""title"", ""content""]
}",
            ScribanTemplate = @"<h2>{{ title }}</h2>
<p>{{ content }}</p>",
            IsBuiltIn = false
        };
        sampleJson = @"{
  ""title"": ""Sample Title"",
  ""content"": ""Sample content goes here.""
}";
        showEditor = true;
    }
    
    private void EditTemplate(UserTemplate template)
    {
        // Clone the template for editing
        editingTemplate = new UserTemplate
        {
            Id = template.Id,
            Name = template.Name,
            Description = template.Description,
            Category = template.Category,
            JsonSchema = template.JsonSchema,
            ScribanTemplate = template.ScribanTemplate,
            IsBuiltIn = template.IsBuiltIn,
            CreatedBy = template.CreatedBy,
            CreatedAt = template.CreatedAt,
            ModifiedAt = template.ModifiedAt,
            Version = template.Version
        };
        sampleJson = null;
        testResult = null;
        showEditor = true;
    }
    
    private async Task SaveTemplate()
    {
        if (editingTemplate == null || string.IsNullOrWhiteSpace(editingTemplate.Name))
            return;
        
        await TemplateStorage.SaveTemplateAsync(editingTemplate);
        await LoadTemplates();
        
        showEditor = false;
        editingTemplate = null;
    }
    
    private void CancelEdit()
    {
        showEditor = false;
        editingTemplate = null;
        testResult = null;
    }
    
    private async Task DuplicateTemplate(UserTemplate template)
    {
        var duplicate = new UserTemplate
        {
            Id = Guid.Empty,
            Name = $"{template.Name} (Copy)",
            Description = template.Description,
            Category = template.Category,
            JsonSchema = template.JsonSchema,
            ScribanTemplate = template.ScribanTemplate,
            IsBuiltIn = false
        };
        
        await TemplateStorage.SaveTemplateAsync(duplicate);
        await LoadTemplates();
    }
    
    private async Task DeleteTemplate(UserTemplate template)
    {
        if (template.IsBuiltIn)
            return;
        
        // TODO: Add confirmation dialog
        await TemplateStorage.DeleteTemplateAsync(template.Id);
        await LoadTemplates();
    }
    
    private async Task ExportTemplate(UserTemplate template)
    {
        var json = JsonSerializer.Serialize(template, new JsonSerializerOptions 
        { 
            WriteIndented = true 
        });
        
        var fileName = $"{template.Name.Replace(" ", "_")}_template.json";
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        var base64 = Convert.ToBase64String(bytes);
        
        await JSRuntime.InvokeVoidAsync("downloadFile", fileName, base64);
    }
    
    private async Task ImportTemplate()
    {
        // TODO: Implement file picker and JSON import
        await Task.CompletedTask;
    }
    
    private async Task ValidateSchema()
    {
        if (editingTemplate == null)
            return;
        
        try
        {
            var schema = JsonSchema.FromText(editingTemplate.JsonSchema);
            // Schema is valid if no exception thrown
            await JSRuntime.InvokeVoidAsync("alert", "Schema is valid!");
        }
        catch (Exception ex)
        {
            await JSRuntime.InvokeVoidAsync("alert", $"Invalid schema: {ex.Message}");
        }
    }
    
    private async Task TestTemplate()
    {
        if (editingTemplate == null || string.IsNullOrWhiteSpace(sampleJson))
            return;
        
        // Validate first
        var validation = await SchemaValidator.ValidateJsonAsync(
            sampleJson,
            editingTemplate.JsonSchema);
        
        if (!validation.IsValid)
        {
            testResult = new TemplateRenderResult
            {
                Success = false,
                Errors = validation.Errors
            };
            return;
        }
        
        // Render
        testResult = await TemplateRenderer.RenderAsync(
            editingTemplate.ScribanTemplate,
            sampleJson);
    }
    
    private void ShowScribanHelp()
    {
        showScribanHelpDialog = true;
    }
}
```
CompleteFileStructure```
Consultologist-Blazor/
├── Services/
│   ├── Templates/
│   │   ├── ITemplateRenderService.cs
│   │   ├── ScribanTemplateRenderService.cs
│   │   ├── ITemplateStorageService.cs
│   │   ├── TemplateStorageService.cs
│   │   ├── ITemplateSeederService.cs
│   │   └── TemplateSeederService.cs
│   ├── Validation/
│   │   ├── ISchemaValidationService.cs
│   │   └── SchemaValidationService.cs
│   └── AI/
│       ├── IAIEndpointService.cs
│       └── AIEndpointService.cs
├── Models/
│   ├── Templates/
│   │   ├── UserTemplate.cs
│   │   ├── TemplateRenderResult.cs
│   │   ├── AIGenerationRequest.cs
│   │   └── AIGenerationResult.cs
│   └── Validation/
│       └── ValidationResult.cs
├── Extensions/
│   └── Scriban/
│       └── ScribanCustomFunctions.cs
├── Pages/
│   ├── Templates.razor (template builder)
│   └── Consults.razor (template consumer)
├── wwwroot/
│   ├── templates/
│   │   └── consult_template.scriban (built-in)
│   ├── schemas/
│   │   └── mortigen_render_context.schema.json (built-in)
│   ├── index.html (add JS helpers)
│   └── appsettings.json (AI endpoint config)
├── BlazorWasm.csproj (Scriban + Json.Schema.Net)
└── Program.cs (register services)
```
ImplementationTimeline###Week1 Foundation
- ✅ Add Scriban and Json.Schema.Net packages
- ✅ Implement all core services
- ✅ Convert oncology template to Scriban
- ✅ Seed built-in templates
- ✅ Test basic rendering### Week 2: Core UI
- ✅ Implement Consults.razor (consumer)
- ✅ Implement Templates.razor (builder - basic)
- ✅ Test end-to-end workflow
- ✅ AI endpoint integration

### Week 3: Enhanced UI
- ✅ Advanced template editor features
- ✅ Syntax highlighting (optional)
- ✅ Better validation feedback
- ✅ Export/import templates

### Week 4: Polish & Features
- ✅ Form builder from schema (optional)
- ✅ Template versioning
- ✅ Performance optimization
- ✅ Comprehensive error handling

### Week 5: Production
- ✅ Database migration (localStorage → Azure)
- ✅ Multi-user support
- ✅ Testing
- ✅ Documentation
SuccessCriteria✅ **Unified System**: All templates use JSON Schema + Scriban (no C# models)  
✅ **Built-in Template**: Oncology template migrated and functional  
✅ **Template Builder**: Users can create/edit templates in Templates.razor  
✅ **Template Consumer**: Users can generate notes in Consults.razor  
✅ **AI Integration**: User text → AI → structured JSON workflow works  
✅ **Validation**: JSON validation against schemas functional  
✅ **Rendering**: Templates render correctly to HTML  
✅ **Storage**: Templates persist (localStorage MVP, database production)  
✅ **Performance**: Rendering < 500ms for typical templates  
✅ **Error Handling**: User-friendly error messages  
## Next Steps

1. **Start Phase 1**: Add packages, create service interfaces
2. **Implement Core Services**: Rendering, storage, validation
3. **Convert Oncology Template**: Liquid → Scriban
4. **Build Consults.razor**: Basic consumer UI
5. **Build Templates.razor**: Basic builder UI
6. **Test Workflow**: Create template → use in consult
7. **Integrate AI Endpoint**: User text → structured JSON
8. **Iterate & Enhance**: Based on feedback

---

**Architecture**: Fully dynamic template system  
**Storage**: localStorage (MVP) → Database (production)  
**AI Workflow**: User text + Schema → AI → JSON → Template → HTML  
**No C# Models**: Everything is data-driven


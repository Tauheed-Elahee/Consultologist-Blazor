# Blazor Folder Structure Patterns Research

## Overview

This document provides a comprehensive analysis of folder structure patterns in Blazor applications across different project types and .NET versions. Understanding these patterns is crucial for organizing your Blazor projects effectively and following Microsoft's recommended conventions.

---

## Table of Contents

1. [Blazor Project Templates and Architectures](#blazor-project-templates-and-architectures)
2. [Folder Structure Patterns](#folder-structure-patterns)
3. [Folder Purpose Reference](#folder-purpose-reference)
4. [Evolution Across .NET Versions](#evolution-across-net-versions)
5. [Current Project Analysis](#current-project-analysis)
6. [Best Practices and Recommendations](#best-practices-and-recommendations)
7. [Key Takeaways](#key-takeaways)
8. [Decision Guide](#decision-guide)

---

## Blazor Project Templates and Architectures

### Current Templates (.NET 8+)

As of .NET 8/9, there are two primary Blazor templates:

#### 1. Blazor Web App (`blazor`)
**Modern unified template supporting:**
- Server-side rendering (SSR)
- Interactive Server mode
- Interactive WebAssembly mode  
- Auto mode (Server + WebAssembly hybrid)

**SDK**: `Microsoft.NET.Sdk.Web`

#### 2. Blazor WebAssembly Standalone (`blazorwasm`)
**Client-only application:**
- Runs entirely in the browser
- No server dependency after download
- Can be deployed as static files
- Downloads .NET runtime to browser

**SDK**: `Microsoft.NET.Sdk.BlazorWebAssembly`

### Important Changes in .NET 8

⚠️ **Removed Templates:**
- Blazor WebAssembly Hosted template (removed)
- Blazor Server template (replaced by Blazor Web App)

✅ **New Approach:**
- Multi-project solutions now use the Blazor Web App template
- Server-only apps use Blazor Web App
- Client-only apps use Blazor WebAssembly Standalone

---

## Folder Structure Patterns

### Pattern 1: Blazor WebAssembly Standalone

**Used For**: Client-only applications that run entirely in the browser

#### Modern Convention (.NET 9+)

```
ProjectRoot/
├── Pages/                      - Routable components (@page directive)
│   ├── Home.razor
│   ├── Counter.razor
│   └── Weather.razor
├── Layout/                     - Layout components (NEW convention)
│   ├── MainLayout.razor
│   └── NavMenu.razor
├── wwwroot/                    - Static assets (CSS, JS, images)
│   ├── css/
│   ├── js/
│   └── index.html
├── App.razor                   - Root component with Router
├── Program.cs                  - Entry point
├── _Imports.razor              - Global using statements
└── BlazorWasm.csproj
```

**_Imports.razor includes:**
```razor
@using ProjectName.Layout
```

#### Older Convention (.NET 6-7)

```
ProjectRoot/
├── Pages/
├── Shared/                     - Layout & shared components (OLD convention)
│   ├── MainLayout.razor
│   ├── NavMenu.razor
│   └── SurveyPrompt.razor
├── wwwroot/
├── App.razor
├── Program.cs
└── _Imports.razor
```

**_Imports.razor includes:**
```razor
@using ProjectName.Shared
```

---

### Pattern 2: Blazor Web App (Server-based)

**Used For**: Server-rendered apps with optional interactivity

```
ProjectRoot/
├── Components/                 - ROOT folder for ALL components
│   ├── Layout/                 - Layout components
│   │   ├── MainLayout.razor
│   │   └── NavMenu.razor
│   ├── Pages/                  - Routable pages
│   │   ├── Home.razor
│   │   ├── Counter.razor
│   │   └── Weather.razor
│   ├── App.razor               - HTML document root
│   ├── Routes.razor            - Router configuration
│   └── _Imports.razor
├── wwwroot/                    - Static assets
├── Program.cs                  - Entry point
└── BlazorWeb.csproj
```

**Key Difference**: `Components/` is the **root namespace** for all Blazor components.

**_Imports.razor includes:**
```razor
@using ProjectName.Components
@using ProjectName.Components.Layout
```

---

### Pattern 3: Multi-Project Solution (Server + Client)

**Used For**: Apps with both server-side and WebAssembly interactive modes

#### Solution Structure

```
Solution.sln
├── Solution/                           - Server project
│   ├── Components/
│   │   ├── App.razor                   - HTML root
│   │   ├── Pages/
│   │   │   └── Error.razor             - Server-only error page
│   │   └── _Imports.razor
│   ├── wwwroot/
│   ├── Program.cs
│   └── Solution.csproj                 - References Client project
│
└── Solution.Client/                    - Client project (WebAssembly)
    ├── Pages/                          - Client-side pages
    │   ├── Home.razor
    │   ├── Counter.razor
    │   └── Weather.razor
    ├── Layout/                         - Client-side layouts
    │   ├── MainLayout.razor
    │   └── NavMenu.razor
    ├── Routes.razor                    - Client router
    ├── Program.cs
    └── Solution.Client.csproj
```

#### Project References

**Server Project (`Solution.csproj`):**
```xml
<ItemGroup>
  <ProjectReference Include="..\Solution.Client\Solution.Client.csproj" />
</ItemGroup>
```

**Server Program.cs:**
```csharp
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

// ...

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(Solution.Client._Imports).Assembly);
```

**Client Project (`Solution.Client.csproj`):**
```xml
<Project Sdk="Microsoft.NET.Sdk.BlazorWebAssembly">
  <!-- Runs in browser -->
</Project>
```

---

## Folder Purpose Reference

| Folder | Project Type | Purpose | Examples |
|--------|-------------|---------|----------|
| **`Pages/`** | All | Routable components with `@page` directive | `Home.razor`, `Counter.razor` |
| **`Shared/`** | WebAssembly Standalone (old) | Layout and shared components | `MainLayout.razor`, `NavMenu.razor` |
| **`Layout/`** | WebAssembly Standalone (modern), Client projects | Layout components only | `MainLayout.razor`, `NavMenu.razor` |
| **`Components/`** | Blazor Web App (server) | **Root folder** for all components | Contains `Layout/` and `Pages/` |
| **`Components/Layout/`** | Blazor Web App | Layout components in server projects | `MainLayout.razor` |
| **`Components/Pages/`** | Blazor Web App | Routable pages in server projects | `Home.razor`, `Error.razor` |
| **`Client/`** | Multi-project solution | **Entire separate project** for WebAssembly | Separate `.csproj` file |
| **`wwwroot/`** | All | Static assets (CSS, JS, images) | `index.html`, `css/`, `favicon.ico` |

---

## Evolution Across .NET Versions

### .NET 6-7

**Blazor WebAssembly Template:**
```
/Pages/
/Shared/              ← Used this convention
/wwwroot/
```

**Hosted Blazor WebAssembly:**
```
Solution.sln
├── Client/          ← Separate project
├── Server/          ← Separate project
└── Shared/          ← Shared class library
```

**Blazor Server:**
```
/Pages/
/Shared/
/Data/
```

### .NET 8+ (Current)

**Changes:**
- ❌ **Removed**: Blazor WebAssembly Hosted template
- ❌ **Removed**: Blazor Server template
- ✅ **New**: Unified Blazor Web App template
- 🔄 **Changed**: WebAssembly standalone now uses `Layout/` instead of `Shared/`
- 🔄 **Changed**: Server-based apps use `Components/` as root folder

**Blazor WebAssembly Standalone:**
```
/Pages/
/Layout/              ← Now uses this
/wwwroot/
```

**Blazor Web App:**
```
/Components/          ← New root folder
  ├── Layout/
  └── Pages/
```

**Multi-Project (Web App with WebAssembly):**
```
/Server/
  └── Components/
/Server.Client/       ← Note: .Client suffix, not separate folder
  ├── Layout/
  └── Pages/
```

---

## Current Project Analysis

### Your Project Structure

**Location**: `/home/thegreat/Projects/GitHub/Consultologist-Blazor`

**Current Structure:**
```
/home/thegreat/Projects/GitHub/Consultologist-Blazor/
├── Pages/
│   ├── Index.razor
│   └── Profile.razor
├── Shared/                     ← Using older convention
│   ├── MainLayout.razor
│   ├── NavMenu.razor
│   └── LoginDisplay.razor
├── wwwroot/
│   ├── css/
│   ├── index.html
│   └── ...
├── App.razor
├── Program.cs
├── _Imports.razor
└── BlazorWasm.csproj
```

**Project Type:**
- SDK: `Microsoft.NET.Sdk.BlazorWebAssembly`
- Target: `net8.0`
- Type: **Standalone Blazor WebAssembly**

**_Imports.razor:**
```razor
@using BlazorWasm
@using BlazorWasm.Shared          ← References Shared folder
```

### Analysis

✅ **Your `Shared/` folder is perfectly valid**

**Why it works:**
- Came from Microsoft's official tutorial files
- Matches .NET 6-7 convention
- Fully functional for standalone WebAssembly apps
- Namespace is correctly configured

**Comparison with Modern Template:**

| Aspect | Your Project | Modern .NET 9 Template |
|--------|--------------|------------------------|
| Folder Name | `Shared/` | `Layout/` |
| Namespace | `BlazorWasm.Shared` | `BlazorWasm.Layout` |
| Contents | Layout components | Layout components |
| Functionality | ✅ Works perfectly | ✅ Works perfectly |
| Convention | Older (valid) | Current |

---

## Best Practices and Recommendations

### For Your Current Project

#### Option A: Keep `Shared/` (Recommended)

**Pros:**
- ✅ Already working perfectly
- ✅ Matches Microsoft tutorial code
- ✅ No migration needed
- ✅ Zero risk of breaking changes

**Cons:**
- ⚠️ Slightly outdated naming convention
- ⚠️ May confuse developers familiar with newer templates

**Recommendation**: **Keep it as-is** unless you have other reasons to restructure.

#### Option B: Migrate to `Layout/`

**Pros:**
- ✅ Matches modern .NET 9 convention
- ✅ More semantic naming (these ARE layout components)
- ✅ Future-proof

**Cons:**
- ⚠️ Requires folder rename
- ⚠️ Need to update `_Imports.razor`
- ⚠️ Risk of breaking references

**Migration Steps (if chosen):**
1. Rename `Shared/` folder to `Layout/`
2. Update `_Imports.razor`:
   ```razor
   @using BlazorWasm.Layout
   ```
3. Update any direct namespace references in code
4. Test thoroughly

---

### For New Projects

#### Standalone WebAssembly

**Use this structure:**
```
/Pages/
/Layout/              ← Use Layout, not Shared
/wwwroot/
```

**Template command:**
```bash
dotnet new blazorwasm -n MyProject
```

#### Server-based Application

**Use this structure:**
```
/Components/
  ├── Layout/
  └── Pages/
/wwwroot/
```

**Template command:**
```bash
dotnet new blazor -n MyProject
```

#### Multi-Project Solution

**Use this structure:**
```
/MyProject/
  └── Components/
/MyProject.Client/
  ├── Layout/
  └── Pages/
```

**Template command:**
```bash
dotnet new blazor -n MyProject -int WebAssembly -ai
```

---

### General Conventions

1. **Use `Pages/` for routable components** (those with `@page` directive)
2. **Use `Layout/` for layout components** in WebAssembly and client projects
3. **Use `Components/` as root** in server-based Blazor Web Apps
4. **Use meaningful subfolders** for organization:
   - `/Components/Forms/`
   - `/Components/Dialogs/`
   - `/Services/`
   - `/Models/`

---

## Key Takeaways

### 🔑 Critical Points

1. **`Client/` is a separate project**, not a folder within a single project
   - Only appears in multi-project solutions
   - Has its own `.csproj` file
   - Different SDK: `Microsoft.NET.Sdk.BlazorWebAssembly`

2. **`Components/` is a root folder** in Blazor Web App templates
   - Contains both `Layout/` and `Pages/`
   - Used in server-based applications
   - Different namespace structure

3. **`Shared/` → `Layout/` evolution**
   - `Shared/` was the old convention
   - `Layout/` is the modern convention
   - Both work perfectly for standalone WebAssembly
   - Change is purely semantic

4. **Your project is correct**
   - Using valid Microsoft convention
   - Came from official tutorials
   - No need to change unless desired

### 📊 Quick Reference

| Scenario | Folder Structure |
|----------|------------------|
| New standalone WebAssembly | `/Pages/`, `/Layout/` |
| Old standalone WebAssembly | `/Pages/`, `/Shared/` ✅ Still valid |
| New server-based app | `/Components/Layout/`, `/Components/Pages/` |
| Multi-project solution | Server: `/Components/`<br>Client: `/Layout/`, `/Pages/` |

### 🎯 When to Use What

**Use `Shared/` when:**
- Maintaining existing projects
- Working with .NET 6-7 code
- Following older tutorials

**Use `Layout/` when:**
- Creating new standalone WebAssembly projects
- Following .NET 9 conventions
- Components are specifically layout-related

**Use `Components/` when:**
- Building a Blazor Web App (server-based)
- Need server-side rendering (SSR)
- Want unified SSR + interactive components

**Use `Client/` project when:**
- Building a multi-project solution
- Need both server and WebAssembly rendering
- Want code sharing between server and client

---

## Decision Guide

### Should I Migrate from `Shared/` to `Layout/`?

**Ask yourself:**

1. **Is your project actively developed?**
   - Yes → Consider migrating to modern convention
   - No → Keep as-is

2. **Do you follow latest .NET templates?**
   - Yes → Migrate to `Layout/`
   - No → Keep `Shared/`

3. **Is this causing confusion for your team?**
   - Yes → Migrate for clarity
   - No → Keep as-is

4. **Do you have time for testing after changes?**
   - Yes → Safe to migrate
   - No → Keep as-is (too risky)

### Recommendation Matrix

| Project Status | Team Familiarity | Recommendation |
|---------------|------------------|----------------|
| Stable production | .NET 6-7 | **Keep `Shared/`** |
| Active development | .NET 8+ | **Consider `Layout/`** |
| New project | Any | **Use `Layout/`** |
| Legacy maintenance | Any | **Keep `Shared/`** |

---

## Additional Resources

### Official Documentation
- [Blazor project structure](https://learn.microsoft.com/en-us/aspnet/core/blazor/project-structure)
- [Blazor Web App documentation](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/render-modes)
- [.NET project templates](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-new)

### Related Files in Your Project
- Project file: `/home/thegreat/Projects/GitHub/Consultologist-Blazor/BlazorWasm.csproj`
- Shared folder: `/home/thegreat/Projects/GitHub/Consultologist-Blazor/Shared/`
- Imports: `/home/thegreat/Projects/GitHub/Consultologist-Blazor/_Imports.razor`

---

**Document Version**: 1.0  
**Last Updated**: 2025-11-20  
**Project**: Consultologist Blazor WebAssembly  
**Research Date**: 2025-11-20

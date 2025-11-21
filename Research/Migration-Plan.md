# Migration Plan: Bootstrap 5.1.0 → Microsoft Fluent UI Blazor

## Overview

This document provides a comprehensive, step-by-step plan to migrate the Consultologist Blazor WebAssembly application from Bootstrap 5.1.0 to Microsoft Fluent UI Blazor (`Microsoft.FluentUI.AspNetCore.Components`).

### Current Stack
- **UI Framework**: Bootstrap 5.1.0 (CSS only)
- **Icons**: Open Iconic
- **Runtime**: .NET 8.0
- **Authentication**: MSAL (Microsoft Authentication Library)
- **Components**: Standard Blazor components with Bootstrap CSS classes

### Target Stack
- **UI Framework**: Microsoft Fluent UI Blazor 4.x
- **Icons**: Fluent UI System Icons (2,000+ SVG icons)
- **Runtime**: .NET 8.0 (no change)
- **Authentication**: MSAL (no change)
- **Components**: Native Fluent UI Blazor components

### Benefits of Migration
✅ Modern Microsoft design language (consistent with Microsoft 365, Windows 11)  
✅ 70+ native Blazor components  
✅ No jQuery dependency  
✅ Better accessibility  
✅ Built-in theming (light/dark mode)  
✅ Comprehensive icon library  
✅ Fully compatible with existing MSAL authentication  

### Estimated Time
**Total: 4-7 hours** (can be done incrementally)

---

## Prerequisites

Before starting:
- [ ] Backup your project or commit current changes to git
- [ ] Ensure the project builds successfully
- [ ] Have .NET 8.0 SDK installed
- [ ] Review [Fluent UI Blazor documentation](https://www.fluentui-blazor.net/)

---

## Phase 1: Installation & Setup

**Estimated Time: 30-45 minutes**

### Step 1.1: Add NuGet Package

**File**: `BlazorWasm.csproj`

Add the Fluent UI package reference:

```xml
<PackageReference Include="Microsoft.FluentUI.AspNetCore.Components" Version="4.*" />
```

**Complete package references section:**
```xml
<ItemGroup>
  <PackageReference Include="Microsoft.AspNetCore.Components.WebAssembly" Version="8.*" />
  <PackageReference Include="Microsoft.Authentication.WebAssembly.Msal" Version="8.*" />
  <PackageReference Include="Microsoft.Extensions.Http" Version="8.*" />
  <PackageReference Include="Microsoft.FluentUI.AspNetCore.Components" Version="4.*" />
</ItemGroup>
```

**Command to run:**
```bash
dotnet add package Microsoft.FluentUI.AspNetCore.Components
dotnet restore
```

---

### Step 1.2: Update Program.cs

**File**: `Program.cs`

**Add using statement** at the top:
```csharp
using Microsoft.FluentUI.AspNetCore.Components;
```

**Add service registration** after creating the builder:
```csharp
builder.Services.AddFluentUIComponents();
```

**Complete updated Program.cs:**
```csharp
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using BlazorWasm;
using Microsoft.FluentUI.AspNetCore.Components; // ADD THIS

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// ADD THIS LINE
builder.Services.AddFluentUIComponents();

builder.Services.AddMsalAuthentication(options =>
{
    builder.Configuration.Bind("AzureAd", options.ProviderOptions.Authentication);
    options.ProviderOptions.DefaultAccessTokenScopes
            .Add("https://graph.microsoft.com/User.Read");
});

builder.Services.AddScoped(sp =>
{
    var authorizationMessageHandler =
        sp.GetRequiredService<AuthorizationMessageHandler>();
    authorizationMessageHandler.InnerHandler = new HttpClientHandler();
    authorizationMessageHandler.ConfigureHandler(
        authorizedUrls: new[] { "https://graph.microsoft.com/v1.0" },
        scopes: new[] { "User.Read" });

    return new HttpClient(authorizationMessageHandler);
});

await builder.Build().RunAsync();
```

---

### Step 1.3: Update _Imports.razor

**File**: `_Imports.razor`

**Add** at the end of the file:
```razor
@using Microsoft.FluentUI.AspNetCore.Components
```

**Complete updated _Imports.razor:**
```razor
@using System.Net.Http
@using System.Net.Http.Json
@using Microsoft.AspNetCore.Components.Forms
@using Microsoft.AspNetCore.Components.Routing
@using Microsoft.AspNetCore.Components.Web
@using Microsoft.AspNetCore.Components.Web.Virtualization
@using Microsoft.AspNetCore.Components.WebAssembly.Http
@using Microsoft.JSInterop
@using BlazorWasm
@using BlazorWasm.Shared
@using Microsoft.AspNetCore.Components.Authorization
@using Microsoft.AspNetCore.Components.WebAssembly.Authentication
@using Microsoft.FluentUI.AspNetCore.Components
```

---

### Step 1.4: Update wwwroot/index.html

**File**: `wwwroot/index.html`

**REMOVE these lines** from `<head>`:
```html
<link href="css/bootstrap/bootstrap.min.css" rel="stylesheet" />
```

**ADD these lines** in `<head>`:
```html
<link href="_content/Microsoft.FluentUI.AspNetCore.Components/css/reboot.css" rel="stylesheet" />
<link href="_content/Microsoft.FluentUI.AspNetCore.Components/css/fluent.css" rel="stylesheet" />
```

**REPLACE the loading indicator** in `<div id="app">`:

**Before:**
```html
<div id="app">
    <svg class="loading-progress">
        <circle r="40%" cx="50%" cy="50%" />
        <circle r="40%" cx="50%" cy="50%" />
    </svg>
    <div class="loading-progress-text"></div>
</div>
```

**After:**
```html
<div id="app">
    <fluent-progress-ring></fluent-progress-ring>
    <div style="text-align: center; margin-top: 20px;">Loading...</div>
</div>
```

**ADD this script** before closing `</body>` tag:
```html
<script src="_content/Microsoft.FluentUI.AspNetCore.Components/Microsoft.FluentUI.AspNetCore.Components.lib.module.js" type="module" async></script>
```

**Complete updated index.html:**
```html
<!DOCTYPE html>
<html lang="en">

<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no" />
    <title>BlazorWasm</title>
    <base href="/" />
    <!-- FLUENT UI CSS -->
    <link href="_content/Microsoft.FluentUI.AspNetCore.Components/css/reboot.css" rel="stylesheet" />
    <link href="_content/Microsoft.FluentUI.AspNetCore.Components/css/fluent.css" rel="stylesheet" />
    <!-- Custom CSS -->
    <link href="css/app.css" rel="stylesheet" />
    <link href="BlazorWasm.styles.css" rel="stylesheet" />
    <link href="manifest.json" rel="manifest" />
    <link rel="apple-touch-icon" sizes="512x512" href="icon-512.png" />
    <link rel="apple-touch-icon" sizes="192x192" href="icon-192.png" />
</head>

<body>
    <div id="app">
        <fluent-progress-ring></fluent-progress-ring>
        <div style="text-align: center; margin-top: 20px;">Loading...</div>
    </div>

    <div id="blazor-error-ui">
        An unhandled error has occurred.
        <a href="" class="reload">Reload</a>
        <a class="dismiss">🗙</a>
    </div>
    <script src="_content/Microsoft.Authentication.WebAssembly.Msal/AuthenticationService.js"></script>
    <script src="_framework/blazor.webassembly.js"></script>
    <script src="_content/Microsoft.FluentUI.AspNetCore.Components/Microsoft.FluentUI.AspNetCore.Components.lib.module.js" type="module" async></script>
    <script>navigator.serviceWorker.register('service-worker.js');</script>
</body>

</html>
```

---

### Step 1.5: Update wwwroot/css/app.css

**File**: `wwwroot/css/app.css`

**REMOVE this line:**
```css
@import url('open-iconic/font/css/open-iconic-bootstrap.min.css');
```

**REMOVE Bootstrap-specific loading animation** (the `.loading-progress` styles)

**UPDATE to use Fluent UI CSS variables:**

**Complete updated app.css:**
```css
html, body {
    font-family: var(--body-font);
}

h1:focus {
    outline: none;
}

a {
    color: var(--accent-fill-rest);
}

.valid.modified:not([type=checkbox]) {
    outline: 1px solid var(--success);
}

.invalid {
    outline: 1px solid var(--error);
}

.validation-message {
    color: var(--error);
}

#blazor-error-ui {
    background: lightyellow;
    bottom: 0;
    box-shadow: 0 -1px 2px rgba(0, 0, 0, 0.2);
    display: none;
    left: 0;
    padding: 0.6rem 1.25rem 0.7rem 1.25rem;
    position: fixed;
    width: 100%;
    z-index: 1000;
}

#blazor-error-ui .dismiss {
    cursor: pointer;
    position: absolute;
    right: 0.75rem;
    top: 0.5rem;
}

.blazor-error-boundary {
    background: url(data:image/svg+xml;base64,PHN2ZyB3aWR0aD0iNTYiIGhlaWdodD0iNDkiIHhtbG5zPSJodHRwOi8vd3d3LnczLm9yZy8yMDAwL3N2ZyIgeG1sbnM6eGxpbms9Imh0dHA6Ly93d3cudzMub3JnLzE5OTkveGxpbmsiIG92ZXJmbG93PSJoaWRkZW4iPjxkZWZzPjxjbGlwUGF0aCBpZD0iY2xpcDAiPjxyZWN0IHg9IjIzNSIgeT0iNTEiIHdpZHRoPSI1NiIgaGVpZ2h0PSI0OSIvPjwvY2xpcFBhdGg+PC9kZWZzPjxnIGNsaXAtcGF0aD0idXJsKCNjbGlwMCkiIHRyYW5zZm9ybT0idHJhbnNsYXRlKC0yMzUgLTUxKSI+PHBhdGggZD0iTTI2My41MDYgNTFDMjY0LjcxNyA1MSAyNjUuODEzIDUxLjQ4MzcgMjY2LjYwNiA1Mi4yNjU4TDI2Ny4wNTIgNTIuNzk4NyAyNjcuNTM5IDUzLjYyODMgMjkwLjE4NSA5Mi4xODMxIDI5MC41NDUgOTIuNzk1IDI5MC42NTYgOTIuOTk2QzI5MC44NzcgOTMuNTEzIDI5MSA5NC4wODE1IDI5MSA5NC42NzgyIDI5MSA5Ny4wNjUxIDI4OS4wMzggOTkgMjg2LjYxNyA5OUwyNDAuMzgzIDk5QzIzNy45NjMgOTkgMjM2IDk3LjA2NTEgMjM2IDk0LjY3ODIgMjM2IDk0LjM3OTkgMjM2LjAzMSA5NC4wODg2IDIzNi4wODkgOTMuODA3MkwyMzYuMzM4IDkzLjAxNjIgMjM2Ljg1OCA5Mi4xMzE0IDI1OS40NzMgNTMuNjI5NCAyNTkuOTYxIDUyLjc5ODUgMjYwLjQwNyA1Mi4yNjU4QzI2MS4yIDUxLjQ4MzcgMjYyLjI5NiA1MSAyNjMuNTA2IDUxWk0yNjMuNTg2IDY2LjAxODNDMjYwLjczNyA2Ni4wMTgzIDI1OS4zMTMgNjcuMTI0NSAyNTkuMzEzIDY5LjMzNyAyNTkuMzEzIDY5LjYxMDIgMjU5LjMzMiA2OS44NjA4IDI1OS4zNzEgNzAuMDg4N0wyNjEuNzk1IDg0LjAxNjEgMjY1LjM4IDg0LjAxNjEgMjY3LjgyMSA2OS43NDc1QzI2Ny44NiA2OS43MzA5IDI2Ny44NzkgNjkuNTg3NyAyNjcuODc5IDY5LjMxNzkgMjY3Ljg3OSA2Ny4xMTgyIDI2Ni40NDggNjYuMDE4MyAyNjMuNTg2IDY2LjAxODNaTTI2My41NzYgODYuMDU0N0MyNjEuMDQ5IDg2LjA1NDcgMjU5Ljc4NiA4Ny4zMDA1IDI1OS43ODYgODkuNzkyMSAyNTkuNzg2IDkyLjI4MzcgMjYxLjA0OSA5My41Mjk1IDI2My41NzYgOTMuNTI5NSAyNjYuMTE2IDkzLjUyOTUgMjY3LjM4NyA5Mi4yODM3IDI2Ny4zODcgODkuNzkyMSAyNjcuMzg3IDg3LjMwMDUgMjY2LjExNiA4Ni4wNTQ3IDI2My41NzYgODYuMDU0N1oiIGZpbGw9IiNGRkU1MDAiIGZpbGwtcnVsZT0iZXZlbm9kZCIvPjwvZz48L3N2Zz4=) no-repeat 1rem/1.8rem, #b32121;
    padding: 1rem 1rem 1rem 3.7rem;
    color: white;
}

.blazor-error-boundary::after {
    content: "An error has occurred."
}
```

---

### Step 1.6: Test Installation

**Build and run the application:**
```bash
dotnet build
dotnet run
```

**Verify:**
- [ ] Application builds without errors
- [ ] Application runs and loads
- [ ] Fluent UI loading spinner appears briefly
- [ ] No console errors related to Fluent UI

---

## Phase 2: Layout Components

**Estimated Time: 1-2 hours**

### Step 2.1: Read Current MainLayout.razor

**File**: `Shared/MainLayout.razor`

**Current structure** (Bootstrap-based):
```razor
@inherits LayoutComponentBase

<div class="page">
    <div class="sidebar">
        <NavMenu />
    </div>

    <main>
        <div class="top-row px-4 auth">
            <LoginDisplay />
        </div>

        <article class="content px-4">
            @Body
        </article>
    </main>
</div>
```

---

### Step 2.2: Update MainLayout.razor to Fluent UI

**File**: `Shared/MainLayout.razor`

**Option A: Simple Migration (Recommended for gradual transition)**
```razor
@inherits LayoutComponentBase

<FluentLayout>
    <FluentStack Orientation="Orientation.Horizontal" Width="100%" Style="min-height: 100vh;">
        <div class="sidebar">
            <NavMenu />
        </div>

        <FluentStack Orientation="Orientation.Vertical" Width="100%">
            <FluentStack Orientation="Orientation.Horizontal" 
                         HorizontalAlignment="HorizontalAlignment.End"
                         Class="top-bar">
                <LoginDisplay />
            </FluentStack>

            <FluentStack Class="content" Style="padding: 2rem;">
                @Body
            </FluentStack>
        </FluentStack>
    </FluentStack>
</FluentLayout>
```

**Option B: Full Fluent UI Components**
```razor
@inherits LayoutComponentBase

<FluentLayout>
    <FluentHeader Style="background: var(--neutral-layer-2); padding: 1rem;">
        <FluentStack Orientation="Orientation.Horizontal" 
                     HorizontalAlignment="HorizontalAlignment.SpaceBetween"
                     Width="100%">
            <FluentLabel Typo="Typography.H4">Consultologist</FluentLabel>
            <LoginDisplay />
        </FluentStack>
    </FluentHeader>
    
    <FluentStack Orientation="Orientation.Horizontal" Style="height: 100%;">
        <FluentNavMenu Width="250px" Collapsible="true" Title="Navigation">
            <NavMenu />
        </FluentNavMenu>
        
        <FluentBodyContent Style="padding: 2rem;">
            @Body
        </FluentBodyContent>
    </FluentStack>
</FluentLayout>
```

**Choose the option that best fits your needs.** Option A is simpler and maintains more of the existing structure.

---

### Step 2.3: Update MainLayout.razor.css

**File**: `Shared/MainLayout.razor.css`

**Current** (Bootstrap-specific):
```css
.page {
    position: relative;
    display: flex;
    flex-direction: column;
}

main {
    flex: 1;
}

.sidebar {
    background-image: linear-gradient(180deg, rgb(5, 39, 103) 0%, #3a0647 70%);
}

.top-row {
    background-color: #f7f7f7;
    border-bottom: 1px solid #d6d5d5;
    justify-content: flex-end;
    height: 3.5rem;
    display: flex;
    align-items: center;
}

    .top-row ::deep a, .top-row ::deep .btn-link {
        white-space: nowrap;
        margin-left: 1.5rem;
        text-decoration: none;
    }

    .top-row ::deep a:hover, .top-row ::deep .btn-link:hover {
        text-decoration: underline;
    }

    .top-row ::deep a:first-child {
        overflow: hidden;
        text-overflow: ellipsis;
    }

@media (max-width: 640.98px) {
    .top-row:not(.auth) {
        display: none;
    }

    .top-row.auth {
        justify-content: space-between;
    }

    .top-row ::deep a, .top-row ::deep .btn-link {
        margin-left: 0;
    }
}

@media (min-width: 641px) {
    .page {
        flex-direction: row;
    }

    .sidebar {
        width: 250px;
        height: 100vh;
        position: sticky;
        top: 0;
    }

    .top-row {
        position: sticky;
        top: 0;
        z-index: 1;
    }

    .top-row.auth ::deep a:first-child {
        flex: 1;
        text-align: right;
        width: 0;
    }

    .top-row, article {
        padding-left: 2rem !important;
        padding-right: 1.5rem !important;
    }
}
```

**Updated** (Fluent UI):
```css
.sidebar {
    background: var(--neutral-layer-1);
    min-height: 100vh;
    width: 250px;
}

.top-bar {
    background: var(--neutral-layer-2);
    border-bottom: 1px solid var(--neutral-stroke-rest);
    padding: 1rem 2rem;
}

.content {
    padding: 2rem;
    flex: 1;
}

@media (max-width: 640px) {
    .sidebar {
        width: 100%;
        min-height: auto;
    }
}
```

---

### Step 2.4: Test Layout

**Verify:**
- [ ] Layout renders correctly
- [ ] Sidebar appears on the left
- [ ] Top bar shows LoginDisplay component
- [ ] Content area displays page body
- [ ] Responsive behavior works on mobile

---

## Phase 3: Navigation Menu

**Estimated Time: 1-2 hours**

### Step 3.1: Current NavMenu.razor

**File**: `Shared/NavMenu.razor`

**Current** (Bootstrap with Open Iconic):
```razor
<div class="top-row ps-3 navbar navbar-dark">
    <div class="container-fluid">
        <a class="navbar-brand" href="">BlazorWasm</a>
        <button title="Navigation menu" class="navbar-toggler" @onclick="ToggleNavMenu">
            <span class="navbar-toggler-icon"></span>
        </button>
    </div>
</div>

<div class="@NavMenuCssClass" @onclick="ToggleNavMenu">
    <nav class="flex-column">
        <div class="nav-item px-3">
            <NavLink class="nav-link" href="" Match="NavLinkMatch.All">
                <span class="oi oi-home" aria-hidden="true"></span> Home
            </NavLink>
        </div>
        <AuthorizeView>
            <Authorized>
                <div class="nav-item px-3">
                    <NavLink class="nav-link" href="profile">
                        <span class="oi oi-person" aria-hidden="true"></span> Profile
                    </NavLink>
                </div>
            </Authorized>
        </AuthorizeView>
    </nav>
</div>

@code {
    private bool collapseNavMenu = true;

    private string? NavMenuCssClass => collapseNavMenu ? "collapse" : null;

    private void ToggleNavMenu()
    {
        collapseNavMenu = !collapseNavMenu;
    }
}
```

---

### Step 3.2: Update NavMenu.razor to Fluent UI

**File**: `Shared/NavMenu.razor`

**Option A: Using FluentNavMenu (Recommended)**
```razor
@using Microsoft.FluentUI.AspNetCore.Components

<FluentNavMenu Width="250" Collapsible="true">
    <FluentNavLink Href="" Match="NavLinkMatch.All" Icon="@(new Icons.Regular.Size20.Home())">
        Home
    </FluentNavLink>
    
    <AuthorizeView>
        <Authorized>
            <FluentNavLink Href="profile" Icon="@(new Icons.Regular.Size20.Person())">
                Profile
            </FluentNavLink>
        </Authorized>
    </AuthorizeView>
</FluentNavMenu>
```

**Option B: More Detailed with Header**
```razor
@using Microsoft.FluentUI.AspNetCore.Components

<FluentStack Orientation="Orientation.Vertical" Width="100%">
    <FluentStack Orientation="Orientation.Horizontal" Class="nav-header">
        <FluentLabel Typo="Typography.H5" Color="Color.Accent">BlazorWasm</FluentLabel>
    </FluentStack>
    
    <FluentDivider Style="width: 100%;" />
    
    <FluentNavMenu Width="100%">
        <FluentNavLink Href="" Match="NavLinkMatch.All" 
                       Icon="@(new Icons.Regular.Size20.Home())">
            Home
        </FluentNavLink>
        
        <AuthorizeView>
            <Authorized>
                <FluentNavLink Href="profile" 
                               Icon="@(new Icons.Regular.Size20.Person())">
                    Profile
                </FluentNavLink>
            </Authorized>
        </AuthorizeView>
    </FluentNavMenu>
</FluentStack>
```

**Option C: Custom Navigation with Standard NavLink**
```razor
@using Microsoft.FluentUI.AspNetCore.Components

<FluentStack Orientation="Orientation.Vertical" Width="250px" Style="padding: 1rem;">
    <FluentLabel Typo="Typography.H5" Style="margin-bottom: 1rem;">BlazorWasm</FluentLabel>
    
    <FluentDivider />
    
    <nav style="margin-top: 1rem;">
        <FluentStack Orientation="Orientation.Vertical" VerticalGap="8">
            <NavLink class="nav-link" href="" Match="NavLinkMatch.All">
                <FluentStack Orientation="Orientation.Horizontal" HorizontalGap="8">
                    <FluentIcon Value="@(new Icons.Regular.Size20.Home())" />
                    <span>Home</span>
                </FluentStack>
            </NavLink>
            
            <AuthorizeView>
                <Authorized>
                    <NavLink class="nav-link" href="profile">
                        <FluentStack Orientation="Orientation.Horizontal" HorizontalGap="8">
                            <FluentIcon Value="@(new Icons.Regular.Size20.Person())" />
                            <span>Profile</span>
                        </FluentStack>
                    </NavLink>
                </Authorized>
            </AuthorizeView>
        </FluentStack>
    </nav>
</FluentStack>

<style>
    .nav-link {
        display: block;
        padding: 0.5rem 1rem;
        color: var(--neutral-foreground-rest);
        text-decoration: none;
        border-radius: 4px;
    }
    
    .nav-link:hover {
        background: var(--neutral-fill-secondary-hover);
    }
    
    .nav-link.active {
        background: var(--accent-fill-rest);
        color: var(--neutral-foreground-on-accent);
    }
</style>
```

---

### Step 3.3: Icon Migration Reference

**Open Iconic → Fluent Icons:**

| Open Iconic | Fluent Icon |
|-------------|-------------|
| `<span class="oi oi-home">` | `<FluentIcon Value="@(new Icons.Regular.Size20.Home())" />` |
| `<span class="oi oi-person">` | `<FluentIcon Value="@(new Icons.Regular.Size20.Person())" />` |
| `<span class="oi oi-list">` | `<FluentIcon Value="@(new Icons.Regular.Size20.List())" />` |
| `<span class="oi oi-cog">` | `<FluentIcon Value="@(new Icons.Regular.Size20.Settings())" />` |
| `<span class="oi oi-plus">` | `<FluentIcon Value="@(new Icons.Regular.Size20.Add())" />` |
| `<span class="oi oi-pencil">` | `<FluentIcon Value="@(new Icons.Regular.Size20.Edit())" />` |
| `<span class="oi oi-trash">` | `<FluentIcon Value="@(new Icons.Regular.Size20.Delete())" />` |

**Icon sizes available:**
- `Size16` - 16x16px
- `Size20` - 20x20px (recommended for navigation)
- `Size24` - 24x24px
- `Size28` - 28x28px
- `Size32` - 32x32px
- `Size48` - 48x48px

**Icon variants:**
- `Icons.Regular.Size20.Home()` - Regular (outline)
- `Icons.Filled.Size20.Home()` - Filled (solid)

---

### Step 3.4: Update NavMenu.razor.css

**File**: `Shared/NavMenu.razor.css`

**Current** (Bootstrap-specific):
```css
.navbar-toggler {
    appearance: none;
    cursor: pointer;
    width: 3.5rem;
    height: 2.5rem;
    color: white;
    position: absolute;
    top: 0.5rem;
    right: 1rem;
    border: 1px solid rgba(255, 255, 255, 0.1);
    background: url("data:image/svg+xml;...") transparent no-repeat center/1.75rem;
    border-radius: 0.25rem;
}

.navbar-toggler:hover, .navbar-toggler:focus {
    background-color: rgba(255, 255, 255, 0.1);
}

.top-row {
    height: 3.5rem;
    background-color: rgba(0,0,0,0.4);
}

.navbar-brand {
    font-size: 1.1rem;
}

.oi {
    width: 2rem;
    font-size: 1.1rem;
    vertical-align: text-top;
    top: -2px;
}

.nav-item {
    font-size: 0.9rem;
    padding-bottom: 0.5rem;
}

    .nav-item:first-of-type {
        padding-top: 1rem;
    }

    .nav-item:last-of-type {
        padding-bottom: 1rem;
    }

    .nav-item ::deep a {
        color: #d7d7d7;
        border-radius: 4px;
        height: 3rem;
        display: flex;
        align-items: center;
        line-height: 3rem;
    }

.nav-item ::deep a.active {
    background-color: rgba(255,255,255,0.37);
    color: white;
}

.nav-item ::deep a:hover {
    background-color: rgba(255,255,255,0.1);
    color: white;
}

@media (min-width: 641px) {
    .navbar-toggler {
        display: none;
    }

    .collapse {
        display: block;
    }
}
```

**Updated** (Fluent UI - Minimal or delete file):

**Option A: Delete the file** if using FluentNavMenu (it has built-in styling)

**Option B: Keep minimal custom styles:**
```css
.nav-header {
    padding: 1.5rem 1rem;
    background: var(--neutral-layer-2);
}

/* Custom active state if needed */
.nav-link.active {
    background: var(--accent-fill-rest);
    color: var(--neutral-foreground-on-accent);
}
```

---

### Step 3.5: Test Navigation

**Verify:**
- [ ] Navigation menu displays correctly
- [ ] Home link appears for all users
- [ ] Profile link only appears when logged in
- [ ] Icons render correctly (Fluent icons, not Open Iconic)
- [ ] Active state highlights current page
- [ ] Mobile responsive behavior works
- [ ] Navigation to pages works correctly

---

## Phase 4: Authentication UI (LoginDisplay)

**Estimated Time: 30-45 minutes**

### Step 4.1: Check if LoginDisplay.razor Exists

**File**: `Shared/LoginDisplay.razor`

**If it doesn't exist**, check if authentication UI is in MainLayout.razor or another component.

---

### Step 4.2: Update LoginDisplay.razor

**Current** (Bootstrap-based):
```razor
@using Microsoft.AspNetCore.Components.Authorization
@using Microsoft.AspNetCore.Components.WebAssembly.Authentication
@inject NavigationManager Navigation

<AuthorizeView>
    <Authorized>
        Hello, @context.User.Identity?.Name!
        <button class="nav-link btn btn-link" @onclick="BeginLogout">Log out</button>
    </Authorized>
    <NotAuthorized>
        <a href="authentication/login">Log in</a>
    </NotAuthorized>
</AuthorizeView>

@code {
    public void BeginLogout()
    {
        Navigation.NavigateToLogout("authentication/logout");
    }
}
```

**Updated** (Fluent UI):
```razor
@using Microsoft.AspNetCore.Components.Authorization
@using Microsoft.AspNetCore.Components.WebAssembly.Authentication
@using Microsoft.FluentUI.AspNetCore.Components
@inject NavigationManager Navigation

<AuthorizeView>
    <Authorized>
        <FluentStack Orientation="Orientation.Horizontal" HorizontalGap="16" VerticalAlignment="VerticalAlignment.Center">
            <FluentPersona Name="@context.User.Identity?.Name" 
                          ImageSize="32px"
                          Style="font-size: 14px;" />
            <FluentButton Appearance="Appearance.Lightweight" @onclick="BeginLogout">
                Log out
            </FluentButton>
        </FluentStack>
    </Authorized>
    <NotAuthorized>
        <FluentAnchor Href="authentication/login" Appearance="Appearance.Hypertext">
            Log in
        </FluentAnchor>
    </NotAuthorized>
</AuthorizeView>

@code {
    public void BeginLogout()
    {
        Navigation.NavigateToLogout("authentication/logout");
    }
}
```

**Alternative** (Simpler without Persona):
```razor
@using Microsoft.AspNetCore.Components.Authorization
@using Microsoft.AspNetCore.Components.WebAssembly.Authentication
@using Microsoft.FluentUI.AspNetCore.Components
@inject NavigationManager Navigation

<AuthorizeView>
    <Authorized>
        <FluentStack Orientation="Orientation.Horizontal" HorizontalGap="12" VerticalAlignment="VerticalAlignment.Center">
            <FluentIcon Value="@(new Icons.Regular.Size20.Person())" />
            <FluentLabel>@context.User.Identity?.Name</FluentLabel>
            <FluentButton Appearance="Appearance.Lightweight" @onclick="BeginLogout">
                Log out
            </FluentButton>
        </FluentStack>
    </Authorized>
    <NotAuthorized>
        <FluentButton Appearance="Appearance.Accent" Href="authentication/login">
            Log in
        </FluentButton>
    </NotAuthorized>
</AuthorizeView>

@code {
    public void BeginLogout()
    {
        Navigation.NavigateToLogout("authentication/logout");
    }
}
```

---

### Step 4.3: Test Authentication UI

**Verify:**
- [ ] Login button appears when not authenticated
- [ ] User name displays when authenticated
- [ ] Logout button works correctly
- [ ] Styling is consistent with Fluent UI theme

---

## Phase 5: Page Components

**Estimated Time: 1-2 hours**

### Step 5.1: Update Pages/Index.razor

**File**: `Pages/Index.razor`

**Current:**
```razor
@page "/"

<PageTitle>Index</PageTitle>

<h1>Welcome to User Sign In ASP.NET Core Blazor WebAssembly</h1>

<AuthorizeView>
  <NotAuthorized>
    <div>This page can be accessed by all users, authenticated or not.</div>
    <p>Click <a href="authentication/login">Log in</a> to sign into the application. Navigating to it while not logged in will automatically initiate the login process. Please note that it doesn't require that your user has any specific application roles assigned, and will access Microsoft Graph on your behalf.</p>
  </NotAuthorized>
  <Authorized>
    <p>Welcome! You are now logged in.</p>
    <p>Visit your <a href="profile">Profile</a> to view your information from Microsoft Graph.</p>
  </Authorized>
</AuthorizeView>
```

**Updated** (Fluent UI):
```razor
@page "/"
@using Microsoft.FluentUI.AspNetCore.Components

<PageTitle>Index</PageTitle>

<FluentStack Orientation="Orientation.Vertical" VerticalGap="24">
    <FluentLabel Typo="Typography.H3">
        Welcome to User Sign In ASP.NET Core Blazor WebAssembly
    </FluentLabel>

    <AuthorizeView>
        <NotAuthorized>
            <FluentCard>
                <FluentStack Orientation="Orientation.Vertical" VerticalGap="16">
                    <FluentLabel Typo="Typography.Body">
                        This page can be accessed by all users, authenticated or not.
                    </FluentLabel>
                    <FluentLabel Typo="Typography.Body">
                        Click <FluentAnchor Href="authentication/login" Appearance="Appearance.Hypertext">Log in</FluentAnchor> to sign into the application. 
                        Navigating to it while not logged in will automatically initiate the login process. 
                        Please note that it doesn't require that your user has any specific application roles assigned, 
                        and will access Microsoft Graph on your behalf.
                    </FluentLabel>
                    <FluentButton Appearance="Appearance.Accent" Href="authentication/login">
                        Log in now
                    </FluentButton>
                </FluentStack>
            </FluentCard>
        </NotAuthorized>
        <Authorized>
            <FluentCard>
                <FluentStack Orientation="Orientation.Vertical" VerticalGap="16">
                    <FluentLabel Typo="Typography.H5">Welcome! You are now logged in.</FluentLabel>
                    <FluentLabel Typo="Typography.Body">
                        Visit your <FluentAnchor Href="profile" Appearance="Appearance.Hypertext">Profile</FluentAnchor> 
                        to view your information from Microsoft Graph.
                    </FluentLabel>
                    <FluentButton Appearance="Appearance.Accent" Href="profile">
                        View Profile
                    </FluentButton>
                </FluentStack>
            </FluentCard>
        </Authorized>
    </AuthorizeView>
</FluentStack>
```

---

### Step 5.2: Update Pages/Profile.razor

**File**: `Pages/Profile.razor`

**Current:**
```razor
@using Microsoft.AspNetCore.Authorization
@using Microsoft.AspNetCore.Components.Authorization
@using Microsoft.AspNetCore.Components.WebAssembly.Authentication
@using System.Text.Json
@inject HttpClient Http
@attribute [Authorize]

@page "/profile"

<PageTitle>Profile</PageTitle>

<h1>Your Profile</h1>

<AuthorizeView>
  <Authorized>
    @if (graphApiResponse != null)
    {
         <p>Below is your user information retrieved from Microsoft Graph's <code>/me</code> API:</p>

         <p><pre><code class="language-js">@JsonSerializer.Serialize(graphApiResponse, new JsonSerializerOptions { WriteIndented = true })</code></pre></p>

         <p>Refreshing this page will continue to use the cached access token acquired for Microsoft Graph, which is valid for future page views and will attempt to refresh this token as it nears its expiration.</p>
    }
    else
    {
         <p>Loading your profile information...</p>
    }
  </Authorized>
</AuthorizeView>

@code {
  [CascadingParameter]
  private Task<AuthenticationState>? AuthenticationStateTask { get; set; }

  private JsonDocument? graphApiResponse = null;

  protected override async Task OnInitializedAsync()
  {
    var authState = await AuthenticationStateTask!;
    var user = authState.User;

    if (user.Identity?.IsAuthenticated == true)
    {
      try
      {
        using var response = await Http.GetAsync("https://graph.microsoft.com/v1.0/me");
        response.EnsureSuccessStatusCode();
        graphApiResponse = await response.Content.ReadFromJsonAsync<JsonDocument>().ConfigureAwait(false);
      }
      catch (AccessTokenNotAvailableException exception)
      {
        exception.Redirect();
      }
    }
  }
}
```

**Updated** (Fluent UI with better presentation):
```razor
@using Microsoft.AspNetCore.Authorization
@using Microsoft.AspNetCore.Components.Authorization
@using Microsoft.AspNetCore.Components.WebAssembly.Authentication
@using Microsoft.FluentUI.AspNetCore.Components
@using System.Text.Json
@inject HttpClient Http
@attribute [Authorize]

@page "/profile"

<PageTitle>Profile</PageTitle>

<FluentStack Orientation="Orientation.Vertical" VerticalGap="24">
    <FluentLabel Typo="Typography.H3">Your Profile</FluentLabel>

    <AuthorizeView>
        <Authorized>
            @if (graphApiResponse != null)
            {
                <FluentCard>
                    <FluentStack Orientation="Orientation.Vertical" VerticalGap="16">
                        <FluentLabel Typo="Typography.H5">
                            User Information from Microsoft Graph
                        </FluentLabel>
                        
                        <FluentLabel Typo="Typography.Body">
                            Below is your user information retrieved from Microsoft Graph's <code>/me</code> API:
                        </FluentLabel>

                        <FluentStack Orientation="Orientation.Vertical" 
                                     Style="background: var(--neutral-layer-2); padding: 1rem; border-radius: 4px; overflow-x: auto;">
                            <pre style="margin: 0;"><code class="language-json">@JsonSerializer.Serialize(graphApiResponse, new JsonSerializerOptions { WriteIndented = true })</code></pre>
                        </FluentStack>

                        <FluentMessageBar Intent="MessageIntent.Info">
                            Refreshing this page will continue to use the cached access token acquired for Microsoft Graph, 
                            which is valid for future page views and will attempt to refresh this token as it nears its expiration.
                        </FluentMessageBar>
                    </FluentStack>
                </FluentCard>
            }
            else
            {
                <FluentCard>
                    <FluentStack Orientation="Orientation.Horizontal" HorizontalGap="16" VerticalAlignment="VerticalAlignment.Center">
                        <FluentProgressRing />
                        <FluentLabel Typo="Typography.Body">Loading your profile information...</FluentLabel>
                    </FluentStack>
                </FluentCard>
            }
        </Authorized>
    </AuthorizeView>
</FluentStack>

@code {
  [CascadingParameter]
  private Task<AuthenticationState>? AuthenticationStateTask { get; set; }

  private JsonDocument? graphApiResponse = null;

  protected override async Task OnInitializedAsync()
  {
    var authState = await AuthenticationStateTask!;
    var user = authState.User;

    if (user.Identity?.IsAuthenticated == true)
    {
      try
      {
        using var response = await Http.GetAsync("https://graph.microsoft.com/v1.0/me");
        response.EnsureSuccessStatusCode();
        graphApiResponse = await response.Content.ReadFromJsonAsync<JsonDocument>().ConfigureAwait(false);
      }
      catch (AccessTokenNotAvailableException exception)
      {
        exception.Redirect();
      }
    }
  }
}
```

---

### Step 5.3: Test Page Components

**Verify:**
- [ ] Index page displays correctly for authenticated and non-authenticated users
- [ ] Profile page loads and displays Graph API data
- [ ] Loading indicators show while fetching data
- [ ] All links work correctly
- [ ] FluentCard components display properly
- [ ] Typography and spacing look good

---

## Phase 6: Cleanup

**Estimated Time: 30 minutes**

### Step 6.1: Remove Bootstrap References

**Optional: Delete Bootstrap folder:**
```bash
rm -rf wwwroot/css/bootstrap/
```

Or keep it if you want to maintain backward compatibility or reference styles.

---

### Step 6.2: Remove Open Iconic References

**Optional: Delete Open Iconic folder:**
```bash
rm -rf wwwroot/css/open-iconic/
```

---

### Step 6.3: Review and Clean app.css

**File**: `wwwroot/css/app.css`

**Remove any remaining Bootstrap-specific styles:**
- `.btn-primary` styles
- Bootstrap grid overrides
- Any `.navbar-*` custom styles

**Keep:**
- Blazor-specific styles (validation, error UI)
- Fluent UI variable usage
- Custom app-specific styles

---

### Step 6.4: Remove Unused CSS Files

**Check these files and remove if empty or no longer needed:**
- `Shared/NavMenu.razor.css` (if using FluentNavMenu)
- Any other component-scoped CSS files that only contained Bootstrap overrides

---

## Phase 7: Testing & Verification

**Estimated Time: 1 hour**

### Comprehensive Testing Checklist

#### Functionality Testing
- [ ] **Build**: Application builds without errors
- [ ] **Run**: Application starts and loads correctly
- [ ] **Navigation**: All navigation links work
- [ ] **Authentication**: Login flow works
- [ ] **Authentication**: Logout flow works
- [ ] **Profile**: Profile link only visible when logged in
- [ ] **Profile**: Microsoft Graph API data loads correctly
- [ ] **Home**: Both authenticated and non-authenticated views work

#### Visual Testing
- [ ] **Layout**: Overall layout looks correct
- [ ] **Navigation**: Navigation menu displays properly
- [ ] **Icons**: All icons display (no missing Open Iconic icons)
- [ ] **Typography**: Text styling is consistent
- [ ] **Colors**: Theme colors are applied
- [ ] **Cards**: FluentCard components render correctly
- [ ] **Buttons**: FluentButton components work and look good
- [ ] **Spacing**: Component spacing is appropriate

#### Responsive Testing
- [ ] **Desktop**: Layout works on desktop (>1024px)
- [ ] **Tablet**: Layout works on tablet (641-1023px)
- [ ] **Mobile**: Layout works on mobile (<640px)
- [ ] **Navigation**: Mobile navigation works (collapsible menu if applicable)

#### Browser Testing
- [ ] **Chrome**: Works in Chrome
- [ ] **Firefox**: Works in Firefox
- [ ] **Edge**: Works in Edge
- [ ] **Safari**: Works in Safari (if available)

#### Console/Error Testing
- [ ] **No console errors**: No JavaScript errors in browser console
- [ ] **No 404s**: No missing resource errors (CSS, JS, fonts)
- [ ] **No warnings**: No framework warnings about components

---

## Appendix A: Component Quick Reference

### Bootstrap → Fluent UI Component Mapping

| Bootstrap Class/Component | Fluent UI Component | Example |
|---------------------------|---------------------|---------|
| `<button class="btn btn-primary">` | `<FluentButton Appearance="Appearance.Accent">` | Primary action button |
| `<button class="btn btn-secondary">` | `<FluentButton Appearance="Appearance.Neutral">` | Secondary button |
| `<button class="btn btn-link">` | `<FluentButton Appearance="Appearance.Lightweight">` | Text button |
| `<a href="...">` | `<FluentAnchor Href="...">` | Link |
| `<div class="card">` | `<FluentCard>` | Card container |
| `<nav class="navbar">` | `<FluentNavMenu>` | Navigation menu |
| `<a class="nav-link">` | `<FluentNavLink>` | Navigation link |
| `<div class="alert alert-info">` | `<FluentMessageBar Intent="MessageIntent.Info">` | Info message |
| `<div class="alert alert-danger">` | `<FluentMessageBar Intent="MessageIntent.Error">` | Error message |
| `<div class="alert alert-warning">` | `<FluentMessageBar Intent="MessageIntent.Warning">` | Warning message |
| `<div class="alert alert-success">` | `<FluentMessageBar Intent="MessageIntent.Success">` | Success message |
| `<div class="spinner-border">` | `<FluentProgressRing>` | Loading spinner |
| `<div class="container">` | `<FluentStack>` | Container/layout |
| `<div class="row">` | `<FluentStack Orientation="Horizontal">` | Horizontal layout |
| `<div class="col">` | `<FluentStack>` (with width) | Column |
| `<input type="text" class="form-control">` | `<FluentTextField>` | Text input |
| `<textarea class="form-control">` | `<FluentTextArea>` | Multi-line input |
| `<select class="form-select">` | `<FluentSelect>` | Dropdown |
| `<input type="checkbox" class="form-check-input">` | `<FluentCheckbox>` | Checkbox |
| `<input type="radio" class="form-check-input">` | `<FluentRadio>` | Radio button |

---

## Appendix B: Fluent UI Button Appearances

```razor
<!-- Primary action -->
<FluentButton Appearance="Appearance.Accent">Save</FluentButton>

<!-- Secondary action -->
<FluentButton Appearance="Appearance.Neutral">Cancel</FluentButton>

<!-- Tertiary/text button -->
<FluentButton Appearance="Appearance.Lightweight">Learn More</FluentButton>

<!-- Outlined button -->
<FluentButton Appearance="Appearance.Outline">Details</FluentButton>

<!-- Stealth (minimal) button -->
<FluentButton Appearance="Appearance.Stealth">Close</FluentButton>
```

---

## Appendix C: Fluent UI Layout Components

### FluentStack (Flexbox Container)
```razor
<!-- Vertical stack -->
<FluentStack Orientation="Orientation.Vertical" VerticalGap="16">
    <div>Item 1</div>
    <div>Item 2</div>
</FluentStack>

<!-- Horizontal stack -->
<FluentStack Orientation="Orientation.Horizontal" HorizontalGap="16">
    <div>Item 1</div>
    <div>Item 2</div>
</FluentStack>

<!-- Stack with alignment -->
<FluentStack Orientation="Orientation.Horizontal" 
             HorizontalAlignment="HorizontalAlignment.Center"
             VerticalAlignment="VerticalAlignment.Center">
    <div>Centered Item</div>
</FluentStack>
```

### FluentGrid (CSS Grid Container)
```razor
<FluentGrid>
    <FluentGridItem xs="12" sm="6" md="4">
        <div>Column 1</div>
    </FluentGridItem>
    <FluentGridItem xs="12" sm="6" md="4">
        <div>Column 2</div>
    </FluentGridItem>
    <FluentGridItem xs="12" sm="12" md="4">
        <div>Column 3</div>
    </FluentGridItem>
</FluentGrid>
```

---

## Appendix D: Common Fluent Icons

### Navigation Icons
```razor
@using Microsoft.FluentUI.AspNetCore.Components

<FluentIcon Value="@(new Icons.Regular.Size20.Home())" />
<FluentIcon Value="@(new Icons.Regular.Size20.Person())" />
<FluentIcon Value="@(new Icons.Regular.Size20.Settings())" />
<FluentIcon Value="@(new Icons.Regular.Size20.Search())" />
<FluentIcon Value="@(new Icons.Regular.Size20.Navigation())" />
```

### Action Icons
```razor
<FluentIcon Value="@(new Icons.Regular.Size20.Add())" />
<FluentIcon Value="@(new Icons.Regular.Size20.Edit())" />
<FluentIcon Value="@(new Icons.Regular.Size20.Delete())" />
<FluentIcon Value="@(new Icons.Regular.Size20.Save())" />
<FluentIcon Value="@(new Icons.Regular.Size20.Dismiss())" />
```

### Status Icons
```razor
<FluentIcon Value="@(new Icons.Regular.Size20.CheckmarkCircle())" />
<FluentIcon Value="@(new Icons.Regular.Size20.ErrorCircle())" />
<FluentIcon Value="@(new Icons.Regular.Size20.Warning())" />
<FluentIcon Value="@(new Icons.Regular.Size20.Info())" />
```

**Browse all icons:** https://aka.ms/fluentui-system-icons

---

## Appendix E: Fluent UI CSS Variables

### Common CSS Variables for Theming

```css
/* Colors */
var(--accent-fill-rest)                 /* Primary accent color */
var(--neutral-fill-rest)                /* Neutral fill color */
var(--neutral-foreground-rest)          /* Text color */
var(--neutral-layer-1)                  /* Background layer 1 */
var(--neutral-layer-2)                  /* Background layer 2 */
var(--neutral-layer-3)                  /* Background layer 3 */
var(--neutral-stroke-rest)              /* Border color */

/* Typography */
var(--body-font)                        /* Body font family */
var(--type-ramp-base-font-size)         /* Base font size */
var(--type-ramp-base-line-height)       /* Base line height */

/* Status Colors */
var(--error)                            /* Error color */
var(--success)                          /* Success color */
var(--warning)                          /* Warning color */

/* Spacing */
var(--design-unit)                      /* Base spacing unit (4px) */
```

---

## Appendix F: Troubleshooting

### Issue: Icons not displaying
**Solution:** Ensure you've added the Fluent UI JavaScript module to index.html:
```html
<script src="_content/Microsoft.FluentUI.AspNetCore.Components/Microsoft.FluentUI.AspNetCore.Components.lib.module.js" type="module" async></script>
```

### Issue: Components not styling correctly
**Solution:** Verify Fluent UI CSS is loaded in index.html:
```html
<link href="_content/Microsoft.FluentUI.AspNetCore.Components/css/reboot.css" rel="stylesheet" />
<link href="_content/Microsoft.FluentUI.AspNetCore.Components/css/fluent.css" rel="stylesheet" />
```

### Issue: Build errors about missing components
**Solution:** Run `dotnet restore` after adding the Fluent UI package.

### Issue: CSS conflicts between Bootstrap and Fluent UI
**Solution:** Remove Bootstrap CSS reference from index.html and any Bootstrap classes from components.

### Issue: Dark mode not working
**Solution:** Add design theme configuration in App.razor:
```razor
<FluentDesignTheme Mode="DesignThemeModes.System" />
```

### Issue: Navigation not responsive on mobile
**Solution:** Ensure FluentNavMenu has `Collapsible="true"` attribute.

---

## Appendix G: Next Steps After Migration

### Optional Enhancements

1. **Add Dark Mode Toggle**
   - Implement theme switcher using FluentDesignTheme
   - Add toggle button in header

2. **Improve Profile Page**
   - Parse Graph API response into structured data
   - Display user info in cards/panels
   - Add user avatar using Graph photo endpoint

3. **Add More Pages**
   - Use Fluent UI components for forms
   - Implement data tables with FluentDataGrid
   - Add dialogs and modals

4. **Enhance Navigation**
   - Add sub-navigation groups
   - Implement breadcrumbs
   - Add search functionality

5. **Performance Optimization**
   - Enable virtualization for long lists
   - Optimize icon usage
   - Consider lazy loading for large components

---

## Summary

This migration plan provides a comprehensive, step-by-step approach to converting your Blazor WebAssembly application from Bootstrap 5.1.0 to Microsoft Fluent UI Blazor. 

### Key Phases:
1. ✅ Installation & Setup (30-45 min)
2. ✅ Layout Components (1-2 hours)
3. ✅ Navigation Menu (1-2 hours)
4. ✅ Authentication UI (30-45 min)
5. ✅ Page Components (1-2 hours)
6. ✅ Cleanup (30 min)
7. ✅ Testing & Verification (1 hour)

**Total Time: 4-7 hours**

You can now work through these phases incrementally, requesting specific tasks as you're ready to proceed.

---

## References

- [Microsoft Fluent UI Blazor Documentation](https://www.fluentui-blazor.net/)
- [Fluent UI System Icons](https://aka.ms/fluentui-system-icons)
- [Component Examples](https://www.fluentui-blazor.net/Components)
- [GitHub Repository](https://github.com/microsoft/fluentui-blazor)

---

**Document Version:** 1.0  
**Last Updated:** 2025-11-20  
**Project:** Consultologist Blazor WebAssembly  

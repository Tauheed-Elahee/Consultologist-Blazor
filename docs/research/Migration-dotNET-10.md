# .NET 8 to .NET 10 LTS Migration Plan
## Blazor WebAssembly - Consultologist Application

---

## Migration Summary

**Risk Level**: LOW-MEDIUM  
**Estimated Time**: 2-3 hours including testing  
**Breaking Changes**: None identified that affect this project  
**Code Changes Required**: None (configuration only)

Your project is well-positioned for this upgrade:
- ✓ .NET 10.0.100 SDK already installed
- ✓ Clean Git working directory
- ✓ Simple standalone architecture
- ✓ Modern package versions already compatible

---

## Pre-Migration Steps

### 1. Create Backups
```bash
# Create Git tag for rollback point
git tag -a pre-net10-migration -m "Backup before .NET 10 migration"
git push origin pre-net10-migration

# Create backup branch
git checkout -b backup/net8-stable
git push -u origin backup/net8-stable
git checkout main

# Document current state
dotnet list package > packages-net8.txt
```

### 2. Verify Environment
```bash
dotnet --version  # Should show 10.0.100
dotnet workload list  # Verify wasm-tools available
```

---

## Migration Steps

### PHASE 1: Update Project File

**File**: `/home/thegreat/Projects/GitHub/Consultologist-Blazor/BlazorWasm.csproj`

**Change 1 - Target Framework:**
```xml
<!-- UPDATE -->
<TargetFramework>net8.0</TargetFramework>
<!-- TO -->
<TargetFramework>net10.0</TargetFramework>
```

**Change 2 - Package References (Update all 8.* to 10.*):**
```xml
<!-- UPDATE THESE FOUR PACKAGES FROM 8.* to 10.* -->
<PackageReference Include="Microsoft.AspNetCore.Components.WebAssembly" Version="10.*" />
<PackageReference Include="Microsoft.AspNetCore.Components.WebAssembly.DevServer" Version="10.*" PrivateAssets="all" />
<PackageReference Include="Microsoft.Authentication.WebAssembly.Msal" Version="10.*" />
<PackageReference Include="Microsoft.Extensions.Http" Version="10.*" />

<!-- LEAVE FLUENT UI UNCHANGED (already compatible) -->
<PackageReference Include="Microsoft.FluentUI.AspNetCore.Components" Version="4.13.1" />
<PackageReference Include="Microsoft.FluentUI.AspNetCore.Components.Icons" Version="4.13.1" />
```

**Restore packages:**
```bash
dotnet clean
dotnet restore
```

### PHASE 2: Update CI/CD Configuration

**File**: `/.github/workflows/azure-static-web-apps-gentle-desert-09697700f.yml`

**Update .NET SDK version (around line 23):**
```yaml
# UPDATE
- name: Setup .NET
  uses: actions/setup-dotnet@v3
  with:
    dotnet-version: "8.0.x"

# TO
- name: Setup .NET
  uses: actions/setup-dotnet@v3
  with:
    dotnet-version: "10.0.x"
```

### PHASE 3: Update Documentation

**File**: `/README.md`

Update all references:
1. Title: `ASP.NET Core 8.0` → `ASP.NET Core 10.0`
2. Throughout document: Change "8.0" to "10.0"

**Optional**: Update `.vscode/tasks.json` if Api project is used:
```json
// Line 62: Update path from net8.0 to net10.0
"cwd": "${workspaceFolder}/Api/bin/Debug/net10.0"
```

### PHASE 4: Code Verification

**NO CODE CHANGES REQUIRED** - All source files are compatible:
- ✓ Program.cs - MSAL authentication pattern unchanged
- ✓ Authentication.razor - RemoteAuthenticatorView still supported
- ✓ LoginDisplay.razor - Navigation methods unchanged
- ✓ Profile.razor - Exception handling unchanged
- ✓ All other .razor files - No breaking changes

---

## Testing Plan

### Local Testing
```bash
# Build and run
dotnet build -c Debug
dotnet run
```

**Test Checklist:**
- [ ] Application starts without errors
- [ ] Home page loads with Fluent UI components
- [ ] Login button appears
- [ ] Authentication flow completes successfully
- [ ] User name displays in header
- [ ] Navigate to `/profile` page
- [ ] Microsoft Graph data loads
- [ ] Logout works correctly
- [ ] No console errors

**Production build test:**
```bash
dotnet publish -c Release -o output
```

### CI/CD Testing
1. Create feature branch:
```bash
git checkout -b feature/net10-migration
git add BlazorWasm.csproj .github/workflows/*.yml README.md
git commit -m "Migrate to .NET 10.0 LTS

- Update target framework to net10.0
- Update Microsoft packages to 10.*
- Update GitHub Actions to .NET 10.0.x
- Update documentation
- Fluent UI remains at 4.13.1 (compatible)"

git push -u origin feature/net10-migration
```

2. Create Pull Request and verify:
   - GitHub Actions build succeeds
   - Azure Static Web Apps staging deployment works
   - Test staging URL thoroughly

3. After approval:
```bash
git checkout main
git merge feature/net10-migration
git push origin main
git tag -a v2.0.0-net10 -m "Production release on .NET 10.0 LTS"
git push origin v2.0.0-net10
```

---

## Breaking Changes Assessment

**No breaking changes affect this project**. Verified against official .NET 10 breaking changes:
- ✓ Blazor boot config changes - not affected
- ✓ Environment config changes - not affected
- ✓ Cache boot resources removal - not affected
- ✓ Cookie auth API changes - not affected (using MSAL/JWT)
- ✓ Navigation exception handling - not affected

---

## Rollback Plan

### During Development
```bash
git reset --hard pre-net10-migration
```
**Recovery Time**: 1 minute

### After PR Merge
```bash
# Option A: Git revert (preferred)
git revert HEAD --no-edit
git push origin main

# Option B: Azure Portal
# Static Web Apps → Deployments → Previous version → "Activate"
```
**Recovery Time**: 5-10 minutes

---

## Critical Files for Implementation

1. **BlazorWasm.csproj** - Update TargetFramework and packages (CRITICAL)
2. **azure-static-web-apps-gentle-desert-09697700f.yml** - Update CI/CD (HIGH)
3. **README.md** - Update documentation (LOW)
4. **Program.cs** - Verify MSAL compatibility (verification only)
5. **Authentication.razor** - Test authentication flow (verification only)

---

## Answer to User Question

**Do you need to upgrade ASP.NET references?**

**YES - Absolutely required.** The ASP.NET Core version must match your .NET version:
- .NET 8 → ASP.NET Core 8.x packages
- .NET 10 → ASP.NET Core 10.x packages

All four Microsoft packages need updating:
1. Microsoft.AspNetCore.Components.WebAssembly: 8.* → 10.*
2. Microsoft.AspNetCore.Components.WebAssembly.DevServer: 8.* → 10.*
3. Microsoft.Authentication.WebAssembly.Msal: 8.* → 10.*
4. Microsoft.Extensions.Http: 8.* → 10.*

The Fluent UI packages (4.13.1) do NOT need updating - they are compatible with .NET 10.

---

## Troubleshooting

**Package Restore Fails:**
```bash
dotnet nuget locals all --clear
dotnet restore --force
```

**MSAL Authentication Loop:**
1. Clear browser cache and cookies
2. Verify Azure AD redirect URIs
3. Check appsettings.json Authority and ClientId

**GitHub Actions Fails:**
- Update to `setup-dotnet@v4` if needed
- Use explicit version: `dotnet-version: "10.0.100"`

---

## Timeline

- Backup and prep: 15 min
- Update project files: 15 min
- Update CI/CD: 10 min
- Update docs: 10 min
- Local testing: 30 min
- PR and staging test: 20 min
- Production deploy: 10 min
- Verification: 30 min

**Total: ~2.5 hours (3 hours with buffer)**

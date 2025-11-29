# Azure AI Foundry API Endpoint Investigation Findings

## Date: 2025-11-28

## Problem Statement

Azure Function `AgentProxy.cs` receives 400 Bad Request error when attempting to create a run:

```
Invalid 'assistant_id': 'consultologist-canada-east-ai:7'.
Expected an ID that contains letters, numbers, underscores, or dashes,
but this value contained additional characters.
```

## Investigation Process

### Initial Hypothesis (INCORRECT)
Initially suspected the agent ID format was wrong because the error message complained about the colon `:` character in `consultologist-canada-east-ai:7`.

### Key Discovery
Obtained the actual agent YAML from Azure AI Foundry Portal which shows:

```yaml
id: consultologist-canada-east-ai:7
name: consultologist-canada-east-ai
version: "7"
```

This **confirms** that `consultologist-canada-east-ai:7` IS the correct internal agent identifier format in Azure AI Foundry, using the pattern `{agent-name}:{version}`.

### Root Cause Analysis

The issue stems from a **mismatch between Azure AI Foundry's internal agent versioning system and the OpenAI-compatible REST API**:

1. **Azure AI Foundry Internal Format**: Uses versioned IDs like `consultologist-canada-east-ai:7`
2. **REST API Expected Format**: Follows OpenAI Assistants API convention with `asst_{alphanumeric}` format (no colons allowed)

### API Documentation Research

From Microsoft documentation ([Azure AI Foundry Agent Service REST API](https://learn.microsoft.com/en-us/rest/api/aifoundry/aiagents/)):

- The GA API version is `2025-05-01`
- The REST API follows OpenAI Assistants API protocol
- The `assistant_id` field in run creation expects OpenAI-compatible format
- Valid characters: letters, numbers, underscores, dashes
- Invalid characters: colons, semicolons, other special characters

### Current Implementation

**Endpoint**:
```
https://consultologist-canada-east-resou.services.ai.azure.com/api/projects/consultologist-canada-east
```

**API Call Flow** (from `Api/AgentProxy.cs`):
1. Create thread: `POST /threads`
2. Add message: `POST /threads/{threadId}/messages`
3. Create run: `POST /threads/{threadId}/runs` with `assistant_id` field ← **Error occurs here**
4. Poll status: `GET /threads/{threadId}/runs/{runId}`
5. Get messages: `GET /threads/{threadId}/messages`

**Run creation payload**:
```csharp
var runPayload = new
{
    assistant_id = agentId  // Currently: "consultologist-canada-east-ai:7"
};
```

## Solution Options

### Option 1: Use Base Agent Name (Quick Test)

**Hypothesis**: The API may accept the agent name without the version suffix.

**Implementation**:
```json
"AzureAI__AgentId": "consultologist-canada-east-ai"
```

**Pros**:
- Simple one-line config change
- Quick to test
- May automatically use latest version

**Cons**:
- Not confirmed to work
- May not support version pinning

### Option 2: Find REST API Assistant ID

**Hypothesis**: The agent has a separate `asst_` prefixed ID for REST API usage.

**Implementation**:
Use API to list assistants:
```bash
curl -X GET \
  "https://consultologist-canada-east-resou.services.ai.azure.com/api/projects/consultologist-canada-east/assistants?api-version=2025-05-01" \
  -H "Authorization: Bearer {token}"
```

Or check Azure AI Foundry Portal for an "Assistant ID" field separate from the versioned agent ID.

**Pros**:
- Follows documented API format
- Guaranteed to work if ID exists

**Cons**:
- Requires finding the correct ID
- May require additional Azure portal navigation

### Option 3: Use Azure AI Foundry SDK

**Hypothesis**: The official SDK handles versioned agents properly.

**Implementation**:
Replace REST API calls with Azure AI Foundry Agent SDK which abstracts the versioning complexity.

**Pros**:
- Proper handling of versioned agents
- Official Microsoft support
- Type-safe API

**Cons**:
- Requires code refactoring
- Additional NuGet package dependency
- More complex than config change

## Recommended Approach

**Step 1**: Try Option 1 (base agent name without version)
- Fastest to test
- Minimal risk
- Easy rollback

**Step 2**: If Step 1 fails, use Option 2 (find asst_ ID)
- Query the assistants endpoint
- Check Azure Portal for alternative ID field
- Update configuration with correct ID

**Step 3**: If both fail, consider Option 3 (SDK migration)
- Longer-term solution
- Better architecture for production

## Files to Modify

### Configuration Files
1. **`/Api/local.settings.json`** - Local development config
   ```json
   "AzureAI__AgentId": "{CORRECT_ID}"
   ```

2. **`/.env`** - Repository environment variables
   ```
   AZURE_EXISTING_AGENT_ID="{CORRECT_ID}"
   ```

3. **Azure Portal** - Static Web App → Configuration → Application Settings
   ```
   AzureAI__AgentId = {CORRECT_ID}
   ```

### Documentation
4. **`/Research/Migration-Azure-Foundry.md`** - Update with correct ID format

### Code (No changes needed for Options 1 & 2)
- **`/Api/AgentProxy.cs`** is already correct - it reads from configuration

## Testing Plan

### Local Testing
1. Update `Api/local.settings.json` with test ID
2. Run: `func start` in `/Api` directory
3. Send test request to `http://localhost:7071/api/AgentProxy`
4. Check logs for:
   - Successful thread creation (200)
   - Successful message addition (200)
   - Successful run creation (200) ← Key test point
   - Run status polling completes
   - Messages retrieved successfully

### Production Testing
1. Update Azure Static Web App configuration
2. Deploy via Git push
3. Test from Blazor UI
4. Monitor Azure Function logs for errors

## Expected Outcome

After using the correct agent ID format:
- ✅ Run creation returns 200 OK
- ✅ Run status polling succeeds
- ✅ Agent processes the request
- ✅ Response JSON is retrieved
- ✅ End-to-end workflow completes

## References

- [Azure AI Foundry Agent Service REST API](https://learn.microsoft.com/en-us/rest/api/aifoundry/aiagents/)
- [Threads, Runs, and Messages Concepts](https://learn.microsoft.com/en-us/azure/ai-foundry/agents/concepts/threads-runs-messages?view=foundry-classic)
- [Quickstart - Azure AI Foundry Agent Service](https://learn.microsoft.com/en-us/azure/ai-foundry/agents/quickstart?view=foundry-classic)
- [Azure AI Foundry Agents Documentation](https://learn.microsoft.com/en-us/agent-framework/user-guide/agents/agent-types/azure-ai-foundry-agent)

## Resolution

### Tests Performed

**Test 1: Base Agent Name (Failed)**
- Tried: `consultologist-canada-east-ai` (without version suffix)
- Result: `Invalid 'assistant_id': 'consultologist-canada-east-ai'. Expected an ID that begins with 'asst'.`
- Conclusion: API requires the `asst_` prefix format

**Test 2: Query Assistants Endpoint (Success)**

**Step 2.1: Get Azure AD Access Token**

The Azure AI Foundry API requires an Azure AD token with the `https://ai.azure.com` scope.

```bash
# Get access token using Azure CLI
az account get-access-token --resource https://ai.azure.com --query accessToken -o tsv
```

**Note**: Initially tried `https://cognitiveservices.azure.com` scope, which resulted in 401 Unauthorized:
```
{ "statusCode": 401, "message": "Unauthorized. Access token is missing, invalid, audience is incorrect (https://ai.azure.com), or have expired." }
```

The error message explicitly states the required audience is `https://ai.azure.com`, not `https://cognitiveservices.azure.com`.

**Step 2.2: Query Assistants Endpoint**

```bash
# Store token in variable
TOKEN=$(az account get-access-token --resource https://ai.azure.com --query accessToken -o tsv)

# Query the assistants endpoint
curl -s -X GET \
  "https://consultologist-canada-east-resou.services.ai.azure.com/api/projects/consultologist-canada-east/assistants?api-version=2025-05-01" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json"
```

**API Response**:
```json
{
  "object": "list",
  "data": [
    {
      "id": "asst_tx4wK44h3Q4fLrMv3vZAhDy3",
      "object": "assistant",
      "created_at": 1763749671,
      "name": "Agent441",
      "description": "",
      "model": "gpt-4.1",
      "instructions": "You are Consultologist, an AI assistant...",
      "tools": [],
      "top_p": 1.0,
      "temperature": 0.01,
      "tool_resources": {},
      "metadata": {},
      "response_format": "auto"
    }
  ],
  "first_id": "asst_tx4wK44h3Q4fLrMv3vZAhDy3",
  "last_id": "asst_tx4wK44h3Q4fLrMv3vZAhDy3",
  "has_more": false
}
```

**Found**: `asst_tx4wK44h3Q4fLrMv3vZAhDy3`

**Key Observation**: The assistant name shown in the API response is "Agent441", but this corresponds to the Azure AI Foundry agent `consultologist-canada-east-ai:7`. The `asst_` prefixed ID is the OpenAI-compatible identifier needed for the REST API.

**Result**: ✅ SUCCESS - Complete workflow executed properly with this ID

### Correct Assistant ID

**Format**: `asst_tx4wK44h3Q4fLrMv3vZAhDy3`

This is the OpenAI-compatible assistant ID that maps to the Azure AI Foundry agent `consultologist-canada-east-ai:7`.

### Key Learning

Azure AI Foundry uses **two different ID formats**:

1. **Internal/Portal Format**: `consultologist-canada-east-ai:7` (versioned, with colon)
   - Used in Azure AI Foundry Portal YAML
   - Used for agent management/deployment

2. **REST API Format**: `asst_tx4wK44h3Q4fLrMv3vZAhDy3` (OpenAI-compatible)
   - Required for OpenAI Assistants API protocol
   - Used in `assistant_id` field for run creation
   - No colons, starts with `asst_` prefix

### Test Results

```
HTTP Status: 200
Response: {"response":"{\"test\": \"...\"}", "error":null, "success":true}

Workflow executed successfully:
✅ Thread creation
✅ Message addition
✅ Run creation (with correct asst_ ID)
✅ Run status polling (completed)
✅ Message retrieval
✅ JSON response returned
```

## Next Actions

1. ✅ Document findings (this file)
2. ✅ Test Option 1: Base agent name without version (Failed)
3. ✅ Query assistants endpoint to find correct ID (Success)
4. ✅ Update all configuration files
5. ✅ Test locally (Success - 200 OK)
6. ⏳ Update Azure Static Web App configuration (Production)
7. ⏳ Deploy to production
8. ⏳ Verify end-to-end functionality from Blazor UI

## Production Deployment Checklist

- [ ] Update Azure Static Web App Configuration
  - Navigate to: Azure Portal → Static Web App → Configuration
  - Update: `AzureAI__AgentId = asst_tx4wK44h3Q4fLrMv3vZAhDy3`

- [ ] Commit and push changes to Git
  - Files modified: `Api/local.settings.json`, `.env`
  - Trigger GitHub Actions deployment

- [ ] Test from Blazor UI
  - Load application
  - Submit consult draft
  - Verify JSON response

- [ ] Monitor Azure Function logs
  - Check for any errors in production
  - Verify complete workflow execution






The Problem
Your Azure Function was returning a 400 error: `Invalid 'assistant_id': 'consultologist-canada-east-ai:7'` because the colon character wasn't allowed.

### The Root Cause
Azure AI Foundry uses **two different ID formats**:

1. **Internal/Portal ID**: `consultologist-canada-east-ai:7` (versioned, with colon)
   - This is what you see in the Azure AI Foundry Portal YAML
   - Used for agent management and deployment

2. **REST API ID**: `asst_tx4wK44h3Q4fLrMv3vZAhDy3` (OpenAI-compatible)
   - This is what the REST API requires
   - Must start with `asst_` prefix, no colons

### The Solution
I queried the Azure API to find the correct assistant ID:

**Correct Assistant ID**: `asst_tx4wK44h3Q4fLrMv3vZAhDy3`

### Files Updated
1. ✅ `Api/local.settings.json` - Updated with correct assistant ID
2. ✅ `.env` - Updated with correct assistant ID
3. ✅ `Research/Findings-AI-Endpoint.md` - Created comprehensive investigation report
4. ✅ `Research/Migration-Azure-Foundry.md` - Added resolution notice

### Test Results
✅ **Successfully tested locally** - HTTP 200, complete workflow executed:
- Thread creation ✅
- Message addition ✅
- Run creation ✅
- Status polling ✅
- Message retrieval ✅
- JSON response returned ✅

### Next Steps for Production

**You need to update Azure Portal configuration:**

1. Go to Azure Portal → Your Static Web App → Configuration
2. Update the application setting:
   ```
   AzureAI__AgentId = asst_tx4wK44h3Q4fLrMv3vZAhDy3
   ```

3. Then commit and push your changes:
   ```bash
   git add Api/local.settings.json .env Research/
   git commit -m "Fix: Update agent ID to OpenAI-compatible format"
   git push
   ```

4. Test from your Blazor UI to verify everything works end-to-end!

The local configuration is now working perfectly. Once you update the Azure Portal settings and deploy, your production environment will work too! 🚀

Perfect! I've updated the findings document with detailed commands showing exactly how to retrieve the assistant ID. The documentation now includes:

1. **Step 2.1: Get Azure AD Access Token**
   - Command to get the token using Azure CLI
   - Important note about using the correct scope (`https://ai.azure.com` instead of `https://cognitiveservices.azure.com`)
   - The actual error message that helped identify the correct scope

2. **Step 2.2: Query Assistants Endpoint**
   - Complete curl command with the token
   - Full API response showing the assistant details
   - Key observation that "Agent441" (the name in the API response) corresponds to the Azure AI Foundry agent `consultologist-canada-east-ai:7`

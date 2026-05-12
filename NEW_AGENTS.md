# New Azure AI Foundry Agents

## Current App Integration

The Blazor app does not call Azure AI Foundry directly. It calls the configured Azure Function proxy:

```text
AzureFunction:AgentProxyUrl
```

The function is implemented in `Api/AgentProxy.cs`.

The function uses the Azure AI Foundry project Responses API:

```text
POST /openai/v1/responses
```

It selects the agent with:

```json
{
  "agent_reference": {
    "name": "<AzureAI__AgentName>",
    "version": "<AzureAI__AgentVersion>",
    "type": "agent_reference"
  }
}
```

The app no longer uses an OpenAI-compatible `asst_...` assistant ID.

## New Foundry Agent Model

The new Azure AI Foundry agent model is different from the older Assistants-style API.

The newer model is centered around:

- Agents identified by name and version, not primarily by `asst_...`.
- Responses created through `/openai/v1/responses`.
- Optional Conversations for durable multi-turn state.
- `agent_reference` instead of `assistant_id`.

In the new model, the Foundry UI may not expose the old `asst_...` assistant ID. If the UI only shows an agent name, version, GUID, or a value like `agent-name:version`, that value is not compatible with the current `assistant_id` code path.

Useful resources:

- Runtime components: https://learn.microsoft.com/en-us/azure/foundry/agents/concepts/runtime-components
- Foundry quickstart: https://learn.microsoft.com/en-us/azure/foundry/quickstarts/get-started-code
- Agent memory and user scope: https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/memory-usage
- Foundry REST reference: https://learn.microsoft.com/en-us/rest/api/aifoundry/aiproject
- Microsoft Q&A on missing `asst_...` IDs in the new UI: https://learn.microsoft.com/en-us/answers/questions/5783028/where-to-find-microsoft-foundry-agent-id-to-put-as

## New REST API Shape

The new REST path is a single response creation call instead of thread/message/run polling.

Example:

```bash
curl -X POST "${ENDPOINT}/openai/v1/responses" \
  -H "Authorization: Bearer ${ACCESS_TOKEN}" \
  -H "Content-Type: application/json" \
  -d '{
    "input": "Your prompt here",
    "agent_reference": {
      "name": "your_agent_name",
      "version": "your_agent_version",
      "type": "agent_reference"
    },
    "store": false
  }'
```

For multi-turn state, create or reuse a conversation:

```bash
curl -X POST "${ENDPOINT}/openai/v1/conversations" \
  -H "Authorization: Bearer ${ACCESS_TOKEN}" \
  -H "Content-Type: application/json" \
  -d '{}'
```

Then pass the conversation ID to the responses call:

```json
{
  "conversation": "conv_...",
  "input": "Next user message",
  "agent_reference": {
    "name": "your_agent_name",
    "version": "your_agent_version",
    "type": "agent_reference"
  }
}
```

For this consult app, `store: false` is likely preferable unless cross-section memory is intentionally needed. Each generated consult section should generally be independent, using only the submitted consult draft and the selected section standard.

## Function Configuration

Required Function App settings:

```text
AzureAI__Endpoint=https://<resource>.services.ai.azure.com/api/projects/<project>
AzureAI__AgentName=<foundry-agent-name>
AzureAI__AgentVersion=<foundry-agent-version>
AzureAI__ApiVersion=v1
```

`AzureAI__ApiVersion` defaults to `v1` when omitted.

The function still uses `DefaultAzureCredential` and requests a token for:

```text
https://ai.azure.com/.default
```

The Function App managed identity must have access to the target Foundry/Azure AI resource or project.

## Request Shape

`Api/AgentProxy.cs` builds the same consult-section prompt used by the previous implementation and sends it as `input`:

```json
{
  "input": "<consult section prompt>",
  "agent_reference": {
    "name": "<AzureAI__AgentName>",
    "version": "<AzureAI__AgentVersion>",
    "type": "agent_reference"
  },
  "store": false
}
```

The proxy returns the existing Blazor contract:

```json
{
  "response": "<generated section text>",
  "error": null,
  "success": true
}
```

Response parsing first checks top-level `output_text`, then falls back to the first text content item in `output[]` entries with type `message` or `output_message`.

The proxy requires `AzureAI__AgentVersion` so deployments are explicit. Because the current Foundry runtime examples show `agent_reference` with only `name` and `type`, the proxy retries once without `agent_reference.version` if the versioned request returns `400 BadRequest`.

## REST Versus C# SDK

REST and the C# SDK call the same underlying Foundry service. The difference is how much plumbing the app owns.

### REST Path

With REST, `Api/AgentProxy.cs` manually builds HTTP calls.

The code owns:

- Request URLs.
- Bearer token headers.
- JSON body shape.
- Response JSON parsing.
- Error handling.
- Retry behavior.
- API path/version updates.

Advantages:

- Smallest change from the current code.
- Easy to inspect the exact payload being sent.
- No dependency on new or preview SDK package shapes.
- Easy to reproduce with `curl`.

Tradeoffs:

- Manual response parsing.
- More brittle if response schemas change.
- The app must track API shape changes directly.
- Less discoverable than typed SDK clients.

For this repo, REST is the pragmatic first migration because `Api/AgentProxy.cs` already uses `HttpClient`, and the app only needs a simple prompt-in/text-out call for each section.

### C# SDK Path

With the C# SDK, the function uses Azure SDK clients and typed models instead of hand-written URLs and raw JSON.

Typical packages include:

```bash
dotnet add package Azure.AI.Projects
dotnet add package Azure.AI.Projects.Agents
dotnet add package Azure.AI.Extensions.OpenAI
dotnet add package Azure.Identity
```

Example shape:

```csharp
AIProjectClient projectClient = new(
    endpoint: new Uri(projectEndpoint),
    tokenProvider: new DefaultAzureCredential());

ProjectResponsesClient responsesClient =
    projectClient.ProjectOpenAIClient.GetProjectResponsesClientForAgent(agentName);

ResponseResult response = await responsesClient.CreateResponseAsync(prompt);
var text = response.GetOutputText();
```

If the SDK exposes an overload or options type for agent version selection, pass `agentVersion` there. If it does not, keep the version in app configuration and confirm whether the SDK resolves the active/default version by agent name.

Advantages:

- Typed clients and models.
- Less manual JSON parsing.
- SDK handles more request construction details.
- Cleaner if the app later uses more Foundry features.

Tradeoffs:

- Larger code change.
- Adds package dependencies.
- SDK APIs may move if packages are new or preview.
- Requires learning the SDK object model.

The SDK path is a better fit if the app will manage richer Foundry behavior from code, such as conversations, memory, tracing, tools, agent deployment, evaluation, or project management.

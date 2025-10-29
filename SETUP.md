# Azure AI Foundry Chat Integration Setup Guide

## Overview

This Blazor Static Web App now includes integration with Azure AI Foundry Agents, allowing authenticated users to interact with your "Consultologist-demo" agent through a chat interface.

## Architecture

- **Frontend**: Blazor WebAssembly with Chat UI component
- **Backend**: Azure Functions API endpoint that communicates with Azure AI Foundry
- **Authentication**: Microsoft Entra ID (Azure AD) via Static Web Apps built-in auth
- **AI Service**: Azure AI Foundry Agents using Managed Identity

## Prerequisites

1. Azure subscription with access to Azure AI Foundry
2. Azure Static Web Apps resource with System Assigned Managed Identity enabled
3. Azure AI Foundry project with the "Consultologist-demo" agent configured
4. Azure CLI installed and configured for local development

## Configuration Steps

### 1. Enable System Assigned Managed Identity on Static Web Apps

1. Navigate to your Static Web App in the Azure Portal
2. Go to **Settings** > **Identity**
3. Under **System assigned**, set **Status** to **On**
4. Click **Save** and note the Object (principal) ID

### 2. Assign Azure AI Developer Role

1. Navigate to your Azure AI Foundry project in the Azure Portal
2. Go to **Access control (IAM)**
3. Click **Add** > **Add role assignment**
4. Select **Azure AI Developer** role
5. In the **Members** tab, select **Managed identity**
6. Find and select your Static Web App's managed identity
7. Click **Review + assign**

### 3. Configure Application Settings in Azure

In your Static Web App, add the following application settings:

1. Go to **Settings** > **Configuration**
2. Add the following settings:

```
AzureAIFoundry__ProjectEndpoint=https://consultologist-eastus2-resource.services.ai.azure.com/api/projects/<your-project-name>
AzureAIFoundry__AgentId=asst_gI4fbjg8eQHv1P3lHdUDsZvr
```

**To get your Project Endpoint:**
- In Azure AI Foundry portal, go to your project
- Navigate to **Settings** > **Project properties**
- Copy the **Project endpoint** URL

**To get your Agent ID:**
- In Azure AI Foundry portal, open your agent ("Consultologist-demo")
- The Agent ID is visible in the agent details (starts with "asst_")

### 4. Local Development Setup

For local development, you need to:

1. **Authenticate with Azure CLI:**
   ```bash
   az login
   ```

2. **Create local.settings.json** in the `Api` folder:
   ```bash
   cd Api
   cp local.settings.example.json local.settings.json
   ```

3. **Update local.settings.json** with your values:
   ```json
   {
     "IsEncrypted": false,
     "Values": {
       "AzureWebJobsStorage": "",
       "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
       "AzureAIFoundry:ProjectEndpoint": "https://consultologist-eastus2-resource.services.ai.azure.com/api/projects/<your-project-name>",
       "AzureAIFoundry:AgentId": "asst_gI4fbjg8eQHv1P3lHdUDsZvr"
     },
     "Host": {
       "LocalHttpPort": 7071,
       "CORS": "*",
       "CORSCredentials": false
     }
   }
   ```

4. **Assign yourself Azure AI Developer role** for local testing:
   - Navigate to your Azure AI Foundry project in Azure Portal
   - Go to **Access control (IAM)** > **Add role assignment**
   - Select **Azure AI Developer** role
   - Assign it to your user account

## Features

### Chat Interface

- **Location**: `/chat` route (authenticated users only)
- **Features**:
  - Real-time conversation with Azure AI Foundry agent
  - Message history maintained during session
  - Clear conversation to start fresh
  - Loading indicators and error handling
  - Responsive design for mobile and desktop

### Security

- **Authentication**: Only authenticated users can access the chat page
- **Authorization**: API endpoint validates user authentication via Static Web Apps headers
- **Managed Identity**: Passwordless authentication to Azure AI Foundry in production
- **Azure CLI**: Local development uses your Azure CLI credentials

### Chat Service

The `ChatService` manages:
- Sending messages to the Azure Functions API
- Maintaining thread ID for conversation continuity
- Error handling and retry logic
- Session-based conversation state

## API Endpoint

**Endpoint**: `POST /api/chat`

**Request Body**:
```json
{
  "message": "Your message here",
  "threadId": "optional-existing-thread-id"
}
```

**Response**:
```json
{
  "success": true,
  "message": "Agent response here",
  "threadId": "thread-id-for-continuation"
}
```

## Usage

1. Navigate to your deployed Static Web App
2. Click **Sign in** to authenticate with Azure AD
3. Navigate to **AI Chat** in the menu
4. Start chatting with the AI assistant
5. Use **Clear Chat** to start a new conversation

## Troubleshooting

### Common Issues

1. **401 Unauthorized Error**
   - Verify you're signed in to the application
   - Check Static Web Apps authentication configuration

2. **403 Forbidden or Azure AI Request Failed**
   - Verify managed identity is enabled on Static Web Apps
   - Confirm Azure AI Developer role is assigned to the managed identity
   - Check the project endpoint URL is correct

3. **Agent Not Responding**
   - Verify the Agent ID is correct in configuration
   - Check the agent is deployed and active in Azure AI Foundry
   - Review Azure Functions logs for detailed error messages

4. **Local Development Issues**
   - Ensure `az login` is completed successfully
   - Verify your user account has Azure AI Developer role
   - Check local.settings.json has correct endpoint and agent ID

### Viewing Logs

**Azure Portal**:
1. Go to your Static Web App
2. Navigate to **Functions** > select the Chat function
3. View **Invocations** and **Logs**

**Azure AI Foundry**:
1. Open your project in AI Foundry portal
2. Navigate to **Agents** > **My threads**
3. View conversation history and agent responses

## Next Steps

- Customize the agent's instructions in Azure AI Foundry portal
- Add additional tools or capabilities to your agent
- Implement conversation history persistence using Supabase database
- Add file upload capabilities for document analysis
- Configure content safety filters and moderation

## Resources

- [Azure AI Foundry Documentation](https://learn.microsoft.com/azure/ai-foundry/)
- [Azure Static Web Apps Authentication](https://learn.microsoft.com/azure/static-web-apps/authentication-authorization)
- [Azure AI Agents SDK](https://learn.microsoft.com/azure/ai-foundry/agents/quickstart)
- [Managed Identity Overview](https://learn.microsoft.com/azure/active-directory/managed-identities-azure-resources/overview)

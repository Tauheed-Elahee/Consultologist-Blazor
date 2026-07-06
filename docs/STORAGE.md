# Durable Functions Storage Setup

## Portal Setup

1. In Azure Portal, create or choose a Storage account.
   - Use a standard general-purpose storage account.
   - Prefer the same region as the Function App.
   - It must support Blob, Queue, and Table storage.

2. Open the storage account:
   - **Security + networking** -> **Access keys**
   - Copy a **Connection string**.

3. Open your Function App:
   - **Settings** -> **Environment variables**
   - Go to **App settings**
   - Add or update:

```text
Name: AzureWebJobsStorage
Value: <storage account connection string>
```

4. For non-Flex Consumption Function Apps, also confirm this setting exists:

```text
Name: FUNCTIONS_WORKER_RUNTIME
Value: dotnet-isolated
```

For Flex Consumption Function Apps, do not add `FUNCTIONS_WORKER_RUNTIME`.
Flex stores the runtime in `properties.functionAppConfig.runtime.name` instead.

5. Click **Apply** / **Save**. The Function App will restart.

6. Retry:

```text
POST /api/ConsultGenerationJobs
```

## Optional Dedicated Durable Storage

For a production Durable setup, you can use a dedicated storage account instead
of reusing the host storage account. Add this to `host.json`:

```json
{
  "extensions": {
    "durableTask": {
      "storageProvider": {
        "connectionStringName": "DurableStorage"
      }
    }
  }
}
```

Then add this Function App setting:

```text
Name: DurableStorage
Value: <dedicated storage account connection string>
```

For now, using `AzureWebJobsStorage` is the fastest fix and is supported by
default.

## After Setup

In the storage account, Durable Functions will create runtime artifacts such as
queues, tables, and blobs for orchestration history, control messages, entities,
and leases. You usually do not create these manually.

# Network Hardening Notes

## Durable Functions Storage Baseline

The Durable Functions job implementation requires Azure Storage for orchestration,
entity, queue, table, blob, and lease state. The fastest reliable setup is a
standard general-purpose storage account referenced by the Function App setting:

```text
AzureWebJobsStorage=<storage-account-connection-string>
```

Recommended initial storage settings:

```text
Performance: Standard
Account kind: StorageV2 / General-purpose v2
Hierarchical namespace: Disabled
Enable Managed Identity for SMB: Disabled
Public network access: Enabled from all networks
Replication: LRS is acceptable for initial setup
```

Durable Functions uses queues, tables, and blobs. It does not need Azure Data
Lake hierarchical namespace and does not use SMB-mounted Azure Files for
orchestration/entity state.

## Public Network Access

For initial validation, keep storage public network access enabled from all
networks. This keeps the Function App able to reach `AzureWebJobsStorage`
without extra virtual network routing, private endpoint, or DNS work.

Validate the baseline before hardening:

1. Set `AzureWebJobsStorage` on the Function App.
2. Confirm `FUNCTIONS_WORKER_RUNTIME=dotnet-isolated`.
3. Restart the Function App.
4. Call `POST /api/ConsultGenerationJobs`.
5. Confirm the response is `202 Accepted`.

## Later Hardening Path

After Durable job startup and polling are confirmed, storage networking can be
locked down as a separate hardening task.

To disable public network access safely, configure all required private access:

```text
Function App VNet integration
Storage private endpoints
Private DNS for storage endpoints
Access to blob, queue, and table subresources
Access to file subresource if the hosting plan or deployment method requires Azure Files
Correct outbound routing from the Function App integration subnet
```

Do not disable public network access until the Function App has verified private
connectivity to the storage account. Otherwise the Functions host or Durable
runtime can fail before user code runs.

## SMB Managed Identity

Leave `Enable Managed Identity for SMB` disabled for the Durable storage account.
That feature is for Azure Files SMB identity-based access from VMs or machines
mounting file shares. It is not required for Durable Functions runtime state.

The Function App can still use a managed identity for other services, such as
Azure AI or Foundry. That is separate from the storage account's SMB identity
feature.

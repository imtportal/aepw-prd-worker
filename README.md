# Function App #2 — Evidence Worker (standalone, ready to publish)

Self-contained .NET 8 isolated Function App. One function:

| Function | Trigger | Role |
|---|---|---|
| `EvidenceWorker` | Service Bus queue (`evidence-queue`) | Download the file from SharePoint (Graph), POST it to the Backend API as multipart/form-data, and apply the retry contract |

The Worker is a **thin adapter**. It does **not** call Document Intelligence or SQL — the Backend API
owns DI extraction, the confidence gate, and the stored procedure. **Compiled with 0 errors/0 warnings**
against Microsoft.Graph 5.61.0.

## Flow

```
Service Bus (evidence-queue)
      │
      ▼
EvidenceWorker  ── download file via Graph ──►  POST multipart to Backend API
                                                      │
                                        API: Document Intelligence → gate → SP → SQL
                                                      │
                                        2xx / 4xx / 5xx  ── retry contract ──►
```

## Retry contract (matches the API design)

| API response | Worker behaviour |
|---|---|
| 2xx | success — message auto-completes |
| 4xx (except 408/429) | **permanent** — logged as error, message **completed** (no retry; bad input won't succeed) |
| 5xx, 408, 429 | **transient** — rethrow → Service Bus retries → DLQ after `maxDeliveryCount` |
| Graph download failure | propagates → retry → DLQ |

## Backend API contract (what your API team implements)

```
POST {BackendApiBaseUrl}{EvidenceProcessEndpointPath}      (default /api/evidence/process)
  Authorization: Bearer <Entra token, audience = API app registration>
  Content-Type: multipart/form-data
    file:  <document bytes>            (part name = "file", filename = original)
    fields: correlationId, eventType, siteId, driveId, itemId, eTag, fileName
  → 2xx { evidenceId, status: "AUTO_APPROVED"|"NEEDS_REVIEW", docConfidence }
  → 4xx permanent (bad input)   → 5xx transient (DI/SQL blip)

POST {BackendApiBaseUrl}{EvidenceDeleteEndpointPath}       (default /api/evidence/delete)
  Authorization: Bearer <token>
  Content-Type: multipart/form-data
    fields: correlationId, driveId, itemId, eTag, fileName   (no file)
  → 2xx on success
```

The API validates the incoming token's audience/appid against the Worker's Managed Identity.

## App settings to configure (on this Function App)

| Setting | Example | Notes |
|---|---|---|
| `ServiceBus__fullyQualifiedNamespace` | `sb-aepwevid.servicebus.windows.net` | MI auth (queue trigger) |
| `ServiceBusQueueName` | `evidence-queue` | |
| `BackendApiBaseUrl` | `https://your-api.azurewebsites.net` | base address of the existing API |
| `BackendApiScope` | `api://<api-app-client-id>/.default` | token audience = the API app registration |
| `EvidenceProcessEndpointPath` | `/api/evidence/process` | optional (has default) |
| `EvidenceDeleteEndpointPath` | `/api/evidence/delete` | optional (has default) |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | `InstrumentationKey=...` | telemetry |

Runtime settings (`FUNCTIONS_WORKER_RUNTIME=dotnet-isolated`, `FUNCTIONS_EXTENSION_VERSION=~4`,
`AzureWebJobsStorage`) come from the infra deployment.

## Managed-Identity grants this Worker needs (its own identity — separate from the Listener)

| Grant | Scope | Why |
|---|---|---|
| **Azure Service Bus Data Receiver** | Service Bus namespace | consume `evidence-queue` |
| **Storage Blob Data Owner** | Functions storage account | Functions runtime |
| **Graph Sites.Selected + per-site write** | via Graph (admin two-part grant) | download the file |
| **API access** | the API app registration exposes an app role/scope; grant the Worker's MI to it | so the token audience check passes |

> The Worker does NOT need Document Intelligence or SQL roles — the API holds those.

### API-side auth setup (one-time, with your API team)
1. The API has (or gets) an Entra **app registration**; note its **Application (client) ID**.
2. Expose it as an audience — `BackendApiScope = api://<client-id>/.default`.
3. Allow the Worker's Managed Identity to obtain a token for that audience (app role assignment on
   the API app, or the API simply validates the incoming token's `azp`/`appid` == Worker MI).

## Publish

```bash
dotnet build -c Release
func azure functionapp publish <worker-app-name> --dotnet-isolated
```

## Test

1. Ensure the Listener has already enqueued a message (upload a file to SharePoint), OR drop a test
   message on `evidence-queue`.
2. Watch **Log stream** on the Worker:
   - `Processed <file> (<n> bytes) via Backend API.`  → success
   - `Transient failure ... will retry.`              → API 5xx (check the API)
   - `Permanent failure ... completing without retry.`→ API 4xx (bad input / contract mismatch)
3. Confirm the row landed in SQL (the API wrote it) and the queue drained.

## Notes
- `maxConcurrentCalls` is 8 (host.json) — tune against your API's throughput.
- HttpClient timeout is 5 min to tolerate slow DI extraction on large documents.
- If the Worker and API are both new to this environment, bring up the API endpoint first so the
  Worker has something to call.

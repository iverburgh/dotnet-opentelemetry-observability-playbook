# Raw payload archive

Some integrations need the exact bytes a partner sent or received: to replay a message, to settle a
dispute, to debug a parser. Logs are the wrong place for that. They are expensive per gigabyte, searchable
by everyone with read access to the workspace, and truncated above a size limit.

Put raw payloads in blob storage and log only a pointer.

## Layout

```text
{container}/{yyyy}/{MM}/{dd}/{traceId}/{guid}.jsonl
```

| Part | Why |
|---|---|
| Date folders | Lifecycle rules and manual clean-up by day; listing one day is cheap. |
| Trace id | Everything one request stored is in one folder, found with the `x-correlation-id`. |
| A new GUID per file | No overwrites when one trace stores several payloads. |
| JSON Lines | One record per line, appendable, readable with any tool. |

Each line holds the payload and the context needed to understand it without the logs:

```json
{"timestamp":"2026-01-01T12:00:00Z","direction":"inbound","partner":"carrier-a","messageType":"StatusUpdate","traceId":"1c72…","spanId":"b7ad…","payload":{…}}
```

## Find it from the telemetry

Log one line when you store a payload, with the blob name in a scope or as a parameter:

```csharp
[LoggerMessage(EventId = 3001, Level = LogLevel.Information, Message = "Archived {MessageType} payload as {BlobName}")]
public static partial void PayloadArchived(ILogger logger, string messageType, string blobName);
```

The log line is in the trace; the blob name finds the bytes.

## Find it without the telemetry

Blob index tags make payloads searchable by business keys without opening them:

| Tag | Example |
|---|---|
| `traceId` | `1c72f1a0…` |
| `messageType` | `StatusUpdate` |
| `partner` | `carrier-a` |

`Find Blobs by Tags` returns every blob with `traceId = '1c72…'` across date folders. Tags are limited to
ten per blob and are visible to anyone who can read tags: no personal data in them.

## Protect it

- A separate storage account or container, with access for the integration's identity (write) and a small
  group (read). Not the same access as the logs.
- A lifecycle rule that deletes after the retention you need — and not later. Raw payloads are often the
  most sensitive data the system has.
- Immutable storage only if a regulation requires it; it also prevents deleting what you must delete.
- No public access, no shared keys; managed identities and RBAC only.

## When not to

- Payloads you can rebuild from your own database. Store the identifiers instead.
- Every internal message. The outbox table already holds what you published; archive at the edges, where
  a partner is involved.

## Cost

Blob storage in the cool tier costs a small fraction of log ingestion per gigabyte, and nothing per query
you do not run. Moving payloads out of logs is often the biggest single cut in a telemetry bill. See
[sampling and cost](sampling-and-cost.md).

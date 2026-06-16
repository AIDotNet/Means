# Means

> **S3-Compatible Object Storage — Enterprise-Grade, Self-Deployable, .NET-Powered.**

Means is a self-deployable S3-compatible object storage service built on ASP.NET Core. It uses the custom `XlFs` storage backend (inspired by MinIO's architecture — single-node multi-disk, object manifest, quorum-based writes, LogDb metadata indexing), powered entirely by the self-developed **MeansLogDb** metadata engine.

> **As of 2026-05-09:** This document reflects the **current code state** of the repository, not aspirational design goals.

---

## Table of Contents

- [Architecture Overview](#architecture-overview)
- [Implemented Capabilities](#implemented-capabilities)
  - [S3 Data Plane](#s3-data-plane-v1-baseline)
  - [Console Management Plane](#console-management-plane)
- [Not Yet Implemented](#not-yet-implemented)
- [Quick Start](#quick-start)
  - [Prerequisites](#1-prerequisites)
  - [Run with .NET CLI](#2-start-the-service)
  - [Run with Docker Compose](#21-docker-compose)
  - [Access the Console](#3-access-the-console)
  - [S3 Access Methods](#4-local-s3-access-methods)
- [Configuration Reference](#configuration-reference)
- [Development & Testing](#development--testing)
  - [Running Tests](#running-tests)
  - [Frontend Development](#frontend-development)
- [SDKs & Specification](#sdks--specification)
- [Project Map](#project-map)
- [License](#license)

---

## Architecture Overview

| Layer | Project | Purpose |
|---|---|---|
| **Host / API** | `src/Means` | ASP.NET Core `net10.0` application entry point, middleware pipeline, endpoints, DI composition |
| **Protocol** | `src/Means.Protocol.S3` | S3-compatible address resolution, SigV4 signature validation, S3 XML response serialization |
| **Storage Engine** | `src/Means.Infrastructure.XlFs` | Default XlFs storage backend (multi-disk, manifest, quorum, MeansLogDb metadata) |
| **Core Abstractions** | `src/Means.Core` | Domain models, storage interfaces, policies, error definitions, placement strategies |
| **Management** | `src/Means/Endpoints/Console` | Cookie-authenticated Console JSON API |
| **Web UI** | `src/Means/wwwroot` | React-based management dashboard (built from `web/`) |
| **SDK - C#** | `SDKs/csharp` | Full-featured C# client SDK |
| **SDK - TypeScript** | `SDKs/typescript` | Browser-safe TS SDK + Node extension (SigV4) |
| **Tests** | `tests/` | Unit, integration, and contract tests |

### Solution Structure (`Means.slnx`)

```
Means.slnx
├── src/
│   ├── Means/                  # Host application
│   ├── Means.Core/             # Core abstractions & domain
│   ├── Means.Infrastructure.XlFs/  # XlFs storage engine
│   └── Means.Protocol.S3/      # S3 protocol implementation
│
├── tests/
│   ├── Means.UnitTests/        # Unit tests
│   ├── Means.IntegrationTests/ # Integration tests
│   └── Means.ContractTests/    # SDK spec compliance tests
│
├── SDKs/
│   ├── csharp/                 # C# SDK
│   ├── typescript/             # TypeScript SDK
│   └── spec/                   # Machine-readable protocol spec
│
├── web/                        # React frontend (Vite)
└── docs/                       # Documentation site
```

---

## Implemented Capabilities

### S3 Data Plane (v1 Baseline)

| Category | Operations | Status |
|---|---|---|
| **Service** | `ListBuckets` | ✅ |
| **Bucket** | `CreateBucket`, `HeadBucket`, `DeleteBucket` | ✅ |
| **Object** | `PutObject`, `GetObject`, `HeadObject`, `DeleteObject` | ✅ |
| **Listing** | `ListObjectsV2` (`prefix`, `delimiter`, `continuation-token`, `max-keys`) | ✅ |
| **Copy** | `CopyObject` (`x-amz-copy-source`, `COPY`/`REPLACE` metadata directive) | ✅ |
| **Multipart** | Initiate, UploadPart, UploadPartCopy, Complete, Abort, ListParts, ListMultipartUploads | ✅ |
| **Versioning** | `?versioning`, `?versions`, GET/HEAD/DELETE by `versionId`, delete markers | ✅ |
| **Tagging** | `?tagging` (current & specific version) | ✅ |
| **Lifecycle** | `?lifecycle` (expiration, noncurrent cleanup, AbortIncompleteMultipartUpload) | ✅ |
| **CORS** | `?cors` config CRUD, OPTIONS preflight | ✅ |
| **Notification** | `?notification` config persistence (reserved interface) | ✅ |
| **Policy** | `?policy` sub-resource (`GET`/`PUT`/`DELETE`) | ✅ |
| **Pre-signed URL** | SigV4 pre-signed `GET`, `PUT`, multipart `UploadPart` | ✅ |

#### Key Implementation Details

- **Address Resolution:** Supports both `path-style` and `virtual-hosted-style` S3 addressing.
- **Response Format:** Unified S3-compatible XML (listings, errors, copy results).
- **Range Reads:** `Range` header → `206 Partial Content`; invalid ranges → `416 InvalidRange` with `Content-Range` header.
- **Compression:** Content-negotiated (`br` / `gzip`) on responses; disabled for Range requests.
- **Atomic Writes:** `PutObject` is atomic — objects become visible only after the metadata transaction commits.
- **Multipart Rules:**
  - `partNumber`: `1`–`10000`
  - Minimum part size (except last): `5 MiB`
  - Final ETag: `md5(concat(part-md5-bytes))-part-count`
  - `Means:RequestLimits:MaxUploadSizeBytes` applies per-part, not total assembled size.
- **Concurrency Control:** S3 `PUT` and multipart `UploadPart` share a global concurrency limit (default: 64). Exceeding returns `503 SlowDown` with `Retry-After: 1`.
- **Checksum Verification:** Disabled by default on reads to avoid double I/O for large objects. Enable via `VerifyChecksumOnRead`. Background scrub always checksums and enqueues repairs.

### Console Management Plane

Built-in management API (`/api/console`, JSON) and web dashboard (React):

| Feature | Endpoint / Details |
|---|---|
| **Authentication** | Cookie-based login/logout/session check |
| **Bucket Management** | Create, delete, browse objects |
| **Bucket Policy** | View and edit bucket policies |
| **Pre-signed URLs** | Generate upload/download links |
| **Large File Upload** | Multipart from 5 MiB, 16 MiB parts, 3 concurrent |
| **AccessKey Management** | Create, delete, list access keys |
| **System Settings** | Configure max upload size |
| **Audit & Metrics** | Audit log, hourly request statistics dashboard |
| **Cluster Status** | Node/disk health, diagnostics export (`/api/console/cluster`, `/api/console/diagnostics`) |
| **Monitoring Export** | Prometheus `/metrics`, optional OpenTelemetry (OTLP) |
| **Background Tasks** | Unified management with manual triggers — heartbeat, disk health, metadata consistency, storage GC, repair, rebalance, lifecycle, replication worker |
| **Rate Limiting** | Fixed-window limits for Console login, Console API, and S3 data plane |

---

## Not Yet Implemented

The following features are **not present** in the current codebase:

- 🔲 **Replication** — Cross-bucket/bucket replication rules
- 🔲 **Object Lock / Retention** — WORM compliance
- 🔲 **IAM / STS** — Full identity and access management model
- 🔲 **Distributed Sharding / EC** — Multi-node data sharding and erasure coding across nodes

---

## Quick Start

### 1) Prerequisites

| Dependency | Version | Notes |
|---|---|---|
| .NET SDK | `10.0` | Required for building and running |
| Node.js | `20+` | Only needed for frontend development |

### 2) Start the Service

```bash
# Restore dependencies
dotnet restore Means.slnx

# Build the solution
dotnet build Means.slnx

# Run with the 'http' launch profile
dotnet run --project src/Means/Means.csproj --launch-profile http
```

The development server starts at **`http://localhost:5178`**.

### 2.1) Docker Compose

**Single-node (default XlFs):**

```bash
docker compose up -d --build
```

Access: `http://localhost:5178`

Default credentials (override via `.env` or environment variables for production):
- Console: `meansadmin` / `meansadmin-local`
- S3: `meansadmin` / `meansadmin-local-secret`

**Multi-node (experimental):**

```bash
docker compose -f compose.multinode.yaml up -d --build
```

| Node | Address |
|---|---|
| `means-node1` | `http://localhost:5181` |
| `means-node2` | `http://localhost:5182` |
| `means-node3` | `http://localhost:5183` |

> **⚠️ Important:** The multi-node setup is for topology and operations page validation only. Each node has an independent XlFs namespace. Do **not** place a data-plane load balancer in front of these nodes until distributed metadata/RPC is implemented.

### 3) Access the Console

Open `http://localhost:5178` and sign in with the default development credentials:

| Field | Value |
|---|---|
| Username | `admin` |
| Password | `meansadmin` |

### 4) Local S3 Access Methods

**Same-origin alias (development):**

```
http://localhost:5178/s3/{bucket}/{key}
```

**Standard host styles (requires DNS/hosts configuration):**

| Style | URL Pattern |
|---|---|
| Path-style | `http(s)://api.means.local/{bucket}/{key}` |
| Virtual-hosted-style | `http(s)://{bucket}.means.local/{key}` |

---

## Configuration Reference

All configuration is in `src/Means/appsettings.json` under the `Means` section.

### S3 Settings

| Key | Default | Description |
|---|---|---|
| `Means:S3:ServiceHost` | `api.means.local` | Path-style hostname |
| `Means:S3:DomainSuffix` | `means.local` | Virtual-hosted-style domain suffix |
| `Means:S3:AliasPrefix` | `/s3` | Same-origin S3 alias prefix |

### Storage Settings

| Key | Default | Description |
|---|---|---|
| `Means:Storage:ObjectsPath` | `data/objects` | Single-disk directory (when `Disks` not configured) |
| `Means:Storage:Disks` | `/data/xlfs/disk1...` | XlFs multi-disk root directories; each gets `.means.sys/format.json` |
| `Means:Storage:ErasureDataShards` | `2` | Reed-Solomon data shards; used when online disks satisfy data+parity |
| `Means:Storage:ErasureParityShards` | `2` | Reed-Solomon parity shards; falls back to full-copy quorum when insufficient disks |
| `Means:Storage:WriteQuorum` | `3` | Minimum disks required for write success |
| `Means:Storage:ReadQuorum` | `1` | Minimum disks required for read success |
| `Means:Storage:MetaSyncMode` | `Always` | Metadata WAL flush strategy |
| `Means:Storage:VerifyChecksumOnRead` | `false` | Synchronous SHA256 verification on read (adds full I/O) |
| `Means:Storage:DefaultAccessKey` | `meansadmin` | Default S3 access key |
| `Means:Storage:DefaultSecretKey` | `meansadminsecret` | Default S3 secret key |
| `Means:Storage:MultipartUploadCleanupAgeHours` | `24` | Age threshold for incomplete multipart upload cleanup |
| `Means:Storage:MultipartUploadCleanupIntervalMinutes` | `60` | Cleanup background task interval |
| `Means:Storage:GarbageCollectionIntervalSeconds` | `3600` | Orphan file GC scan interval |
| `Means:Storage:GarbageCollectionBatchSize` | `1000` | Max candidates per GC run |
| `Means:Storage:GarbageCollectionTempFileAgeMinutes` | `60` | Min age for temp/unreferenced file GC eligibility |
| `Means:Storage:ReplicationIntervalSeconds` | `3600` | Replication worker interval (reports status only when no rules configured) |

### Request Limits

| Key | Default | Description |
|---|---|---|
| `Means:RequestLimits:MaxUploadSizeBytes` | `1073741824` (1 GiB) | Max per-request upload size |
| `Means:RequestLimits:MaxConcurrentUploadRequests` | `64` | Global concurrency for `PUT`/`UploadPart`; returns `503 SlowDown` when exceeded |

### Rate Limiting

| Key | Default | Description |
|---|---|---|
| `Means:RateLimits:Enabled` | `true` | Enable fixed-window rate limiting |
| `Means:RateLimits:ConsoleLoginPermitLimit` | `10` | Requests per console login window |
| `Means:RateLimits:ConsoleLoginWindowSeconds` | `60` | Console login window (seconds) |
| `Means:RateLimits:ConsoleApiPermitLimit` | `600` | Requests per console API window |
| `Means:RateLimits:ConsoleApiWindowSeconds` | `60` | Console API window (seconds) |
| `Means:RateLimits:S3PermitLimit` | `1200` | Requests per S3 data plane window |
| `Means:RateLimits:S3WindowSeconds` | `60` | S3 data plane window (seconds) |

### Telemetry

| Key | Default | Description |
|---|---|---|
| `Means:Telemetry:Enabled` | `false` | Enable OpenTelemetry tracing |
| `Means:Telemetry:ServiceName` | `Means` | Tracing resource service name |
| `Means:Telemetry:OtlpEndpoint` | *(empty)* | OTLP exporter endpoint |
| `Means:Telemetry:SampleRatio` | `1.0` | Trace sampling ratio `[0, 1]` |

### Cluster

| Key | Default | Description |
|---|---|---|
| `Means:Cluster:InternalAuthToken` | *(empty)* | Inter-node shard RPC token; empty = `/api/internal/cluster` returns 404 |
| `Means:Cluster:MaxShardTransferBytes` | `5368709120` (5 GiB) | Per-shard streaming transfer limit |

### Console

| Key | Default | Description |
|---|---|---|
| `Means:Console:AdminUser` | `admin` | Admin username |
| `Means:Console:AdminPassword` | `meansadmin` | Admin password |
| `Means:Console:SessionHours` | `8` | Session duration (hours) |

### Important Notes

- **Production security:** The service refuses to start if default console credentials are detected. Always replace `AdminUser`/`AdminPassword` and `DefaultAccessKey`/`DefaultSecretKey` for production.
- **Multipart cleanup:** Background tasks automatically clean up incomplete upload metadata and part files. Frontend cancellation best-effort calls abort, but disk reclamation does not depend on browser request success.
- **Read verification:** SHA256 verification is off by default on reads. Enable `VerifyChecksumOnRead` for strong verification. Background scrub always checksums.
- **Rate limiting responses:** Console → JSON `SlowDown`; S3 → XML `SlowDown`; both include `Retry-After` header.
- **Prometheus:** Scrape `/metrics`. Recommended alerting rules in `docs/operations/deployment-observability.md`.
- **OpenTelemetry:** Disabled by default. Set `Means:Telemetry:Enabled=true` to collect ASP.NET Core and background task spans. Configure `OtlpEndpoint` for collector export.

---

## Development & Testing

### Running Tests

```bash
dotnet test Means.slnx
```

| Test Project | Focus |
|---|---|
| `Means.UnitTests` | Address resolution, naming rules, policy evaluation, compression logic |
| `Means.IntegrationTests` | S3 full-chain workflows, pre-signed URLs, Console API end-to-end |
| `Means.ContractTests` | SDK protocol YAML spec vs. fixture completeness |

### Frontend Development

```bash
cd web
npm install
npm run dev
```

- Vite dev server proxies `/api` and `/s3` to `http://localhost:5178`.
- Build for production: `npm run build` → outputs to `src/Means/wwwroot`.

---

## SDKs & Specification

| Package | Location | Description |
|---|---|---|
| **C# SDK** | `SDKs/csharp` | Full-featured .NET client SDK (`Means.Client.csproj`) |
| **TypeScript SDK** | `SDKs/typescript/packages/sdk` | Browser-safe S3 client |
| **TypeScript Node Extension** | `SDKs/typescript/packages/sdk-node` | SigV4 signing & pre-signing for Node.js |
| **Protocol Spec (YAML)** | `SDKs/spec/means-sdk-v1.yaml` | Machine-readable API specification |
| **Protocol Spec (Markdown)** | `SDKs/spec/means-sdk-v1.md` | Human-readable protocol documentation |
| **SDK Examples** | `SDKs/examples/` | Usage examples in C# and TypeScript |

---

## Project Map

```
Means/
├── CHANGELOG.md
├── IMPLEMENTATION_PLAN.md
├── Means.slnx                      # .NET solution file
├── compose.yaml                    # Single-node Docker Compose
├── compose.multinode.yaml          # Multi-node experimental Compose
├── README.md                       # This file (English)
├── README.zh.md                    # Chinese documentation
│
├── src/
│   ├── Means/                      # ASP.NET Core host
│   │   ├── Program.cs              # Application entry point
│   │   ├── Composition/            # DI composition & service registration
│   │   ├── Configuration/          # Configuration binding & validation
│   │   ├── Endpoints/              # HTTP API endpoints (Console, etc.)
│   │   ├── Middleware/             # ASP.NET Core middleware pipeline
│   │   ├── Services/               # Application services
│   │   ├── Security/               # Authentication & authorization
│   │   ├── Serialization/          # JSON serialization configuration
│   │   ├── Properties/             # Launch profiles, assembly info
│   │   └── wwwroot/                # Built frontend artifacts
│   │
│   ├── Means.Core/                 # Core domain & abstractions
│   │   ├── Abstractions/           # Storage interfaces & contracts
│   │   ├── Constants/              # Well-known constants
│   │   ├── Errors/                 # Domain error types
│   │   ├── Models/                 # Domain models
│   │   ├── Placement/              # Disk placement strategies
│   │   ├── Policies/               # Storage policies
│   │   └── Requests/               # Request model definitions
│   │
│   ├── Means.Infrastructure.XlFs/  # XlFs storage engine
│   │   ├── Store/                  # Core store implementation
│   │   ├── LogDb/                  # MeansLogDb metadata engine
│   │   ├── Models/                 # XlFs-specific models
│   │   └── XlFsOptions.cs          # Storage options
│   │
│   └── Means.Protocol.S3/         # S3 protocol layer
│       ├── Addressing/             # Path-style & vhost-style resolution
│       ├── Compression/            # Content negotiation & compression
│       ├── Serialization/          # S3 XML response serialization
│       ├── Signing/                # SigV4 signature validation
│       └── Validation/             # Request validation
│
├── tests/
│   ├── Means.UnitTests/
│   ├── Means.IntegrationTests/
│   └── Means.ContractTests/
│
├── SDKs/
│   ├── csharp/
│   ├── typescript/
│   ├── spec/
│   └── examples/
│
├── web/                            # React frontend (Vite + TypeScript)
│   ├── src/                        # React components & pages
│   ├── public/                     # Static assets
│   ├── vite.config.ts              # Vite configuration
│   └── package.json
│
├── docs/                           # Documentation site (Next.js)
│   ├── content/docs/               # Documentation content
│   ├── app/                        # Next.js app router pages
│   └── source.config.ts
│
└── scripts/                        # Utility scripts
    ├── benchmarks/                 # S3 benchmark scripts
    └── compatibility/              # S3 client compatibility matrix
```

---

## License

MIT — see [LICENSE](LICENSE) for details.

import i18n from "@/i18n"

export type Session = {
  authenticated: boolean
  userName: string
}

export type BucketUsage = {
  bucketName: string
  createdAt: string
  objectCount: number
  totalBytes: number
}

export type BucketConsoleSummary = {
  bucketName: string
  createdAt: string
  objectCount: number
  totalBytes: number
  requestCount: number
  errorCount: number
  ingressBytes: number
  egressBytes: number
  putCount: number
  getCount: number
  deleteCount: number
  headCount: number
  listCount: number
  lastActivityAt: string | null
}

export type BucketSummary = {
  hours: number
  startUtc: string
  endUtc: string
  summary: BucketConsoleSummary
}

export type BucketSettings = {
  bucketName: string
  defaultResponseHeaders: Record<string, string>
  defaultMetadata: Record<string, string>
  updatedAt: string | null
}

export type Overview = {
  bucketCount: number
  objectCount: number
  totalBytes: number
  metadataPath: string
  objectsPath: string
  serviceHost: string
  domainSuffix: string
  aliasPrefix: string
  version: string
  topBuckets: BucketUsage[]
}

export type DashboardStats = {
  range: {
    hours: number
    startUtc: string
    endUtc: string
  }
  capacity: {
    totalBytes: number
    usedBytes: number
    freeBytes: number
    objectBytes: number
    usedPercent: number
  }
  summary: {
    bucketCount: number
    objectCount: number
    totalBytes: number
    requestCount: number
    errorCount: number
    version: string
  }
  nodes: {
    serversOnline: number
    serversOffline: number
    drivesOnline: number
    drivesOffline: number
    metadataPath: string
    objectsPath: string
    serviceHost: string
    domainSuffix: string
    aliasPrefix: string
    paths: DashboardPathStatus[]
  }
  hourly: DashboardHourlyMetric[]
  recentBuckets: DashboardRecentBucket[]
  pools: DashboardPool[]
}

export type DashboardPathStatus = {
  name: string
  path: string
  online: boolean
}

export type DashboardHourlyMetric = {
  hourUtc: string
  requestCount: number
  errorCount: number
  ingressBytes: number
  egressBytes: number
  putCount: number
  getCount: number
  deleteCount: number
  headCount: number
  listCount: number
}

export type DashboardRecentBucket = {
  bucketName: string
  requestCount: number
  errorCount: number
  ingressBytes: number
  egressBytes: number
  objectCount: number
  totalBytes: number
  lastActivityAt: string | null
}

export type DashboardPool = {
  name: string
  totalBytes: number
  usedBytes: number
  freeBytes: number
  objectBytes: number
  driveCount: number
  onlineDrives: number
  offlineDrives: number
}

export type StorageClusterInfo = {
  clusterId: string
  name: string
  createdAt: string
}

export type StoragePoolInfo = {
  poolId: string
  clusterId: string
  name: string
  createdAt: string
  nodeCount: number
  diskCount: number
  totalBytes: number
  availableBytes: number
}

export type StorageDiskInfo = {
  diskId: string
  nodeId: string
  poolId: string
  mountPath: string
  totalBytes: number
  availableBytes: number
  status: "Online" | "Offline" | string
  lastSeenAt: string
}

export type ClusterNodeInfo = {
  nodeId: string
  clusterId: string
  hostName: string
  endpoint: string
  status: "Online" | "Offline" | string
  registeredAt: string
  lastHeartbeatAt: string
  disks: StorageDiskInfo[]
}

export type ClusterTopology = {
  cluster: StorageClusterInfo
  nodes: ClusterNodeInfo[]
  pools: StoragePoolInfo[]
}

export type ObjectReplicaDiagnostics = {
  desiredReplicaCount: number
  objectCount: number
  replicaRecordCount: number
  committedReplicaRecordCount: number
  existingReplicaFileCount: number
  missingReplicaFileCount: number
  missingReplicaObjectCount: number
  underReplicatedObjectCount: number
  objectsWithoutReplicaManifestCount: number
}

export type ReplicaRepairQueueStatusDiagnostics = {
  status: string
  count: number
}

export type ReplicaRepairQueueItemDiagnostics = {
  bucketName: string
  key: string
  objectId: string
  reason: string
  status: string
  attemptCount: number
  queuedAt: string
  updatedAt: string
  lastAttemptAt: string | null
  nextAttemptAt: string | null
  lastError: string | null
}

export type ReplicaRepairQueueDiagnostics = {
  totalCount: number
  pendingCount: number
  completedCount: number
  failedCount: number
  retryableFailedCount: number
  maxAttemptsReachedCount: number
  oldestPendingAt: string | null
  lastUpdatedAt: string | null
  statuses: ReplicaRepairQueueStatusDiagnostics[]
  items: ReplicaRepairQueueItemDiagnostics[]
}

export type ErasureCodingDiagnostics = {
  profileCount: number
  enabledProfileCount: number
  disabledProfileCount: number
}

export type MetadataDiagnostics = {
  pendingCommitCount: number
  orphanedReplicaRecordCount: number
}

export type ClusterInternalTransportDiagnostics = {
  shardRpcEnabled: boolean
  maxShardTransferBytes: number
}

export type ClusterDiagnosticsSummary = {
  bucketCount: number
  objectCount: number
  totalObjectBytes: number
  nodeCount: number
  onlineNodeCount: number
  offlineNodeCount: number
  poolCount: number
  diskCount: number
  onlineDiskCount: number
  offlineDiskCount: number
  totalCapacityBytes: number
  availableCapacityBytes: number
  usedCapacityBytes: number
}

export type BackgroundTaskSnapshot = {
  taskId: string
  name: string
  category: string
  intervalSeconds: number
  manualRunSupported: boolean
  status: "NeverRun" | "Running" | "Succeeded" | "Failed" | "Cancelled" | string
  successCount: number
  failureCount: number
  lastStartedAt: string | null
  lastCompletedAt: string | null
  lastDurationMilliseconds: number | null
  lastResult: string | null
  lastError: string | null
}

export type BackgroundTaskRunRecord = {
  taskId: string
  name: string
  category: string
  status: "NeverRun" | "Running" | "Succeeded" | "Failed" | "Cancelled" | string
  startedAt: string
  completedAt: string
  durationMilliseconds: number
  result: string | null
  error: string | null
}

export type BackgroundTaskGroup = {
  category: string
  name: string
  tasks: BackgroundTaskSnapshot[]
}

export type BackgroundTaskManagement = {
  groups: BackgroundTaskGroup[]
  tasks: BackgroundTaskSnapshot[]
  history: BackgroundTaskRunRecord[]
}

export type ClusterDiagnostics = {
  generatedAt: string
  summary: ClusterDiagnosticsSummary
  topology: ClusterTopology
  objectReplicas: ObjectReplicaDiagnostics
  repairQueue: ReplicaRepairQueueDiagnostics
  metadata: MetadataDiagnostics
  erasureCoding: ErasureCodingDiagnostics
  internalTransport: ClusterInternalTransportDiagnostics
  backgroundTasks: BackgroundTaskSnapshot[]
}

export type ListedObject = {
  key: string
  eTag: string
  size: number
  lastModified: string
  contentType: string
}

export type ListObjectsResult = {
  bucketName: string
  prefix: string | null
  delimiter: string | null
  keyCount: number
  isTruncated: boolean
  nextContinuationToken: string | null
  objects: ListedObject[]
  commonPrefixes: string[]
}

export type ObjectInfo = {
  bucketName: string
  key: string
  objectId: string
  eTag: string
  contentLength: number
  contentType: string
  lastModified: string
  metadata: Record<string, string>
  cacheControl: string | null
  contentDisposition: string | null
}

export type BucketVersioningStatus = "Off" | "Enabled" | "Suspended"

export type BucketVersioning = {
  bucketName: string
  status: BucketVersioningStatus
}

export type ObjectVersion = {
  key: string
  versionId: string
  isLatest: boolean
  isDeleteMarker: boolean
  eTag: string
  size: number
  lastModified: string
}

export type ListObjectVersionsResult = {
  bucketName: string
  prefix: string | null
  delimiter: string | null
  keyMarker: string | null
  versionIdMarker: string | null
  maxKeys: number
  isTruncated: boolean
  nextKeyMarker: string | null
  nextVersionIdMarker: string | null
  versions: ObjectVersion[]
  commonPrefixes: string[]
}

export type LifecycleRule = {
  id: string
  status: "Enabled" | "Disabled"
  prefix: string
  expirationDays: number | null
  noncurrentVersionExpirationDays: number | null
  abortIncompleteMultipartUploadDays: number | null
}

export type BucketLifecycle = {
  rules: LifecycleRule[]
}

export type PolicyResponse = {
  policy: string
}

export type AccessKeyInfo = {
  accessKey: string
  enabled: boolean
  createdAt: string
}

export type AccessKeySecretResult = AccessKeyInfo & {
  secretKey: string
}

export type AuditEntry = {
  id: number
  occurredAt: string
  actor: string
  action: string
  resource: string
  status: string
  message: string | null
}

export type PresignedTransfer = {
  method: "GET" | "PUT"
  url: string
  expiresSeconds: number
}

export type MultipartUpload = {
  bucketName: string
  key: string
  uploadId: string
}

export type CompletedMultipartPart = {
  partNumber: number
  eTag: string
}

export type SystemSettings = {
  maxUploadSizeBytes: number
  minimumMaxUploadSizeBytes: number
  maximumMaxUploadSizeBytes: number
  publicOrigin: string | null
}

export class ApiError extends Error {
  readonly statusCode: number
  readonly code: string

  constructor(message: string, statusCode: number, code: string) {
    super(message)
    this.name = "ApiError"
    this.statusCode = statusCode
    this.code = code
  }
}

type RequestOptions = Omit<RequestInit, "body"> & {
  body?: unknown
}

async function request<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const headers = new Headers(options.headers)
  let body: BodyInit | undefined
  if (options.body !== undefined) {
    headers.set("Content-Type", "application/json")
    body = JSON.stringify(options.body)
  }

  const response = await fetch(path, {
    ...options,
    body,
    headers,
    credentials: "include",
  })

  if (!response.ok) {
    const fallback = response.statusText || i18n.t("api.requestFailed")
    try {
      const payload = (await response.json()) as {
        code?: string
        message?: string
        statusCode?: number
      }
      throw new ApiError(
        payload.message ?? fallback,
        payload.statusCode ?? response.status,
        payload.code ?? "HttpError"
      )
    } catch (error) {
      if (error instanceof ApiError) {
        throw error
      }

      throw new ApiError(fallback, response.status, "HttpError")
    }
  }

  if (response.status === 204) {
    return undefined as T
  }

  return (await response.json()) as T
}

export const api = {
  login: (userName: string, password: string) =>
    request<Session>("/api/console/auth/login", {
      method: "POST",
      body: { userName, password },
    }),
  logout: () =>
    request<void>("/api/console/auth/logout", {
      method: "POST",
    }),
  session: () => request<Session>("/api/console/auth/session"),
  overview: () => request<Overview>("/api/console/overview"),
  dashboardStats: (hours = 24) =>
    request<DashboardStats>(`/api/console/dashboard-stats?hours=${hours}`),
  cluster: () => request<ClusterTopology>("/api/console/cluster"),
  diagnostics: () => request<ClusterDiagnostics>("/api/console/diagnostics"),
  backgroundTasks: () => request<BackgroundTaskManagement>("/api/console/background-tasks"),
  runBackgroundTask: (taskId: string) =>
    request<BackgroundTaskSnapshot>(
      `/api/console/background-tasks/${encodeURIComponent(taskId)}/run`,
      { method: "POST" }
    ),
  buckets: () => request<BucketUsage[]>("/api/console/buckets"),
  createBucket: (bucketName: string) =>
    request<BucketUsage>("/api/console/buckets", {
      method: "POST",
      body: { bucketName },
    }),
  deleteBucket: (bucketName: string) =>
    request<void>(`/api/console/buckets/${encodeURIComponent(bucketName)}`, {
      method: "DELETE",
    }),
  bucketSummary: (bucketName: string, hours = 24) =>
    request<BucketSummary>(
      `/api/console/buckets/${encodeURIComponent(bucketName)}/summary?hours=${hours}`
    ),
  bucketSettings: (bucketName: string) =>
    request<BucketSettings>(
      `/api/console/buckets/${encodeURIComponent(bucketName)}/settings`
    ),
  updateBucketSettings: (
    bucketName: string,
    defaultResponseHeaders: Record<string, string>,
    defaultMetadata: Record<string, string>
  ) =>
    request<BucketSettings>(
      `/api/console/buckets/${encodeURIComponent(bucketName)}/settings`,
      {
        method: "PUT",
        body: { defaultResponseHeaders, defaultMetadata },
      }
    ),
  bucketVersioning: (bucketName: string) =>
    request<BucketVersioning>(
      `/api/console/buckets/${encodeURIComponent(bucketName)}/versioning`
    ),
  putBucketVersioning: (bucketName: string, status: BucketVersioningStatus) =>
    request<BucketVersioning>(
      `/api/console/buckets/${encodeURIComponent(bucketName)}/versioning`,
      {
        method: "PUT",
        body: { status },
      }
    ),
  bucketLifecycle: (bucketName: string) =>
    request<BucketLifecycle>(
      `/api/console/buckets/${encodeURIComponent(bucketName)}/lifecycle`
    ),
  putBucketLifecycle: (bucketName: string, rules: LifecycleRule[]) =>
    request<BucketLifecycle>(
      `/api/console/buckets/${encodeURIComponent(bucketName)}/lifecycle`,
      {
        method: "PUT",
        body: { rules },
      }
    ),
  deleteBucketLifecycle: (bucketName: string) =>
    request<void>(`/api/console/buckets/${encodeURIComponent(bucketName)}/lifecycle`, {
      method: "DELETE",
    }),
  policy: (bucketName: string) =>
    request<PolicyResponse>(
      `/api/console/buckets/${encodeURIComponent(bucketName)}/policy`
    ),
  putPolicy: (bucketName: string, policy: string) =>
    request<void>(`/api/console/buckets/${encodeURIComponent(bucketName)}/policy`, {
      method: "PUT",
      body: { policy },
    }),
  deletePolicy: (bucketName: string) =>
    request<void>(`/api/console/buckets/${encodeURIComponent(bucketName)}/policy`, {
      method: "DELETE",
    }),
  objects: (bucketName: string, params: URLSearchParams) =>
    request<ListObjectsResult>(
      `/api/console/buckets/${encodeURIComponent(bucketName)}/objects?${params}`
    ),
  objectVersions: (bucketName: string, params: URLSearchParams) =>
    request<ListObjectVersionsResult>(
      `/api/console/buckets/${encodeURIComponent(bucketName)}/objects/versions?${params}`
    ),
  objectDetail: (bucketName: string, key: string, versionId?: string) => {
    const params = new URLSearchParams({ key })
    if (versionId) {
      params.set("versionId", versionId)
    }
    return request<ObjectInfo>(
      `/api/console/buckets/${encodeURIComponent(bucketName)}/objects/detail?${params}`
    )
  },
  deleteObject: (bucketName: string, key: string, versionId?: string) => {
    const params = new URLSearchParams({ key })
    if (versionId) {
      params.set("versionId", versionId)
    }
    return request<void>(
      `/api/console/buckets/${encodeURIComponent(bucketName)}/objects?${params}`,
      { method: "DELETE" }
    )
  },
  copyObject: (bucketName: string, sourceBucket: string, sourceKey: string, destinationKey: string) =>
    request<ObjectInfo>(
      `/api/console/buckets/${encodeURIComponent(bucketName)}/objects/copy`,
      {
        method: "POST",
        body: { sourceBucket, sourceKey, destinationKey },
      }
    ),
  presignUpload: (bucketName: string, key: string) =>
    request<PresignedTransfer>(
      `/api/console/buckets/${encodeURIComponent(bucketName)}/objects/presign-upload`,
      {
        method: "POST",
        body: { key, expiresSeconds: 900 },
      }
    ),
  presignDownload: (bucketName: string, key: string, versionId?: string) =>
    request<PresignedTransfer>(
      `/api/console/buckets/${encodeURIComponent(bucketName)}/objects/presign-download`,
      {
        method: "POST",
        body: { key, expiresSeconds: 900, versionId: versionId ?? null },
      }
    ),
  initiateMultipartUpload: (bucketName: string, key: string, contentType: string) =>
    request<MultipartUpload>(
      `/api/console/buckets/${encodeURIComponent(bucketName)}/objects/multipart/initiate`,
      {
        method: "POST",
        body: {
          key,
          contentType,
          metadata: {},
          cacheControl: null,
          contentDisposition: null,
        },
      }
    ),
  presignMultipartPart: (bucketName: string, key: string, uploadId: string, partNumber: number) =>
    request<PresignedTransfer>(
      `/api/console/buckets/${encodeURIComponent(bucketName)}/objects/multipart/presign-part`,
      {
        method: "POST",
        body: { key, uploadId, partNumber, expiresSeconds: 900 },
      }
    ),
  completeMultipartUpload: (
    bucketName: string,
    key: string,
    uploadId: string,
    parts: CompletedMultipartPart[]
  ) =>
    request<ObjectInfo>(
      `/api/console/buckets/${encodeURIComponent(bucketName)}/objects/multipart/complete`,
      {
        method: "POST",
        body: { key, uploadId, parts },
      }
    ),
  abortMultipartUpload: (bucketName: string, key: string, uploadId: string) =>
    request<void>(
      `/api/console/buckets/${encodeURIComponent(bucketName)}/objects/multipart/abort`,
      {
        method: "POST",
        body: { key, uploadId },
      }
    ),
  accessKeys: () => request<AccessKeyInfo[]>("/api/console/access-keys"),
  createAccessKey: (accessKey?: string) =>
    request<AccessKeySecretResult>("/api/console/access-keys", {
      method: "POST",
      body: { accessKey: accessKey || null },
    }),
  deleteAccessKey: (accessKey: string) =>
    request<void>(`/api/console/access-keys/${encodeURIComponent(accessKey)}`, {
      method: "DELETE",
    }),
  settings: () => request<SystemSettings>("/api/console/settings"),
  updateSettings: (maxUploadSizeBytes: number, publicOrigin?: string | null) =>
    request<SystemSettings>("/api/console/settings", {
      method: "PUT",
      body: { maxUploadSizeBytes, publicOrigin: publicOrigin || null },
    }),
  audit: () => request<AuditEntry[]>("/api/console/audit"),
}

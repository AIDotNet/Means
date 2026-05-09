export type AddressingStyle = "path" | "virtualHosted";

export type FetchLike = (input: string | URL | Request, init?: RequestInit) => Promise<Response>;

export type BodyPayload = BodyInit | null | undefined;

export type ObjectMetadata = Record<string, string>;

const MIN_MULTIPART_PART_SIZE = 5 * 1024 * 1024;
const DEFAULT_MULTIPART_PART_SIZE = 16 * 1024 * 1024;

export interface MeansClientOptions {
  endpoint: string | URL;
  addressingStyle?: AddressingStyle;
  domainSuffix?: string;
  fetch?: FetchLike;
  headers?: HeadersInit;
  signer?: MeansRequestSigner;
}

export interface MeansRequestOptions {
  signal?: AbortSignal;
  headers?: HeadersInit;
}

export interface MeansHttpRequest {
  method: string;
  url: URL;
  headers: Headers;
  body?: BodyPayload;
}

export interface MeansRequestSigner {
  sign(request: MeansHttpRequest): MeansHttpRequest | void | Promise<MeansHttpRequest | void>;
}

export interface BuildMeansUrlOptions {
  endpoint: string | URL;
  addressingStyle?: AddressingStyle;
  domainSuffix?: string;
}

export interface BuildBucketUrlOptions extends BuildMeansUrlOptions {
  bucket: string;
}

export interface BuildObjectUrlOptions extends BuildBucketUrlOptions {
  key: string;
}

export interface BucketInput {
  bucket: string;
}

export interface ObjectInput extends BucketInput {
  key: string;
}

export interface ListObjectsInput extends BucketInput {
  prefix?: string;
  delimiter?: string;
  continuationToken?: string;
  maxKeys?: number;
}

export interface PutObjectInput extends ObjectInput {
  body: BodyPayload;
  contentType?: string;
  metadata?: ObjectMetadata;
  cacheControl?: string;
  contentDisposition?: string;
}

export interface CopyObjectInput extends ObjectInput {
  sourceBucket: string;
  sourceKey: string;
  metadata?: ObjectMetadata;
  cacheControl?: string;
  contentDisposition?: string;
}

export interface InitiateMultipartUploadInput extends ObjectInput {
  contentType?: string;
  metadata?: ObjectMetadata;
  cacheControl?: string;
  contentDisposition?: string;
}

export interface UploadPartInput extends ObjectInput {
  uploadId: string;
  partNumber: number;
  body: BodyPayload;
  contentType?: string;
}

export interface CompletedMultipartPart {
  partNumber: number;
  etag: string;
}

export interface CompleteMultipartUploadInput extends ObjectInput {
  uploadId: string;
  parts: CompletedMultipartPart[];
}

export interface AbortMultipartUploadInput extends ObjectInput {
  uploadId: string;
}

export interface ListPartsInput extends ObjectInput {
  uploadId: string;
}

export interface ListMultipartUploadsInput extends BucketInput {
  prefix?: string;
  keyMarker?: string;
  uploadIdMarker?: string;
  maxUploads?: number;
}

export type MultipartUploadBody = Blob | ArrayBuffer | Uint8Array;

export interface UploadObjectMultipartInput extends ObjectInput {
  body: MultipartUploadBody;
  contentType?: string;
  metadata?: ObjectMetadata;
  cacheControl?: string;
  contentDisposition?: string;
  partSize?: number;
}

export interface PresignedPutOptions extends MeansRequestOptions {
  contentType?: string;
  metadata?: ObjectMetadata;
  cacheControl?: string;
  contentDisposition?: string;
}

export interface BucketInfo {
  name: string;
  creationDate?: Date;
}

export interface ListedObject {
  key: string;
  etag?: string;
  size: number;
  lastModified?: Date;
  contentType?: string;
  storageClass?: string;
}

export interface ListBucketsResult {
  buckets: BucketInfo[];
  response: Response;
  requestId?: string;
}

export interface ListObjectsResult {
  bucket: string;
  prefix?: string;
  delimiter?: string;
  keyCount: number;
  isTruncated: boolean;
  nextContinuationToken?: string;
  objects: ListedObject[];
  commonPrefixes: string[];
  response: Response;
  requestId?: string;
}

export interface OperationResult {
  response: Response;
  requestId?: string;
}

export interface BucketOperationResult extends OperationResult {
  bucket: string;
}

export interface ObjectHeaders {
  etag?: string;
  lastModified?: Date;
  contentLength?: number;
  contentType?: string;
  cacheControl?: string;
  contentDisposition?: string;
  contentEncoding?: string;
  acceptRanges?: string;
  metadata: ObjectMetadata;
  requestId?: string;
}

export interface GetObjectResult extends ObjectHeaders {
  response: Response;
  body: ReadableStream<Uint8Array> | null;
}

export interface HeadObjectResult extends ObjectHeaders {
  response: Response;
}

export interface PutObjectResult extends OperationResult {
  bucket?: string;
  key?: string;
  etag?: string;
  versionId?: string;
}

export interface CopyObjectResult extends OperationResult {
  bucket: string;
  key: string;
  sourceBucket: string;
  sourceKey: string;
  etag?: string;
  lastModified?: Date;
}

export interface MultipartUploadResult extends OperationResult {
  bucket: string;
  key: string;
  uploadId: string;
}

export interface UploadPartResult extends OperationResult {
  bucket: string;
  key: string;
  uploadId: string;
  partNumber: number;
  etag?: string;
}

export interface CompleteMultipartUploadResult extends OperationResult {
  bucket: string;
  key: string;
  location?: string;
  etag?: string;
}

export interface MultipartPart {
  partNumber: number;
  etag?: string;
  size: number;
  lastModified?: Date;
}

export interface ListPartsResult extends OperationResult {
  bucket: string;
  key: string;
  uploadId: string;
  parts: MultipartPart[];
}

export interface MultipartUploadSummary {
  key: string;
  uploadId: string;
  initiated?: Date;
}

export interface ListMultipartUploadsResult extends OperationResult {
  bucket: string;
  prefix?: string;
  isTruncated: boolean;
  nextKeyMarker?: string;
  nextUploadIdMarker?: string;
  uploads: MultipartUploadSummary[];
}

export interface MeansSdkErrorOptions {
  statusCode: number;
  code?: string;
  requestId?: string;
  resource?: string;
  response: Response;
  body?: string;
}

export class MeansSdkError extends Error {
  readonly statusCode: number;
  readonly code?: string;
  readonly requestId?: string;
  readonly resource?: string;
  readonly response: Response;
  readonly body?: string;

  constructor(message: string, options: MeansSdkErrorOptions) {
    super(message);
    this.name = "MeansSdkError";
    this.statusCode = options.statusCode;
    this.code = options.code;
    this.requestId = options.requestId;
    this.resource = options.resource;
    this.response = options.response;
    this.body = options.body;
  }
}

export class MeansClient {
  private readonly endpoint: URL;
  private readonly addressingStyle: AddressingStyle;
  private readonly domainSuffix?: string;
  private readonly fetchImpl: FetchLike;
  private readonly defaultHeaders: Headers;
  private readonly signer?: MeansRequestSigner;

  constructor(options: MeansClientOptions) {
    this.endpoint = normalizeEndpoint(options.endpoint);
    this.addressingStyle = options.addressingStyle ?? "path";
    this.domainSuffix = options.domainSuffix;
    this.defaultHeaders = new Headers(options.headers);
    this.signer = options.signer;

    const fetchImpl = options.fetch ?? globalThis.fetch?.bind(globalThis);
    if (!fetchImpl) {
      throw new Error("A fetch implementation is required in this runtime.");
    }

    this.fetchImpl = fetchImpl;
  }

  async listBuckets(options: MeansRequestOptions = {}): Promise<ListBucketsResult> {
    const response = await this.execute("GET", this.serviceUrl(), { options });
    const xml = await response.text();
    return {
      buckets: parseListBuckets(xml),
      response,
      requestId: getRequestId(response)
    };
  }

  async createBucket(input: BucketInput, options: MeansRequestOptions = {}): Promise<BucketOperationResult> {
    assertBucket(input.bucket);
    const response = await this.execute("PUT", this.bucketUrl(input.bucket), { options });
    return {
      bucket: input.bucket,
      response,
      requestId: getRequestId(response)
    };
  }

  async headBucket(input: BucketInput, options: MeansRequestOptions = {}): Promise<BucketOperationResult> {
    assertBucket(input.bucket);
    const response = await this.execute("HEAD", this.bucketUrl(input.bucket), { options });
    return {
      bucket: input.bucket,
      response,
      requestId: getRequestId(response)
    };
  }

  async deleteBucket(input: BucketInput, options: MeansRequestOptions = {}): Promise<BucketOperationResult> {
    assertBucket(input.bucket);
    const response = await this.execute("DELETE", this.bucketUrl(input.bucket), { options });
    return {
      bucket: input.bucket,
      response,
      requestId: getRequestId(response)
    };
  }

  async listObjects(input: ListObjectsInput, options: MeansRequestOptions = {}): Promise<ListObjectsResult> {
    assertBucket(input.bucket);
    const url = this.bucketUrl(input.bucket);
    url.searchParams.set("list-type", "2");
    appendQuery(url, "prefix", input.prefix);
    appendQuery(url, "delimiter", input.delimiter);
    appendQuery(url, "continuation-token", input.continuationToken);
    appendQuery(url, "max-keys", input.maxKeys?.toString());

    const response = await this.execute("GET", url, { options });
    const xml = await response.text();
    return {
      ...parseListObjects(xml, input.bucket),
      response,
      requestId: getRequestId(response)
    };
  }

  async putObject(input: PutObjectInput, options: MeansRequestOptions = {}): Promise<PutObjectResult> {
    assertBucket(input.bucket);
    assertKey(input.key);

    const headers = objectWriteHeaders(input);
    const response = await this.execute("PUT", this.objectUrl(input.bucket, input.key), {
      body: input.body,
      headers,
      options
    });

    return {
      bucket: input.bucket,
      key: input.key,
      etag: cleanEtag(response.headers.get("etag") ?? undefined),
      versionId: response.headers.get("x-amz-version-id") ?? undefined,
      response,
      requestId: getRequestId(response)
    };
  }

  async getObject(input: ObjectInput, options: MeansRequestOptions = {}): Promise<GetObjectResult> {
    assertBucket(input.bucket);
    assertKey(input.key);
    const response = await this.execute("GET", this.objectUrl(input.bucket, input.key), { options });
    return objectResult(response);
  }

  async headObject(input: ObjectInput, options: MeansRequestOptions = {}): Promise<HeadObjectResult> {
    assertBucket(input.bucket);
    assertKey(input.key);
    const response = await this.execute("HEAD", this.objectUrl(input.bucket, input.key), { options });
    return {
      ...parseObjectHeaders(response),
      response
    };
  }

  async deleteObject(input: ObjectInput, options: MeansRequestOptions = {}): Promise<OperationResult> {
    assertBucket(input.bucket);
    assertKey(input.key);
    const response = await this.execute("DELETE", this.objectUrl(input.bucket, input.key), { options });
    return {
      response,
      requestId: getRequestId(response)
    };
  }

  async copyObject(input: CopyObjectInput, options: MeansRequestOptions = {}): Promise<CopyObjectResult> {
    assertBucket(input.bucket);
    assertKey(input.key);
    assertBucket(input.sourceBucket);
    assertKey(input.sourceKey);

    const headers = objectWriteHeaders(input);
    headers.set("x-amz-copy-source", `/${encodePathSegment(input.sourceBucket)}/${encodeObjectKey(input.sourceKey)}`);

    const response = await this.execute("PUT", this.objectUrl(input.bucket, input.key), {
      headers,
      options
    });
    const xml = await response.text();

    return {
      bucket: input.bucket,
      key: input.key,
      sourceBucket: input.sourceBucket,
      sourceKey: input.sourceKey,
      etag: cleanEtag(readXmlTag(xml, "ETag") ?? response.headers.get("etag") ?? undefined),
      lastModified: parseDate(readXmlTag(xml, "LastModified")),
      response,
      requestId: getRequestId(response)
    };
  }

  async initiateMultipartUpload(
    input: InitiateMultipartUploadInput,
    options: MeansRequestOptions = {}
  ): Promise<MultipartUploadResult> {
    assertBucket(input.bucket);
    assertKey(input.key);
    const url = this.objectUrl(input.bucket, input.key);
    url.searchParams.set("uploads", "");
    const response = await this.execute("POST", url, {
      body: "",
      headers: objectWriteHeaders(input),
      options
    });
    const xml = await response.text();
    return {
      bucket: readXmlTag(xml, "Bucket") ?? input.bucket,
      key: readXmlTag(xml, "Key") ?? input.key,
      uploadId: readXmlTag(xml, "UploadId") ?? "",
      response,
      requestId: getRequestId(response)
    };
  }

  async uploadPart(input: UploadPartInput, options: MeansRequestOptions = {}): Promise<UploadPartResult> {
    assertBucket(input.bucket);
    assertKey(input.key);
    assertUploadId(input.uploadId);
    assertPartNumber(input.partNumber);
    const url = this.objectUrl(input.bucket, input.key);
    url.searchParams.set("partNumber", input.partNumber.toString());
    url.searchParams.set("uploadId", input.uploadId);
    const headers = new Headers();
    if (input.contentType) {
      headers.set("content-type", input.contentType);
    }

    const response = await this.execute("PUT", url, {
      body: input.body,
      headers,
      options
    });

    return {
      bucket: input.bucket,
      key: input.key,
      uploadId: input.uploadId,
      partNumber: input.partNumber,
      etag: cleanEtag(response.headers.get("etag") ?? undefined),
      response,
      requestId: getRequestId(response)
    };
  }

  async completeMultipartUpload(
    input: CompleteMultipartUploadInput,
    options: MeansRequestOptions = {}
  ): Promise<CompleteMultipartUploadResult> {
    assertBucket(input.bucket);
    assertKey(input.key);
    assertUploadId(input.uploadId);
    assertCompletedParts(input.parts);
    const url = this.objectUrl(input.bucket, input.key);
    url.searchParams.set("uploadId", input.uploadId);
    const response = await this.execute("POST", url, {
      body: completeMultipartXml(input.parts),
      headers: { "content-type": "application/xml" },
      options
    });
    const xml = await response.text();
    return {
      bucket: readXmlTag(xml, "Bucket") ?? input.bucket,
      key: readXmlTag(xml, "Key") ?? input.key,
      location: readXmlTag(xml, "Location") ?? undefined,
      etag: cleanEtag(readXmlTag(xml, "ETag") ?? response.headers.get("etag") ?? undefined),
      response,
      requestId: getRequestId(response)
    };
  }

  async abortMultipartUpload(input: AbortMultipartUploadInput, options: MeansRequestOptions = {}): Promise<OperationResult> {
    assertBucket(input.bucket);
    assertKey(input.key);
    assertUploadId(input.uploadId);
    const url = this.objectUrl(input.bucket, input.key);
    url.searchParams.set("uploadId", input.uploadId);
    const response = await this.execute("DELETE", url, { options });
    return {
      response,
      requestId: getRequestId(response)
    };
  }

  async listParts(input: ListPartsInput, options: MeansRequestOptions = {}): Promise<ListPartsResult> {
    assertBucket(input.bucket);
    assertKey(input.key);
    assertUploadId(input.uploadId);
    const url = this.objectUrl(input.bucket, input.key);
    url.searchParams.set("uploadId", input.uploadId);
    const response = await this.execute("GET", url, { options });
    const xml = await response.text();
    return {
      bucket: readXmlTag(xml, "Bucket") ?? input.bucket,
      key: readXmlTag(xml, "Key") ?? input.key,
      uploadId: readXmlTag(xml, "UploadId") ?? input.uploadId,
      parts: parseMultipartParts(xml),
      response,
      requestId: getRequestId(response)
    };
  }

  async listMultipartUploads(
    input: ListMultipartUploadsInput,
    options: MeansRequestOptions = {}
  ): Promise<ListMultipartUploadsResult> {
    assertBucket(input.bucket);
    const url = this.bucketUrl(input.bucket);
    url.searchParams.set("uploads", "");
    appendQuery(url, "prefix", input.prefix);
    appendQuery(url, "key-marker", input.keyMarker);
    appendQuery(url, "upload-id-marker", input.uploadIdMarker);
    appendQuery(url, "max-uploads", input.maxUploads?.toString());
    const response = await this.execute("GET", url, { options });
    const xml = await response.text();
    return {
      bucket: readXmlTag(xml, "Bucket") ?? input.bucket,
      prefix: readXmlTag(xml, "Prefix") ?? undefined,
      isTruncated: (readXmlTag(xml, "IsTruncated") ?? "false").toLowerCase() === "true",
      nextKeyMarker: readXmlTag(xml, "NextKeyMarker") ?? undefined,
      nextUploadIdMarker: readXmlTag(xml, "NextUploadIdMarker") ?? undefined,
      uploads: parseMultipartUploads(xml),
      response,
      requestId: getRequestId(response)
    };
  }

  async uploadObjectMultipart(
    input: UploadObjectMultipartInput,
    options: MeansRequestOptions = {}
  ): Promise<CompleteMultipartUploadResult> {
    assertBucket(input.bucket);
    assertKey(input.key);
    const partSize = input.partSize ?? DEFAULT_MULTIPART_PART_SIZE;
    if (partSize < MIN_MULTIPART_PART_SIZE) {
      throw new RangeError("Multipart part size must be at least 5 MiB.");
    }

    const upload = await this.initiateMultipartUpload({
      bucket: input.bucket,
      key: input.key,
      contentType: input.contentType ?? multipartBodyContentType(input.body),
      metadata: input.metadata,
      cacheControl: input.cacheControl,
      contentDisposition: input.contentDisposition
    }, options);
    const parts: CompletedMultipartPart[] = [];
    try {
      const size = multipartBodySize(input.body);
      let partNumber = 1;
      for (let offset = 0; offset < size; offset += partSize) {
        const part = await this.uploadPart({
          bucket: input.bucket,
          key: input.key,
          uploadId: upload.uploadId,
          partNumber,
          body: multipartBodySlice(input.body, offset, Math.min(offset + partSize, size)),
          contentType: input.contentType ?? multipartBodyContentType(input.body)
        }, options);
        parts.push({ partNumber, etag: part.etag ?? "" });
        partNumber += 1;
      }

      if (parts.length === 0) {
        const part = await this.uploadPart({
          bucket: input.bucket,
          key: input.key,
          uploadId: upload.uploadId,
          partNumber: 1,
          body: new Uint8Array(),
          contentType: input.contentType ?? multipartBodyContentType(input.body)
        }, options);
        parts.push({ partNumber: 1, etag: part.etag ?? "" });
      }

      return await this.completeMultipartUpload({
        bucket: input.bucket,
        key: input.key,
        uploadId: upload.uploadId,
        parts
      }, options);
    } catch (error) {
      await this.abortMultipartUpload({
        bucket: input.bucket,
        key: input.key,
        uploadId: upload.uploadId
      }, options).catch(() => undefined);
      throw error;
    }
  }

  async getObjectFromUrl(url: string | URL, options: MeansRequestOptions = {}): Promise<GetObjectResult> {
    const response = await this.execute("GET", new URL(url.toString()), { options, sign: false });
    return objectResult(response);
  }

  async headObjectFromUrl(url: string | URL, options: MeansRequestOptions = {}): Promise<HeadObjectResult> {
    const response = await this.execute("HEAD", new URL(url.toString()), { options, sign: false });
    return {
      ...parseObjectHeaders(response),
      response
    };
  }

  async putObjectToUrl(
    url: string | URL,
    body: BodyPayload,
    options: PresignedPutOptions = {}
  ): Promise<PutObjectResult> {
    const { contentType, metadata, cacheControl, contentDisposition, ...requestOptions } = options;
    const headers = objectWriteHeaders({
      contentType,
      metadata,
      cacheControl,
      contentDisposition
    });

    const response = await this.execute("PUT", new URL(url.toString()), {
      body,
      headers,
      options: requestOptions,
      sign: false
    });

    return {
      etag: cleanEtag(response.headers.get("etag") ?? undefined),
      versionId: response.headers.get("x-amz-version-id") ?? undefined,
      response,
      requestId: getRequestId(response)
    };
  }

  protected serviceUrl(): URL {
    return buildMeansServiceUrl({ endpoint: this.endpoint });
  }

  protected bucketUrl(bucket: string): URL {
    return buildMeansBucketUrl({
      endpoint: this.endpoint,
      addressingStyle: this.addressingStyle,
      domainSuffix: this.domainSuffix,
      bucket
    });
  }

  protected objectUrl(bucket: string, key: string): URL {
    return buildMeansObjectUrl({
      endpoint: this.endpoint,
      addressingStyle: this.addressingStyle,
      domainSuffix: this.domainSuffix,
      bucket,
      key
    });
  }

  private async execute(
    method: string,
    url: URL,
    request: {
      body?: BodyPayload;
      headers?: HeadersInit;
      options?: MeansRequestOptions;
      sign?: boolean;
    } = {}
  ): Promise<Response> {
    const headers = new Headers(this.defaultHeaders);
    mergeHeaders(headers, request.headers);
    mergeHeaders(headers, request.options?.headers);

    const baseRequest: MeansHttpRequest = {
      method,
      url: new URL(url.toString()),
      headers,
      body: request.body
    };

    const signedRequest = request.sign === false || !this.signer
      ? baseRequest
      : (await this.signer.sign(baseRequest)) ?? baseRequest;

    const init: RequestInit & { duplex?: "half" } = {
      method: signedRequest.method,
      headers: signedRequest.headers,
      body: signedRequest.body == null ? undefined : signedRequest.body,
      signal: request.options?.signal
    };

    if (requiresDuplex(signedRequest.body)) {
      init.duplex = "half";
    }

    const response = await this.fetchImpl(signedRequest.url, init);
    if (!response.ok) {
      await throwSdkError(response);
    }

    return response;
  }
}

export function buildMeansServiceUrl(options: { endpoint: string | URL }): URL {
  const url = normalizeEndpoint(options.endpoint);
  url.search = "";
  url.hash = "";
  return url;
}

export function buildMeansBucketUrl(options: BuildBucketUrlOptions): URL {
  assertBucket(options.bucket);
  const url = normalizeEndpoint(options.endpoint);
  url.search = "";
  url.hash = "";

  if ((options.addressingStyle ?? "path") === "virtualHosted") {
    applyVirtualHost(url, options.bucket, options.domainSuffix);
    url.pathname = normalizeBasePath(url.pathname);
    return url;
  }

  url.pathname = joinUrlPath(url.pathname, encodePathSegment(options.bucket));
  return url;
}

export function buildMeansObjectUrl(options: BuildObjectUrlOptions): URL {
  assertBucket(options.bucket);
  assertKey(options.key);
  const url = normalizeEndpoint(options.endpoint);
  url.search = "";
  url.hash = "";

  if ((options.addressingStyle ?? "path") === "virtualHosted") {
    applyVirtualHost(url, options.bucket, options.domainSuffix);
    url.pathname = joinUrlPath(url.pathname, encodeObjectKey(options.key));
    return url;
  }

  url.pathname = joinUrlPath(url.pathname, encodePathSegment(options.bucket), encodeObjectKey(options.key));
  return url;
}

export async function getObjectFromUrl(
  url: string | URL,
  options: MeansRequestOptions = {}
): Promise<GetObjectResult> {
  const client = clientForPresignedUrl(url);
  return client.getObjectFromUrl(url, options);
}

export async function headObjectFromUrl(
  url: string | URL,
  options: MeansRequestOptions = {}
): Promise<HeadObjectResult> {
  const client = clientForPresignedUrl(url);
  return client.headObjectFromUrl(url, options);
}

export async function putObjectToUrl(
  url: string | URL,
  body: BodyPayload,
  options: PresignedPutOptions = {}
): Promise<PutObjectResult> {
  const client = clientForPresignedUrl(url);
  return client.putObjectToUrl(url, body, options);
}

export function encodePathSegment(value: string): string {
  return encodeRfc3986(value);
}

export function encodeObjectKey(key: string): string {
  return key.split("/").map(encodePathSegment).join("/");
}

function clientForPresignedUrl(url: string | URL): MeansClient {
  const endpoint = new URL(url.toString());
  endpoint.pathname = "/";
  endpoint.search = "";
  endpoint.hash = "";
  return new MeansClient({ endpoint });
}

function objectResult(response: Response): GetObjectResult {
  return {
    ...parseObjectHeaders(response),
    response,
    body: response.body
  };
}

function objectWriteHeaders(input: {
  contentType?: string;
  metadata?: ObjectMetadata;
  cacheControl?: string;
  contentDisposition?: string;
}): Headers {
  const headers = new Headers();
  if (input.contentType) {
    headers.set("content-type", input.contentType);
  }

  if (input.cacheControl) {
    headers.set("cache-control", input.cacheControl);
  }

  if (input.contentDisposition) {
    headers.set("content-disposition", input.contentDisposition);
  }

  for (const [rawName, value] of Object.entries(input.metadata ?? {})) {
    const name = rawName.toLowerCase().startsWith("x-amz-meta-")
      ? rawName.slice("x-amz-meta-".length)
      : rawName;
    headers.set(`x-amz-meta-${name}`, value);
  }

  return headers;
}

function parseObjectHeaders(response: Response): ObjectHeaders {
  const metadata: ObjectMetadata = {};
  response.headers.forEach((value, key) => {
    if (key.toLowerCase().startsWith("x-amz-meta-")) {
      metadata[key.slice("x-amz-meta-".length)] = value;
    }
  });

  return {
    etag: cleanEtag(response.headers.get("etag") ?? undefined),
    lastModified: parseDate(response.headers.get("last-modified") ?? undefined),
    contentLength: parseInteger(response.headers.get("content-length") ?? undefined),
    contentType: response.headers.get("content-type") ?? undefined,
    cacheControl: response.headers.get("cache-control") ?? undefined,
    contentDisposition: response.headers.get("content-disposition") ?? undefined,
    contentEncoding: response.headers.get("content-encoding") ?? undefined,
    acceptRanges: response.headers.get("accept-ranges") ?? undefined,
    metadata,
    requestId: getRequestId(response)
  };
}

function parseListBuckets(xml: string): BucketInfo[] {
  return findXmlElements(xml, "Bucket").map((bucketXml) => ({
    name: readXmlTag(bucketXml, "Name") ?? "",
    creationDate: parseDate(readXmlTag(bucketXml, "CreationDate"))
  })).filter((bucket) => bucket.name.length > 0);
}

function parseListObjects(xml: string, fallbackBucket: string): Omit<ListObjectsResult, "response" | "requestId"> {
  const objects = findXmlElements(xml, "Contents").map((contentsXml) => ({
    key: readXmlTag(contentsXml, "Key") ?? "",
    etag: cleanEtag(readXmlTag(contentsXml, "ETag")),
    size: parseInteger(readXmlTag(contentsXml, "Size")) ?? 0,
    lastModified: parseDate(readXmlTag(contentsXml, "LastModified")),
    storageClass: readXmlTag(contentsXml, "StorageClass") ?? undefined
  })).filter((object) => object.key.length > 0);

  const commonPrefixes = findXmlElements(xml, "CommonPrefixes")
    .map((prefixXml) => readXmlTag(prefixXml, "Prefix") ?? "")
    .filter((prefix) => prefix.length > 0);

  return {
    bucket: readXmlTag(xml, "Name") ?? fallbackBucket,
    prefix: readXmlTag(xml, "Prefix") ?? undefined,
    delimiter: readXmlTag(xml, "Delimiter") ?? undefined,
    keyCount: parseInteger(readXmlTag(xml, "KeyCount")) ?? objects.length + commonPrefixes.length,
    isTruncated: (readXmlTag(xml, "IsTruncated") ?? "false").toLowerCase() === "true",
    nextContinuationToken: readXmlTag(xml, "NextContinuationToken") ?? undefined,
    objects,
    commonPrefixes
  };
}

function parseMultipartParts(xml: string): MultipartPart[] {
  return findXmlElements(xml, "Part").map((partXml) => ({
    partNumber: parseInteger(readXmlTag(partXml, "PartNumber")) ?? 0,
    etag: cleanEtag(readXmlTag(partXml, "ETag")),
    size: parseInteger(readXmlTag(partXml, "Size")) ?? 0,
    lastModified: parseDate(readXmlTag(partXml, "LastModified"))
  })).filter((part) => part.partNumber > 0);
}

function parseMultipartUploads(xml: string): MultipartUploadSummary[] {
  return findXmlElements(xml, "Upload").map((uploadXml) => ({
    key: readXmlTag(uploadXml, "Key") ?? "",
    uploadId: readXmlTag(uploadXml, "UploadId") ?? "",
    initiated: parseDate(readXmlTag(uploadXml, "Initiated"))
  })).filter((upload) => upload.key.length > 0 && upload.uploadId.length > 0);
}

function completeMultipartXml(parts: CompletedMultipartPart[]): string {
  return `<CompleteMultipartUpload>${parts.map((part) =>
    `<Part><PartNumber>${part.partNumber}</PartNumber><ETag>&quot;${escapeXml(cleanEtag(part.etag) ?? part.etag)}&quot;</ETag></Part>`
  ).join("")}</CompleteMultipartUpload>`;
}

function readXmlTag(xml: string, tag: string): string | undefined {
  const match = new RegExp(`<(?:[\\w.-]+:)?${tag}(?:\\s[^>]*)?>([\\s\\S]*?)<\\/(?:[\\w.-]+:)?${tag}>`, "i")
    .exec(xml);
  return match?.[1] == null ? undefined : decodeXml(match[1]);
}

function findXmlElements(xml: string, tag: string): string[] {
  const matches: string[] = [];
  const pattern = new RegExp(`<(?:[\\w.-]+:)?${tag}(?:\\s[^>]*)?>([\\s\\S]*?)<\\/(?:[\\w.-]+:)?${tag}>`, "gi");
  for (let match = pattern.exec(xml); match; match = pattern.exec(xml)) {
    matches.push(match[1] ?? "");
  }

  return matches;
}

function decodeXml(value: string): string {
  return value
    .replace(/<!\[CDATA\[([\s\S]*?)]]>/g, "$1")
    .replace(/&lt;/g, "<")
    .replace(/&gt;/g, ">")
    .replace(/&quot;/g, "\"")
    .replace(/&apos;/g, "'")
    .replace(/&#x([0-9a-f]+);/gi, (_, code: string) => String.fromCodePoint(Number.parseInt(code, 16)))
    .replace(/&#([0-9]+);/g, (_, code: string) => String.fromCodePoint(Number.parseInt(code, 10)))
    .replace(/&amp;/g, "&");
}

function escapeXml(value: string): string {
  return value
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&apos;");
}

function normalizeEndpoint(endpoint: string | URL): URL {
  const url = new URL(endpoint.toString());
  if (!url.pathname.endsWith("/")) {
    url.pathname += "/";
  }

  return url;
}

function applyVirtualHost(url: URL, bucket: string, domainSuffix?: string): void {
  const suffix = domainSuffix ?? inferDomainSuffix(url.hostname);
  url.hostname = `${bucket}.${suffix}`;
}

function inferDomainSuffix(hostname: string): string {
  return hostname.toLowerCase().startsWith("api.")
    ? hostname.slice(4)
    : hostname;
}

function joinUrlPath(basePath: string, ...segments: string[]): string {
  const base = normalizeBasePath(basePath);
  const suffix = segments.filter((segment) => segment.length > 0).join("/");
  return suffix.length === 0 ? base : `${base === "/" ? "" : base}/${suffix}`;
}

function normalizeBasePath(pathname: string): string {
  const trimmed = pathname.replace(/\/+$/g, "");
  return trimmed.length === 0 ? "/" : trimmed;
}

function appendQuery(url: URL, key: string, value: string | undefined): void {
  if (value != null && value.length > 0) {
    url.searchParams.set(key, value);
  }
}

function mergeHeaders(target: Headers, source?: HeadersInit): void {
  if (!source) {
    return;
  }

  new Headers(source).forEach((value, key) => target.set(key, value));
}

function getRequestId(response: Response): string | undefined {
  return response.headers.get("x-amz-request-id")
    ?? response.headers.get("x-amz-id-2")
    ?? undefined;
}

function cleanEtag(value: string | undefined): string | undefined {
  if (!value) {
    return undefined;
  }

  return value.replace(/^"+|"+$/g, "");
}

function parseInteger(value: string | undefined): number | undefined {
  if (value == null || value.length === 0) {
    return undefined;
  }

  const parsed = Number.parseInt(value, 10);
  return Number.isFinite(parsed) ? parsed : undefined;
}

function parseDate(value: string | undefined): Date | undefined {
  if (!value) {
    return undefined;
  }

  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? undefined : date;
}

function assertBucket(bucket: string): void {
  if (!bucket || bucket.trim().length === 0) {
    throw new TypeError("Bucket name is required.");
  }
}

function assertKey(key: string): void {
  if (!key || key.length === 0) {
    throw new TypeError("Object key is required.");
  }
}

function assertUploadId(uploadId: string): void {
  if (!uploadId || uploadId.trim().length === 0) {
    throw new TypeError("Upload id is required.");
  }
}

function assertPartNumber(partNumber: number): void {
  if (!Number.isInteger(partNumber) || partNumber < 1 || partNumber > 10000) {
    throw new RangeError("Part number must be between 1 and 10000.");
  }
}

function assertCompletedParts(parts: CompletedMultipartPart[]): void {
  if (!Array.isArray(parts) || parts.length === 0) {
    throw new TypeError("At least one completed part is required.");
  }

  let previous = 0;
  for (const part of parts) {
    assertPartNumber(part.partNumber);
    if (part.partNumber <= previous) {
      throw new TypeError("Completed parts must be in ascending part number order.");
    }

    if (!part.etag || part.etag.trim().length === 0) {
      throw new TypeError("Completed part ETags are required.");
    }

    previous = part.partNumber;
  }
}

function multipartBodySize(body: MultipartUploadBody): number {
  if (typeof Blob !== "undefined" && body instanceof Blob) {
    return body.size;
  }

  if (body instanceof ArrayBuffer) {
    return body.byteLength;
  }

  return (body as Uint8Array).byteLength;
}

function multipartBodySlice(body: MultipartUploadBody, start: number, end: number): BodyPayload {
  if (typeof Blob !== "undefined" && body instanceof Blob) {
    return body.slice(start, end);
  }

  if (body instanceof ArrayBuffer) {
    return body.slice(start, end);
  }

  return body.slice(start, end);
}

function multipartBodyContentType(body: MultipartUploadBody): string | undefined {
  return typeof Blob !== "undefined" && body instanceof Blob && body.type.length > 0 ? body.type : undefined;
}

function encodeRfc3986(value: string): string {
  return encodeURIComponent(value)
    .replace(/[!'()*]/g, (character) => `%${character.charCodeAt(0).toString(16).toUpperCase()}`)
    .replace(/%7E/g, "~");
}

function requiresDuplex(body: BodyPayload): boolean {
  if (body == null || typeof body !== "object") {
    return false;
  }

  if (typeof ReadableStream !== "undefined" && body instanceof ReadableStream) {
    return false;
  }

  const candidate = body as { pipe?: unknown; [Symbol.asyncIterator]?: unknown };
  return typeof candidate.pipe === "function" || typeof candidate[Symbol.asyncIterator] === "function";
}

async function throwSdkError(response: Response): Promise<never> {
  const body = await response.text().catch(() => "");
  const code = readXmlTag(body, "Code");
  const message = readXmlTag(body, "Message")
    ?? response.statusText
    ?? `Means request failed with status ${response.status}.`;

  throw new MeansSdkError(message, {
    statusCode: response.status,
    code,
    requestId: readXmlTag(body, "RequestId") ?? getRequestId(response),
    resource: readXmlTag(body, "Resource"),
    response,
    body
  });
}

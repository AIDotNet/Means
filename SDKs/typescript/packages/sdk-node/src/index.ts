import {
  MeansClient,
  buildMeansObjectUrl,
  type AddressingStyle,
  type BodyPayload,
  type BuildMeansUrlOptions,
  type BuildObjectUrlOptions,
  type CompleteMultipartUploadResult,
  type MeansClientOptions,
  type MeansHttpRequest,
  type MeansRequestOptions,
  type MeansRequestSigner,
  type PutObjectInput,
  type PutObjectResult,
  type UploadObjectMultipartInput
} from "@means/sdk";
import { createHash, createHmac } from "node:crypto";
import { open } from "node:fs/promises";
import type { Readable } from "node:stream";

export * from "@means/sdk";

export interface MeansCredentials {
  accessKey: string;
  secretKey: string;
}

export interface SigV4SignerOptions {
  credentials: MeansCredentials;
  region?: string;
  service?: string;
  now?: () => Date;
}

export interface MeansNodeClientOptions extends Omit<MeansClientOptions, "signer"> {
  credentials: MeansCredentials;
  region?: string;
  service?: string;
  now?: () => Date;
}

export type NodeBodyPayload = BodyPayload | Readable | AsyncIterable<Uint8Array>;

export interface NodePutObjectInput extends Omit<PutObjectInput, "body"> {
  body: NodeBodyPayload;
}

export interface PresignedObjectInput {
  bucket: string;
  key: string;
  expiresIn?: number;
}

export interface PresignedUploadPartInput extends PresignedObjectInput {
  uploadId: string;
  partNumber: number;
}

export interface NodeUploadFileMultipartInput extends Omit<UploadObjectMultipartInput, "body"> {
  filePath: string;
}

export interface CreatePresignedObjectUrlOptions extends BuildObjectUrlOptions {
  credentials: MeansCredentials;
  region?: string;
  service?: string;
  expiresIn?: number;
  now?: () => Date;
}

export class MeansNodeClient extends MeansClient {
  private readonly sigV4Signer: SigV4Signer;

  constructor(options: MeansNodeClientOptions) {
    const sigV4Signer = new SigV4Signer({
      credentials: options.credentials,
      region: options.region,
      service: options.service,
      now: options.now
    });

    super({
      ...options,
      signer: sigV4Signer
    });

    this.sigV4Signer = sigV4Signer;
  }

  putObject(input: NodePutObjectInput, options?: MeansRequestOptions): Promise<PutObjectResult>;
  putObject(input: PutObjectInput, options?: MeansRequestOptions): Promise<PutObjectResult>;
  putObject(input: NodePutObjectInput | PutObjectInput, options: MeansRequestOptions = {}): Promise<PutObjectResult> {
    return super.putObject(input as PutObjectInput, options);
  }

  createPresignedGetUrl(input: PresignedObjectInput): string {
    return this.sigV4Signer.presign(this.objectUrl(input.bucket, input.key), "GET", input.expiresIn).toString();
  }

  createPresignedPutUrl(input: PresignedObjectInput): string {
    return this.sigV4Signer.presign(this.objectUrl(input.bucket, input.key), "PUT", input.expiresIn).toString();
  }

  createPresignedUploadPartUrl(input: PresignedUploadPartInput): string {
    const url = this.objectUrl(input.bucket, input.key);
    url.searchParams.set("partNumber", input.partNumber.toString());
    url.searchParams.set("uploadId", input.uploadId);
    return this.sigV4Signer.presign(url, "PUT", input.expiresIn).toString();
  }

  uploadBufferMultipart(
    input: UploadObjectMultipartInput,
    options?: MeansRequestOptions
  ): Promise<CompleteMultipartUploadResult> {
    return super.uploadObjectMultipart(input, options);
  }

  async uploadFileMultipart(
    input: NodeUploadFileMultipartInput,
    options: MeansRequestOptions = {}
  ): Promise<CompleteMultipartUploadResult> {
    const partSize = input.partSize ?? 16 * 1024 * 1024;
    if (partSize < 5 * 1024 * 1024) {
      throw new RangeError("Multipart part size must be at least 5 MiB.");
    }

    const upload = await this.initiateMultipartUpload(input, options);
    const parts: Array<{ partNumber: number; etag: string }> = [];
    const file = await open(input.filePath, "r");
    try {
      const stat = await file.stat();
      let offset = 0;
      let partNumber = 1;
      while (offset < stat.size) {
        const toRead = Math.min(partSize, stat.size - offset);
        const buffer = Buffer.allocUnsafe(toRead);
        const { bytesRead } = await file.read(buffer, 0, toRead, offset);
        if (bytesRead === 0) {
          break;
        }

        const part = await this.uploadPart({
          bucket: input.bucket,
          key: input.key,
          uploadId: upload.uploadId,
          partNumber,
          body: bytesRead === buffer.length ? buffer : buffer.subarray(0, bytesRead),
          contentType: input.contentType
        }, options);
        parts.push({ partNumber, etag: part.etag ?? "" });
        offset += bytesRead;
        partNumber += 1;
      }

      if (parts.length === 0) {
        const part = await this.uploadPart({
          bucket: input.bucket,
          key: input.key,
          uploadId: upload.uploadId,
          partNumber: 1,
          body: Buffer.alloc(0),
          contentType: input.contentType
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
    } finally {
      await file.close();
    }
  }
}

export class SigV4Signer implements MeansRequestSigner {
  private readonly credentials: MeansCredentials;
  private readonly region: string;
  private readonly service: string;
  private readonly now: () => Date;

  constructor(options: SigV4SignerOptions) {
    this.credentials = options.credentials;
    this.region = options.region ?? "us-east-1";
    this.service = options.service ?? "s3";
    this.now = options.now ?? (() => new Date());
  }

  sign(request: MeansHttpRequest): MeansHttpRequest {
    const timestamp = this.now();
    const amzDate = formatAmzDate(timestamp);
    const shortDate = formatShortDate(timestamp);
    const headers = new Headers(request.headers);
    headers.set("x-amz-date", amzDate);
    headers.set("x-amz-content-sha256", "UNSIGNED-PAYLOAD");

    const signedHeaders = "host;x-amz-content-sha256;x-amz-date";
    const canonicalRequest = buildCanonicalRequest({
      method: request.method,
      pathname: request.url.pathname,
      searchParams: request.url.searchParams,
      headers: buildHeaderLookup(request.url, headers),
      signedHeaders,
      payloadHash: "UNSIGNED-PAYLOAD",
      includeSignature: true
    });

    const signature = computeSignature({
      secretKey: this.credentials.secretKey,
      accessKey: this.credentials.accessKey,
      date: shortDate,
      region: this.region,
      service: this.service,
      amzDate,
      canonicalRequest
    });

    headers.set(
      "authorization",
      `AWS4-HMAC-SHA256 Credential=${this.credentials.accessKey}/${shortDate}/${this.region}/${this.service}/aws4_request, SignedHeaders=${signedHeaders}, Signature=${signature}`
    );

    return {
      ...request,
      headers
    };
  }

  presign(url: URL, method: "GET" | "PUT", expiresIn = 900): URL {
    const expiresSeconds = normalizeExpires(expiresIn);
    const timestamp = this.now();
    const amzDate = formatAmzDate(timestamp);
    const shortDate = formatShortDate(timestamp);
    const presignedUrl = new URL(url.toString());
    const query = presignedUrl.searchParams;
    query.set("X-Amz-Algorithm", "AWS4-HMAC-SHA256");
    query.set("X-Amz-Credential", `${this.credentials.accessKey}/${shortDate}/${this.region}/${this.service}/aws4_request`);
    query.set("X-Amz-Date", amzDate);
    query.set("X-Amz-Expires", expiresSeconds.toString());
    query.set("X-Amz-SignedHeaders", "host");

    const canonicalRequest = buildCanonicalRequest({
      method,
      pathname: presignedUrl.pathname,
      searchParams: query,
      headers: {
        host: presignedUrl.host.toLowerCase()
      },
      signedHeaders: "host",
      payloadHash: "UNSIGNED-PAYLOAD",
      includeSignature: false
    });

    const signature = computeSignature({
      secretKey: this.credentials.secretKey,
      accessKey: this.credentials.accessKey,
      date: shortDate,
      region: this.region,
      service: this.service,
      amzDate,
      canonicalRequest
    });

    query.set("X-Amz-Signature", signature);
    presignedUrl.search = canonicalQueryString(query, true);
    return presignedUrl;
  }
}

export function createPresignedGetUrl(options: CreatePresignedObjectUrlOptions): string {
  return createPresignedObjectUrl("GET", options);
}

export function createPresignedPutUrl(options: CreatePresignedObjectUrlOptions): string {
  return createPresignedObjectUrl("PUT", options);
}

export function createSigV4Signer(options: SigV4SignerOptions): SigV4Signer {
  return new SigV4Signer(options);
}

export function createMeansNodeClient(options: MeansNodeClientOptions): MeansNodeClient {
  return new MeansNodeClient(options);
}

export function buildSignedObjectUrl(options: BuildObjectUrlOptions): URL {
  return buildMeansObjectUrl(options);
}

export type NodeAddressingOptions = BuildMeansUrlOptions & {
  addressingStyle?: AddressingStyle;
};

function createPresignedObjectUrl(method: "GET" | "PUT", options: CreatePresignedObjectUrlOptions): string {
  const signer = new SigV4Signer({
    credentials: options.credentials,
    region: options.region,
    service: options.service,
    now: options.now
  });

  return signer.presign(buildMeansObjectUrl(options), method, options.expiresIn).toString();
}

function buildCanonicalRequest(input: {
  method: string;
  pathname: string;
  searchParams: URLSearchParams;
  headers: Record<string, string>;
  signedHeaders: string;
  payloadHash: string;
  includeSignature: boolean;
}): string {
  const signedHeaderNames = input.signedHeaders
    .split(";")
    .map((header) => header.trim().toLowerCase())
    .filter((header) => header.length > 0)
    .sort();

  const canonicalHeaders = signedHeaderNames
    .map((header) => `${header}:${normalizeHeaderValue(input.headers[header] ?? "")}\n`)
    .join("");

  return [
    input.method.toUpperCase(),
    canonicalUri(input.pathname),
    canonicalQueryString(input.searchParams, input.includeSignature),
    canonicalHeaders,
    signedHeaderNames.join(";"),
    input.payloadHash
  ].join("\n");
}

function canonicalUri(pathname: string): string {
  if (!pathname) {
    return "/";
  }

  return pathname.split("/").map(awsEncode).join("/");
}

function canonicalQueryString(searchParams: URLSearchParams, includeSignature: boolean): string {
  const pairs: Array<[string, string]> = [];
  searchParams.forEach((value, key) => {
    if (!includeSignature && key === "X-Amz-Signature") {
      return;
    }

    pairs.push([awsEncode(key), awsEncode(value)]);
  });

  return pairs
    .sort(([leftKey, leftValue], [rightKey, rightValue]) => {
      if (leftKey === rightKey) {
        return ordinalCompare(leftValue, rightValue);
      }

      return ordinalCompare(leftKey, rightKey);
    })
    .map(([key, value]) => `${key}=${value}`)
    .join("&");
}

function ordinalCompare(left: string, right: string): number {
  if (left === right) {
    return 0;
  }

  return left < right ? -1 : 1;
}

function buildHeaderLookup(url: URL, headers: Headers): Record<string, string> {
  const lookup: Record<string, string> = {
    host: url.host.toLowerCase()
  };

  headers.forEach((value, key) => {
    lookup[key.toLowerCase()] = value
      .split(",")
      .map((part) => part.trim())
      .filter((part) => part.length > 0)
      .join(",");
  });

  return lookup;
}

function normalizeHeaderValue(value: string): string {
  return value.split(/\s+/g).filter((part) => part.length > 0).join(" ");
}

function computeSignature(input: {
  secretKey: string;
  accessKey: string;
  date: string;
  region: string;
  service: string;
  amzDate: string;
  canonicalRequest: string;
}): string {
  const scope = `${input.date}/${input.region}/${input.service}/aws4_request`;
  const stringToSign = [
    "AWS4-HMAC-SHA256",
    input.amzDate,
    scope,
    sha256Hex(input.canonicalRequest)
  ].join("\n");
  const signingKey = deriveSigningKey(input.secretKey, input.date, input.region, input.service);
  return hmacHex(signingKey, stringToSign);
}

function deriveSigningKey(secretKey: string, date: string, region: string, service: string): Buffer {
  const dateKey = hmac(Buffer.from(`AWS4${secretKey}`, "utf8"), date);
  const regionKey = hmac(dateKey, region);
  const serviceKey = hmac(regionKey, service);
  return hmac(serviceKey, "aws4_request");
}

function sha256Hex(value: string): string {
  return createHash("sha256").update(value, "utf8").digest("hex");
}

function hmac(key: Buffer, value: string): Buffer {
  return createHmac("sha256", key).update(value, "utf8").digest();
}

function hmacHex(key: Buffer, value: string): string {
  return createHmac("sha256", key).update(value, "utf8").digest("hex");
}

function awsEncode(value: string): string {
  return encodeURIComponent(value)
    .replace(/[!'()*]/g, (character) => `%${character.charCodeAt(0).toString(16).toUpperCase()}`)
    .replace(/%7E/g, "~");
}

function formatAmzDate(date: Date): string {
  return date.toISOString().replace(/[:-]|\.\d{3}/g, "");
}

function formatShortDate(date: Date): string {
  return formatAmzDate(date).slice(0, 8);
}

function normalizeExpires(expiresIn: number): number {
  if (!Number.isFinite(expiresIn) || expiresIn <= 0) {
    throw new RangeError("Presigned URL expiration must be a positive number of seconds.");
  }

  return Math.floor(expiresIn);
}

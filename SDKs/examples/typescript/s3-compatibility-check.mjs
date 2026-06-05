import {
  GetObjectCommand,
  HeadBucketCommand,
  ListBucketsCommand,
  S3Client,
} from "@aws-sdk/client-s3";
import { getSignedUrl } from "@aws-sdk/s3-request-presigner";

const endpoint = process.env.MEANS_ENDPOINT ?? "http://localhost:5178/s3/";
const accessKey = process.env.MEANS_ACCESS_KEY ?? "meansadmin";
const secretKey = process.env.MEANS_SECRET_KEY ?? "meansadminsecret";
const region = process.env.AWS_REGION ?? process.env.AWS_DEFAULT_REGION ?? "us-east-1";
const bucket = process.env.MEANS_BUCKET;
const presignKey = process.env.MEANS_PRESIGN_KEY ?? process.env.MEANS_KEY ?? "sdk-preflight.txt";

printConfiguration();

const client = new S3Client({
  endpoint,
  region,
  forcePathStyle: true,
  credentials: {
    accessKeyId: accessKey,
    secretAccessKey: secretKey,
  },
});

try {
  const buckets = await client.send(new ListBucketsCommand({}));
  console.log(`S3 ListBuckets succeeded (${buckets.Buckets?.length ?? 0} bucket(s)).`);

  if (bucket) {
    await client.send(new HeadBucketCommand({ Bucket: bucket }));
    console.log(`S3 HeadBucket succeeded for ${bucket}.`);

    const presignedGetUrl = await getSignedUrl(
      client,
      new GetObjectCommand({ Bucket: bucket, Key: presignKey }),
      { expiresIn: 300 },
    );
    printPresignedUrlCheck(presignedGetUrl);
  } else {
    console.log("Set MEANS_BUCKET to also run signed HeadBucket and presigned URL checks.");
  }
} catch (error) {
  printFailure(error);
  process.exitCode = 1;
}

function printConfiguration() {
  const url = new URL(endpoint);
  console.log("Means S3 compatibility check");
  console.log(`  endpoint: ${endpoint}`);
  console.log(`  region: ${region}`);
  console.log("  forcePathStyle: true");
  console.log(`  path prefix: ${url.pathname}`);
  console.log(`  presign key: ${presignKey}`);
  if (url.pathname === "/" || url.pathname === "") {
    console.log("  note: if your Means data plane is mounted under /s3/, include /s3/ in MEANS_ENDPOINT.");
  }
}

function printPresignedUrlCheck(presignedGetUrl) {
  const endpointUrl = new URL(endpoint);
  const signedUrl = new URL(presignedGetUrl);
  const endpointPrefix = normalizePathPrefix(endpointUrl.pathname);

  console.log(`S3 presigned GET URL generated for ${bucket}/${presignKey} (not fetched).`);
  console.log(`  presigned origin: ${signedUrl.origin}`);
  console.log(`  presigned path: ${signedUrl.pathname}`);

  if (endpointUrl.origin !== signedUrl.origin) {
    console.warn(
      `  warning: presigned origin differs from MEANS_ENDPOINT origin (${endpointUrl.origin}). ` +
        "For browsers and external clients, presign with the public load-balanced origin.",
    );
  }

  if (!endpointPrefix) {
    console.log("  endpoint path prefix preserved: not applicable");
    return;
  }

  const hasEndpointPrefix =
    signedUrl.pathname === endpointPrefix || signedUrl.pathname.startsWith(`${endpointPrefix}/`);
  console.log(`  endpoint path prefix preserved: ${hasEndpointPrefix ? "yes" : "no"}`);

  if (!hasEndpointPrefix) {
    console.warn(
      `  warning: MEANS_ENDPOINT uses path prefix ${endpointPrefix}, but the presigned URL path does not. ` +
        "Keep /s3/ or any gateway prefix in the endpoint used by presigners.",
    );
  }
}

function normalizePathPrefix(pathname) {
  if (!pathname || pathname === "/") {
    return "";
  }

  return pathname.endsWith("/") ? pathname.slice(0, -1) : pathname;
}

function printFailure(error) {
  const status = error?.$metadata?.httpStatusCode;
  const code = error?.name ?? error?.Code ?? "UnknownError";
  console.error(`S3 compatibility check failed: ${code}${status ? ` (${status})` : ""}`);
  if (error?.message) {
    console.error(error.message);
  }

  console.error("Troubleshooting:");
  console.error("- MEANS_ENDPOINT must point to the S3 data plane, for example http://localhost:5178/s3/.");
  console.error("- Keep forcePathStyle enabled for local, path-prefixed, or load-balanced deployments.");
  console.error("- Use the same signing region on clients and presigners, commonly us-east-1.");
  console.error("- Check MEANS_ACCESS_KEY and MEANS_SECRET_KEY against the console access key.");
  console.error("- If presigned URLs fail but ListBuckets works, compare the endpoint path prefix used by the presigner.");
  console.error("- For load-balanced clusters, presign with the public S3 origin and keep the proxy host/path unchanged.");
}

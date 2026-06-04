import {
  HeadBucketCommand,
  ListBucketsCommand,
  S3Client,
} from "@aws-sdk/client-s3";

const endpoint = process.env.MEANS_ENDPOINT ?? "http://localhost:5178/s3/";
const accessKey = process.env.MEANS_ACCESS_KEY ?? "meansadmin";
const secretKey = process.env.MEANS_SECRET_KEY ?? "meansadminsecret";
const region = process.env.AWS_REGION ?? process.env.AWS_DEFAULT_REGION ?? "us-east-1";
const bucket = process.env.MEANS_BUCKET;

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
  } else {
    console.log("Set MEANS_BUCKET to also run a signed HeadBucket check.");
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
  if (url.pathname === "/" || url.pathname === "") {
    console.log("  note: if your Means data plane is mounted under /s3/, include /s3/ in MEANS_ENDPOINT.");
  }
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
}

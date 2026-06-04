import {
  CreateBucketCommand,
  GetObjectCommand,
  HeadObjectCommand,
  ListObjectsV2Command,
  PutObjectCommand,
  S3Client,
} from "@aws-sdk/client-s3";
import { getSignedUrl } from "@aws-sdk/s3-request-presigner";

const endpoint = process.env.MEANS_ENDPOINT ?? "http://localhost:5178/s3/";
const accessKey = process.env.MEANS_ACCESS_KEY ?? "meansadmin";
const secretKey = process.env.MEANS_SECRET_KEY ?? "meansadminsecret";
const bucket = process.env.MEANS_BUCKET ?? "sdk-examples";
const region = process.env.AWS_REGION ?? process.env.AWS_DEFAULT_REGION ?? "us-east-1";

const client = new S3Client({
  endpoint,
  region,
  forcePathStyle: true,
  credentials: {
    accessKeyId: accessKey,
    secretAccessKey: secretKey,
  },
});

await ensureBucket(bucket);

const key = `aws-sdk-js-v3/${new Date().toISOString().replaceAll(":", "-")}.txt`;
await client.send(
  new PutObjectCommand({
    Bucket: bucket,
    Key: key,
    Body: "Hello from the official AWS SDK for JavaScript v3.",
    ContentType: "text/plain",
    Metadata: { example: "aws-sdk-js-v3" },
  }),
);

const head = await client.send(new HeadObjectCommand({ Bucket: bucket, Key: key }));
console.log(`Uploaded ${bucket}/${key}, ETag=${head.ETag}`);

const listed = await client.send(
  new ListObjectsV2Command({
    Bucket: bucket,
    Prefix: "aws-sdk-js-v3/",
  }),
);
console.log(`Found ${(listed.Contents ?? []).length} object(s) under aws-sdk-js-v3/.`);

const object = await client.send(new GetObjectCommand({ Bucket: bucket, Key: key }));
console.log(`Downloaded text: ${await object.Body.transformToString()}`);

const presignedGetUrl = await getSignedUrl(
  client,
  new GetObjectCommand({ Bucket: bucket, Key: key }),
  { expiresIn: 600 },
);
console.log(`Presigned GET URL: ${presignedGetUrl}`);

async function ensureBucket(bucketName) {
  try {
    await client.send(new CreateBucketCommand({ Bucket: bucketName }));
    console.log(`Created bucket ${bucketName}.`);
  } catch (error) {
    if (error?.name !== "BucketAlreadyExists" && error?.name !== "BucketAlreadyOwnedByYou") {
      throw error;
    }
    console.log(`Bucket ${bucketName} already exists.`);
  }
}

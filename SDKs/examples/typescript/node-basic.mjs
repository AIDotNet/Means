import { MeansNodeClient } from "@means/sdk-node";

const endpoint = process.env.MEANS_ENDPOINT ?? "http://localhost:5178/s3/";
const accessKey = process.env.MEANS_ACCESS_KEY ?? "meansadmin";
const secretKey = process.env.MEANS_SECRET_KEY ?? "meansadminsecret";
const bucket = process.env.MEANS_BUCKET ?? "sdk-examples";

const client = new MeansNodeClient({
  endpoint,
  addressingStyle: "path",
  credentials: { accessKey, secretKey },
});

await ensureBucket(bucket);

const key = `node/${new Date().toISOString().replaceAll(":", "-")}.txt`;
await client.putObject({
  bucket,
  key,
  body: "Hello from the Means Node SDK example.",
  contentType: "text/plain",
  metadata: { example: "node-basic" },
});

const listed = await client.listObjects({ bucket, prefix: "node/" });
console.log(`Found ${listed.objects.length} object(s) under node/.`);

const object = await client.getObject({ bucket, key });
console.log(await object.response.text());

const putUrl = client.createPresignedPutUrl({
  bucket,
  key: "incoming/node-presigned.txt",
  expiresIn: 600,
});
const putResponse = await fetch(putUrl, {
  method: "PUT",
  headers: { "Content-Type": "text/plain" },
  body: "Uploaded from Node with a presigned PUT URL.",
});
if (!putResponse.ok) {
  throw new Error(`Presigned PUT failed: ${putResponse.status} ${await putResponse.text()}`);
}

const getUrl = client.createPresignedGetUrl({
  bucket,
  key: "incoming/node-presigned.txt",
  expiresIn: 600,
});
console.log(`Presigned GET URL: ${getUrl}`);

await client.setBucketVersioning({ bucket, status: "Enabled" });
await client.putBucketLifecycle({
  bucket,
  configuration: {
    rules: [
      {
        id: "expire-node-temp",
        status: "Enabled",
        prefix: "tmp/",
        expirationDays: 7,
        noncurrentVersionExpirationDays: 3,
        abortIncompleteMultipartUploadDays: 1,
      },
    ],
  },
});

async function ensureBucket(bucketName) {
  try {
    await client.createBucket({ bucket: bucketName });
    console.log(`Created bucket ${bucketName}.`);
  } catch (error) {
    if (error?.code !== "BucketAlreadyExists" && error?.code !== "BucketAlreadyOwnedByYou") {
      throw error;
    }
    console.log(`Bucket ${bucketName} already exists.`);
  }
}

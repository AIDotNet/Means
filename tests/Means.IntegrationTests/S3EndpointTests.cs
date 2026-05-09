using System.Net;
using System.Security.Cryptography;
using System.Text;
using Means.Protocol.S3;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Means.IntegrationTests;

public sealed class S3EndpointTests
{
    [Fact]
    public async Task BucketAndObjectLifecycleWorksAcrossAddressingStyles()
    {
        await using var factory = new MeansWebApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://api.means.local")
        });

        var credentials = new SigV4SigningCredentials("meansadmin", "meansadminsecret");

        await SendSignedAsync(client, new HttpRequestMessage(HttpMethod.Put, "https://api.means.local/docs"), credentials);

        var body = "hello " + new string('x', 2048);
        var put = new HttpRequestMessage(HttpMethod.Put, "https://api.means.local/docs/hello.txt")
        {
            Content = new StringContent(body, Encoding.UTF8, "text/plain")
        };
        put.Headers.TryAddWithoutValidation("x-amz-meta-origin", "integration");
        var putResponse = await SendSignedAsync(client, put, credentials);
        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);
        Assert.True(putResponse.Headers.ETag is not null);

        var get = new HttpRequestMessage(HttpMethod.Get, "https://docs.means.local/hello.txt");
        var getResponse = await SendSignedAsync(client, get, credentials);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.Equal(body, await getResponse.Content.ReadAsStringAsync());
        Assert.Equal("integration", getResponse.Headers.GetValues("x-amz-meta-origin").Single());

        var ranged = new HttpRequestMessage(HttpMethod.Get, "https://api.means.local/docs/hello.txt");
        ranged.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 4);
        var rangeResponse = await SendSignedAsync(client, ranged, credentials);
        Assert.Equal(HttpStatusCode.PartialContent, rangeResponse.StatusCode);
        Assert.Equal("hello", await rangeResponse.Content.ReadAsStringAsync());
        Assert.False(rangeResponse.Content.Headers.Contains("Content-Encoding"));

        var compressed = new HttpRequestMessage(HttpMethod.Get, "https://api.means.local/docs/hello.txt");
        compressed.Headers.TryAddWithoutValidation("Accept-Encoding", "br");
        var compressedResponse = await SendSignedAsync(client, compressed, credentials);
        Assert.Equal(HttpStatusCode.OK, compressedResponse.StatusCode);
        Assert.True(compressedResponse.Content.Headers.ContentEncoding.Contains("br"));
        Assert.StartsWith("W/\"", compressedResponse.Headers.ETag?.ToString());

        var copy = new HttpRequestMessage(HttpMethod.Put, "https://api.means.local/docs/copy.txt");
        copy.Headers.TryAddWithoutValidation("x-amz-copy-source", "/docs/hello.txt");
        var copyResponse = await SendSignedAsync(client, copy, credentials);
        Assert.Equal(HttpStatusCode.OK, copyResponse.StatusCode);

        var list = new HttpRequestMessage(HttpMethod.Get, "https://api.means.local/docs?list-type=2&prefix=&delimiter=/");
        var listResponse = await SendSignedAsync(client, list, credentials);
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var listXml = await listResponse.Content.ReadAsStringAsync();
        Assert.Contains("<Key>copy.txt</Key>", listXml);
        Assert.Contains("<Key>hello.txt</Key>", listXml);

        var policy = """
            {
              "Version": "2012-10-17",
              "Statement": [
                {
                  "Effect": "Allow",
                  "Principal": "*",
                  "Action": "s3:GetObject",
                  "Resource": "arn:aws:s3:::docs/*"
                }
              ]
            }
            """;
        var putPolicy = new HttpRequestMessage(HttpMethod.Put, "https://api.means.local/docs?policy")
        {
            Content = new StringContent(policy, Encoding.UTF8, "application/json")
        };
        var policyResponse = await SendSignedAsync(client, putPolicy, credentials);
        Assert.Equal(HttpStatusCode.NoContent, policyResponse.StatusCode);

        var anonymous = await client.GetAsync("https://docs.means.local/hello.txt");
        Assert.Equal(HttpStatusCode.OK, anonymous.StatusCode);
        Assert.Equal(body, await anonymous.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PresignedPutAndGetAreAccepted()
    {
        await using var factory = new MeansWebApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://api.means.local")
        });

        var credentials = new SigV4SigningCredentials("meansadmin", "meansadminsecret");
        await SendSignedAsync(client, new HttpRequestMessage(HttpMethod.Put, "https://api.means.local/media"), credentials);

        var putUri = SigV4RequestSigner.Presign(new Uri("https://api.means.local/media/presigned.txt"), HttpMethod.Put, credentials, TimeSpan.FromMinutes(10), now: DateTimeOffset.UtcNow);
        var putResponse = await client.SendAsync(new HttpRequestMessage(HttpMethod.Put, putUri)
        {
            Content = new StringContent("presigned")
        });
        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);

        var getUri = SigV4RequestSigner.Presign(new Uri("https://api.means.local/media/presigned.txt"), HttpMethod.Get, credentials, TimeSpan.FromMinutes(10));
        var getResponse = await client.GetAsync(getUri);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.Equal("presigned", await getResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ValidationAndErrorResponsesUseS3XmlContracts()
    {
        await using var factory = new MeansWebApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://api.means.local")
        });

        var invalidBucket = await client.GetAsync("https://api.means.local/Bad_Name");
        Assert.Equal(HttpStatusCode.BadRequest, invalidBucket.StatusCode);
        Assert.Contains("<Code>InvalidArgument</Code>", await invalidBucket.Content.ReadAsStringAsync());

        var invalidKey = await client.GetAsync("https://api.means.local/valid-bucket/");
        Assert.Equal(HttpStatusCode.BadRequest, invalidKey.StatusCode);
        Assert.Contains("<Code>InvalidArgument</Code>", await invalidKey.Content.ReadAsStringAsync());

        var credentials = new SigV4SigningCredentials("meansadmin", "meansadminsecret");
        await SendSignedAsync(client, new HttpRequestMessage(HttpMethod.Put, "https://api.means.local/ranges"), credentials);
        await SendSignedAsync(client, new HttpRequestMessage(HttpMethod.Put, "https://api.means.local/ranges/file.txt")
        {
            Content = new StringContent("hello", Encoding.UTF8, "text/plain")
        }, credentials);

        var invalidRange = new HttpRequestMessage(HttpMethod.Get, "https://api.means.local/ranges/file.txt");
        invalidRange.Headers.TryAddWithoutValidation("Range", "bytes=999-");
        var invalidRangeResponse = await SendSignedAsync(client, invalidRange, credentials);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, invalidRangeResponse.StatusCode);
        Assert.Equal("bytes */5", invalidRangeResponse.Content.Headers.ContentRange?.ToString());
        Assert.Contains("<Code>InvalidRange</Code>", await invalidRangeResponse.Content.ReadAsStringAsync());

        var missingHead = new HttpRequestMessage(HttpMethod.Head, "https://api.means.local/missing-bucket");
        var missingHeadResponse = await SendSignedAsync(client, missingHead, credentials);
        Assert.Equal(HttpStatusCode.NotFound, missingHeadResponse.StatusCode);
        Assert.Empty(await missingHeadResponse.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task PresignedUrlRejectsWrongMethodAndExcessiveExpiration()
    {
        await using var factory = new MeansWebApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://api.means.local")
        });

        var credentials = new SigV4SigningCredentials("meansadmin", "meansadminsecret");
        await SendSignedAsync(client, new HttpRequestMessage(HttpMethod.Put, "https://api.means.local/presign-rules"), credentials);

        var putOnlyUri = SigV4RequestSigner.Presign(new Uri("https://api.means.local/presign-rules/wrong-method.txt"), HttpMethod.Put, credentials, TimeSpan.FromMinutes(10));
        var wrongMethodResponse = await client.GetAsync(putOnlyUri);
        Assert.Equal(HttpStatusCode.Forbidden, wrongMethodResponse.StatusCode);
        Assert.Contains("<Code>SignatureDoesNotMatch</Code>", await wrongMethodResponse.Content.ReadAsStringAsync());

        var excessiveUri = SigV4RequestSigner.Presign(new Uri("https://api.means.local/presign-rules/too-long.txt"), HttpMethod.Put, credentials, TimeSpan.FromDays(8));
        var excessiveResponse = await client.SendAsync(new HttpRequestMessage(HttpMethod.Put, excessiveUri)
        {
            Content = new StringContent("too long")
        });
        Assert.Equal(HttpStatusCode.Forbidden, excessiveResponse.StatusCode);
        Assert.Contains("<Code>AccessDenied</Code>", await excessiveResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task MultipartUploadLifecycleSupportsSignedRequests()
    {
        await using var factory = new MeansWebApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://api.means.local")
        });

        var credentials = new SigV4SigningCredentials("meansadmin", "meansadminsecret");
        await SendSignedAsync(client, new HttpRequestMessage(HttpMethod.Put, "https://api.means.local/multipart"), credentials);

        var initiate = new HttpRequestMessage(HttpMethod.Post, "https://api.means.local/multipart/big.bin?uploads");
        initiate.Headers.TryAddWithoutValidation("x-amz-meta-origin", "multipart-test");
        initiate.Content = new ByteArrayContent([]);
        initiate.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        var initiateResponse = await SendSignedAsync(client, initiate, credentials);
        Assert.Equal(HttpStatusCode.OK, initiateResponse.StatusCode);
        var uploadId = ReadXmlTag(await initiateResponse.Content.ReadAsStringAsync(), "UploadId");
        Assert.False(string.IsNullOrWhiteSpace(uploadId));

        var firstPart = Encoding.ASCII.GetBytes(new string('a', 5 * 1024 * 1024));
        var secondPart = Encoding.ASCII.GetBytes("tail");
        var firstEtag = await UploadPartAsync(client, credentials, "multipart", "big.bin", uploadId!, 1, firstPart);
        _ = await UploadPartAsync(client, credentials, "multipart", "big.bin", uploadId!, 1, Encoding.ASCII.GetBytes(new string('b', 5 * 1024 * 1024)));
        firstEtag = await UploadPartAsync(client, credentials, "multipart", "big.bin", uploadId!, 1, firstPart);
        var secondEtag = await UploadPartAsync(client, credentials, "multipart", "big.bin", uploadId!, 2, secondPart);

        var listParts = new HttpRequestMessage(HttpMethod.Get, $"https://api.means.local/multipart/big.bin?uploadId={Uri.EscapeDataString(uploadId!)}");
        var listPartsResponse = await SendSignedAsync(client, listParts, credentials);
        Assert.Equal(HttpStatusCode.OK, listPartsResponse.StatusCode);
        var listPartsXml = await listPartsResponse.Content.ReadAsStringAsync();
        Assert.Contains("<PartNumber>1</PartNumber>", listPartsXml);
        Assert.Contains("<PartNumber>2</PartNumber>", listPartsXml);

        var listUploads = new HttpRequestMessage(HttpMethod.Get, "https://api.means.local/multipart?uploads");
        var listUploadsResponse = await SendSignedAsync(client, listUploads, credentials);
        Assert.Equal(HttpStatusCode.OK, listUploadsResponse.StatusCode);
        Assert.Contains(uploadId!, await listUploadsResponse.Content.ReadAsStringAsync());

        var complete = new HttpRequestMessage(HttpMethod.Post, $"https://api.means.local/multipart/big.bin?uploadId={Uri.EscapeDataString(uploadId!)}")
        {
            Content = new StringContent(CompleteMultipartXml((1, firstEtag), (2, secondEtag)), Encoding.UTF8, "application/xml")
        };
        var completeResponse = await SendSignedAsync(client, complete, credentials);
        Assert.Equal(HttpStatusCode.OK, completeResponse.StatusCode);
        var completeXml = await completeResponse.Content.ReadAsStringAsync();
        var multipartEtag = ReadXmlTag(completeXml, "ETag")!.Trim('"');
        Assert.EndsWith("-2", multipartEtag, StringComparison.Ordinal);

        var get = new HttpRequestMessage(HttpMethod.Get, "https://api.means.local/multipart/big.bin");
        var getResponse = await SendSignedAsync(client, get, credentials);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.Equal(firstPart.Length + secondPart.Length, (await getResponse.Content.ReadAsByteArrayAsync()).Length);
        Assert.Equal("multipart-test", getResponse.Headers.GetValues("x-amz-meta-origin").Single());
        Assert.Equal("\"" + multipartEtag + "\"", getResponse.Headers.ETag?.ToString());

        var list = new HttpRequestMessage(HttpMethod.Get, "https://api.means.local/multipart?list-type=2");
        var listResponse = await SendSignedAsync(client, list, credentials);
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var listXml = await listResponse.Content.ReadAsStringAsync();
        Assert.Contains("<Key>big.bin</Key>", listXml);
        Assert.Contains("<ETag>\"" + multipartEtag + "\"</ETag>", listXml);
    }

    [Fact]
    public async Task MultipartUploadRejectsInvalidCompleteRequestsAndSupportsAbort()
    {
        await using var factory = new MeansWebApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://api.means.local")
        });

        var credentials = new SigV4SigningCredentials("meansadmin", "meansadminsecret");
        await SendSignedAsync(client, new HttpRequestMessage(HttpMethod.Put, "https://api.means.local/multipart-errors"), credentials);

        var uploadId = await InitiateMultipartAsync(client, credentials, "multipart-errors", "bad.bin");
        var deleteNonEmptyBucket = new HttpRequestMessage(HttpMethod.Delete, "https://api.means.local/multipart-errors");
        var deleteNonEmptyBucketResponse = await SendSignedAsync(client, deleteNonEmptyBucket, credentials);
        Assert.Equal(HttpStatusCode.Conflict, deleteNonEmptyBucketResponse.StatusCode);
        Assert.Contains("<Code>BucketNotEmpty</Code>", await deleteNonEmptyBucketResponse.Content.ReadAsStringAsync());

        var firstEtag = await UploadPartAsync(client, credentials, "multipart-errors", "bad.bin", uploadId, 1, Encoding.ASCII.GetBytes("small"));
        var secondEtag = await UploadPartAsync(client, credentials, "multipart-errors", "bad.bin", uploadId, 2, Encoding.ASCII.GetBytes("tail"));

        var outOfOrder = await CompleteMultipartAsync(client, credentials, "multipart-errors", "bad.bin", uploadId, (2, secondEtag), (1, firstEtag));
        Assert.Equal(HttpStatusCode.BadRequest, outOfOrder.StatusCode);
        Assert.Contains("<Code>InvalidPartOrder</Code>", await outOfOrder.Content.ReadAsStringAsync());

        var wrongEtag = await CompleteMultipartAsync(client, credentials, "multipart-errors", "bad.bin", uploadId, (1, "00000000000000000000000000000000"), (2, secondEtag));
        Assert.Equal(HttpStatusCode.BadRequest, wrongEtag.StatusCode);
        Assert.Contains("<Code>InvalidPart</Code>", await wrongEtag.Content.ReadAsStringAsync());

        var tooSmall = await CompleteMultipartAsync(client, credentials, "multipart-errors", "bad.bin", uploadId, (1, firstEtag), (2, secondEtag));
        Assert.Equal(HttpStatusCode.BadRequest, tooSmall.StatusCode);
        Assert.Contains("<Code>EntityTooSmall</Code>", await tooSmall.Content.ReadAsStringAsync());

        var abort = new HttpRequestMessage(HttpMethod.Delete, $"https://api.means.local/multipart-errors/bad.bin?uploadId={Uri.EscapeDataString(uploadId)}");
        var abortResponse = await SendSignedAsync(client, abort, credentials);
        Assert.Equal(HttpStatusCode.NoContent, abortResponse.StatusCode);

        var listAfterAbort = new HttpRequestMessage(HttpMethod.Get, $"https://api.means.local/multipart-errors/bad.bin?uploadId={Uri.EscapeDataString(uploadId)}");
        var listAfterAbortResponse = await SendSignedAsync(client, listAfterAbort, credentials);
        Assert.Equal(HttpStatusCode.NotFound, listAfterAbortResponse.StatusCode);
        Assert.Contains("<Code>NoSuchUpload</Code>", await listAfterAbortResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PresignedMultipartPartUrlIncludesUploadQueryInSignature()
    {
        await using var factory = new MeansWebApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://api.means.local")
        });

        var credentials = new SigV4SigningCredentials("meansadmin", "meansadminsecret");
        await SendSignedAsync(client, new HttpRequestMessage(HttpMethod.Put, "https://api.means.local/presigned-multipart"), credentials);
        var uploadId = await InitiateMultipartAsync(client, credentials, "presigned-multipart", "part.bin");

        var signedPart = SigV4RequestSigner.Presign(
            new Uri($"https://api.means.local/presigned-multipart/part.bin?partNumber=1&uploadId={Uri.EscapeDataString(uploadId)}"),
            HttpMethod.Put,
            credentials,
            TimeSpan.FromMinutes(10));
        var putResponse = await client.SendAsync(new HttpRequestMessage(HttpMethod.Put, signedPart)
        {
            Content = new ByteArrayContent(Encoding.ASCII.GetBytes("part"))
        });
        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);

        var tampered = new Uri(signedPart.ToString().Replace("partNumber=1", "partNumber=2", StringComparison.Ordinal));
        var tamperedResponse = await client.SendAsync(new HttpRequestMessage(HttpMethod.Put, tampered)
        {
            Content = new ByteArrayContent(Encoding.ASCII.GetBytes("part"))
        });
        Assert.Equal(HttpStatusCode.Forbidden, tamperedResponse.StatusCode);
        Assert.Contains("<Code>SignatureDoesNotMatch</Code>", await tamperedResponse.Content.ReadAsStringAsync());
    }

    private static async Task<HttpResponseMessage> SendSignedAsync(HttpClient client, HttpRequestMessage request, SigV4SigningCredentials credentials)
    {
        SigV4RequestSigner.Sign(request, credentials, now: new DateTimeOffset(2026, 5, 8, 0, 0, 0, TimeSpan.Zero));
        return await client.SendAsync(request);
    }

    private static async Task<string> InitiateMultipartAsync(HttpClient client, SigV4SigningCredentials credentials, string bucketName, string key)
    {
        var initiate = new HttpRequestMessage(HttpMethod.Post, $"https://api.means.local/{bucketName}/{key}?uploads")
        {
            Content = new ByteArrayContent([])
        };
        var response = await SendSignedAsync(client, initiate, credentials);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return ReadXmlTag(await response.Content.ReadAsStringAsync(), "UploadId")
            ?? throw new InvalidOperationException("Missing UploadId.");
    }

    private static async Task<string> UploadPartAsync(
        HttpClient client,
        SigV4SigningCredentials credentials,
        string bucketName,
        string key,
        string uploadId,
        int partNumber,
        byte[] content)
    {
        var uploadPart = new HttpRequestMessage(
            HttpMethod.Put,
            $"https://api.means.local/{bucketName}/{key}?partNumber={partNumber}&uploadId={Uri.EscapeDataString(uploadId)}")
        {
            Content = new ByteArrayContent(content)
        };
        var response = await SendSignedAsync(client, uploadPart, credentials);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response.Headers.ETag?.Tag.Trim('"') ?? Convert.ToHexString(MD5.HashData(content)).ToLowerInvariant();
    }

    private static async Task<HttpResponseMessage> CompleteMultipartAsync(
        HttpClient client,
        SigV4SigningCredentials credentials,
        string bucketName,
        string key,
        string uploadId,
        params (int PartNumber, string ETag)[] parts)
    {
        var complete = new HttpRequestMessage(HttpMethod.Post, $"https://api.means.local/{bucketName}/{key}?uploadId={Uri.EscapeDataString(uploadId)}")
        {
            Content = new StringContent(CompleteMultipartXml(parts), Encoding.UTF8, "application/xml")
        };
        return await SendSignedAsync(client, complete, credentials);
    }

    private static string CompleteMultipartXml(params (int PartNumber, string ETag)[] parts)
    {
        var body = new StringBuilder("<CompleteMultipartUpload>");
        foreach (var part in parts)
        {
            body.Append("<Part><PartNumber>")
                .Append(part.PartNumber)
                .Append("</PartNumber><ETag>\"")
                .Append(part.ETag.Trim('"'))
                .Append("\"</ETag></Part>");
        }

        body.Append("</CompleteMultipartUpload>");
        return body.ToString();
    }

    private static string? ReadXmlTag(string xml, string tag)
    {
        var start = xml.IndexOf("<" + tag + ">", StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += tag.Length + 2;
        var end = xml.IndexOf("</" + tag + ">", start, StringComparison.Ordinal);
        return end < 0 ? null : xml[start..end];
    }

    private sealed class MeansWebApplicationFactory : WebApplicationFactory<Program>
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "means-tests", Guid.NewGuid().ToString("N"));

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureAppConfiguration(configuration =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Means:Storage:DatabasePath"] = Path.Combine(_root, "means.db"),
                    ["Means:Storage:ObjectsPath"] = Path.Combine(_root, "objects"),
                    ["Means:Storage:DefaultAccessKey"] = "meansadmin",
                    ["Means:Storage:DefaultSecretKey"] = "meansadminsecret",
                    ["Means:S3:ServiceHost"] = "api.means.local",
                    ["Means:S3:DomainSuffix"] = "means.local"
                });
            });

            return base.CreateHost(builder);
        }
    }
}

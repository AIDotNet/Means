using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Means.Core;
using Means.Infrastructure.XlFs;
using Microsoft.Extensions.Options;

namespace Means.UnitTests;

public sealed class XlFsStoreTests
{
    [Fact]
    public async Task LogDbReplaysWalAndTruncatesPartialRecord()
    {
        var root = CreateTempRoot();
        try
        {
            await using (var db = await MeansLogDb.OpenAsync(root, CancellationToken.None))
            {
                await db.PutJsonAsync("b:alpha", "one", CancellationToken.None);
                await db.PutBatchAsync([
                    new LogDbMutation("b:beta", Encoding.UTF8.GetBytes("two"), false),
                    new LogDbMutation("b:deleted", null, true)
                ], CancellationToken.None);
            }

            await File.AppendAllBytesAsync(Path.Combine(root, "current.wal"), [0x01, 0x02, 0x03, 0x04], CancellationToken.None);

            await using var reopened = await MeansLogDb.OpenAsync(root, CancellationToken.None);
            Assert.Equal("one", await reopened.GetJsonAsync<string>("b:alpha", CancellationToken.None));
            Assert.Equal("two", Encoding.UTF8.GetString((await reopened.GetAsync("b:beta", CancellationToken.None))!));
            Assert.Null(await reopened.GetAsync("b:deleted", CancellationToken.None));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task LogDbReplaysLegacyJsonWal()
    {
        var root = CreateTempRoot();
        try
        {
            Directory.CreateDirectory(root);
            var payload = JsonSerializer.SerializeToUtf8Bytes(new[]
            {
                new
                {
                    Key = "b:legacy",
                    Delete = false,
                    Value = Convert.ToBase64String(Encoding.UTF8.GetBytes("old"))
                }
            });
            var header = new byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), 0x4d4c4442);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), payload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), Crc32(payload));
            await File.WriteAllBytesAsync(Path.Combine(root, "current.wal"), header.Concat(payload).ToArray(), CancellationToken.None);

            await using var db = await MeansLogDb.OpenAsync(root, CancellationToken.None);

            Assert.Equal("old", Encoding.UTF8.GetString((await db.GetAsync("b:legacy", CancellationToken.None))!));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task LogDbSnapshotRestoreReplacesCurrentIndex()
    {
        var root = CreateTempRoot();
        var snapshot = Path.Combine(CreateTempRoot(), "snapshot.json");
        try
        {
            await using var db = await MeansLogDb.OpenAsync(root, CancellationToken.None);
            await db.PutJsonAsync("k", "before", CancellationToken.None);
            await db.CreateSnapshotAsync(snapshot, CancellationToken.None);
            await db.PutJsonAsync("k", "after", CancellationToken.None);

            await db.RestoreSnapshotAsync(snapshot, CancellationToken.None);

            Assert.Equal("before", await db.GetJsonAsync<string>("k", CancellationToken.None));
        }
        finally
        {
            DeleteTempRoot(root);
            DeleteTempRoot(Path.GetDirectoryName(snapshot)!);
        }
    }

    [Fact]
    public async Task LogDbPrefixScanSeeksWithinSortedKeyIndex()
    {
        var root = CreateTempRoot();
        try
        {
            await using var db = await MeansLogDb.OpenAsync(root, CancellationToken.None);
            await db.PutBatchAsync([
                new LogDbMutation("a:outside", Encoding.UTF8.GetBytes("0"), false),
                new LogDbMutation("b:001", Encoding.UTF8.GetBytes("1"), false),
                new LogDbMutation("b:002", Encoding.UTF8.GetBytes("2"), false),
                new LogDbMutation("b:003", Encoding.UTF8.GetBytes("3"), false),
                new LogDbMutation("c:outside", Encoding.UTF8.GetBytes("4"), false)
            ], CancellationToken.None);

            var firstPage = await db.ScanPrefixAsync("b:", 2, null, CancellationToken.None);
            Assert.Equal(["b:001", "b:002"], firstPage.Select(pair => pair.Key).ToArray());

            var secondPage = await db.ScanPrefixAsync("b:", 2, firstPage[^1].Key, CancellationToken.None);
            Assert.Equal(["b:003"], secondPage.Select(pair => pair.Key).ToArray());
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task XlFsObjectLifecycleUsesDiskFormatManifestAndLogDb()
    {
        var root = CreateTempRoot();
        try
        {
            await using var store = CreateStore(root);
            await store.CreateBucketAsync("docs", CancellationToken.None);

            var body = "hello xlfs";
            var info = await store.PutObjectAsync(new PutObjectRequest(
                "docs",
                "hello.txt",
                new MemoryStream(Encoding.UTF8.GetBytes(body)),
                "text/plain",
                new Dictionary<string, string> { ["origin"] = "unit" },
                null,
                null), CancellationToken.None);

            Assert.Equal(body.Length, info.ContentLength);
            Assert.True(File.Exists(Path.Combine(root, "disk1", ".means.sys", "format.json")));
            Assert.True(Directory.EnumerateFiles(Path.Combine(root, "disk1", "objects"), "xl.meta", SearchOption.AllDirectories).Any());
            Assert.False(File.Exists(Path.Combine(root, "means.db")));

            await using var data = await store.GetObjectAsync("docs", "hello.txt", CancellationToken.None);
            using var reader = new StreamReader(data.Content, Encoding.UTF8);
            Assert.Equal(body, await reader.ReadToEndAsync());

            var list = await store.ListObjectsAsync("docs", new ListObjectsOptions(null, null, null, 100), CancellationToken.None);
            Assert.Single(list.Objects);
            Assert.Equal("hello.txt", list.Objects[0].Key);

            await store.DeleteObjectAsync("docs", "hello.txt", CancellationToken.None);
            await Assert.ThrowsAsync<MeansException>(() => store.HeadObjectAsync("docs", "hello.txt", CancellationToken.None));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task XlFsMultipartCompleteBuildsMultipartEtagAndCleansUpload()
    {
        var root = CreateTempRoot();
        try
        {
            await using var store = CreateStore(root);
            await store.CreateBucketAsync("media", CancellationToken.None);
            var upload = await store.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest(
                "media",
                "movie.bin",
                "application/octet-stream",
                new Dictionary<string, string>(),
                null,
                null), CancellationToken.None);

            var first = Enumerable.Repeat((byte)'a', 5 * 1024 * 1024).ToArray();
            var second = Encoding.UTF8.GetBytes("tail");
            var part1 = await store.UploadPartAsync(new UploadPartRequest("media", "movie.bin", upload.UploadId, 1, new MemoryStream(first)), CancellationToken.None);
            var part2 = await store.UploadPartAsync(new UploadPartRequest("media", "movie.bin", upload.UploadId, 2, new MemoryStream(second)), CancellationToken.None);

            var info = await store.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest(
                "media",
                "movie.bin",
                upload.UploadId,
                [new CompletedMultipartPart(1, part1.ETag), new CompletedMultipartPart(2, part2.ETag)]), CancellationToken.None);

            Assert.EndsWith("-2", info.ETag);
            Assert.Equal(first.Length + second.Length, info.ContentLength);
            await Assert.ThrowsAsync<MeansException>(() => store.ListPartsAsync("media", "movie.bin", upload.UploadId, 0, 1000, CancellationToken.None));

            await using var data = await store.GetObjectAsync("media", "movie.bin", CancellationToken.None);
            Assert.Equal(first.Length + second.Length, data.Info.ContentLength);
            using var buffer = new MemoryStream();
            await data.Content.CopyToAsync(buffer);
            Assert.Equal(first.Concat(second).ToArray(), buffer.ToArray());

            var manifestPath = Directory.EnumerateFiles(Path.Combine(root, "disk1", "objects"), "xl.meta", SearchOption.AllDirectories).Single();
            var manifest = JsonSerializer.Deserialize<XlObjectManifest>(await File.ReadAllTextAsync(manifestPath));
            Assert.NotNull(manifest);
            Assert.Equal(2, manifest.Parts.Count);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task XlFsMultipartRejectsInvalidPartOrder()
    {
        var root = CreateTempRoot();
        try
        {
            await using var store = CreateStore(root);
            await store.CreateBucketAsync("media", CancellationToken.None);
            var upload = await store.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest(
                "media",
                "bad.bin",
                "application/octet-stream",
                new Dictionary<string, string>(),
                null,
                null), CancellationToken.None);

            var ex = await Assert.ThrowsAsync<MeansException>(() => store.CompleteMultipartUploadAsync(
                new CompleteMultipartUploadRequest(
                    "media",
                    "bad.bin",
                    upload.UploadId,
                    [new CompletedMultipartPart(2, "etag"), new CompletedMultipartPart(1, "etag")]),
                CancellationToken.None));

            Assert.Equal(MeansErrorCodes.InvalidPartOrder, ex.Code);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    private static XlFsStore CreateStore(string root)
    {
        return new XlFsStore(Options.Create(new XlFsOptions
        {
            Backend = XlFsOptions.BackendName,
            DatabasePath = Path.Combine(root, "means.db"),
            ObjectsPath = Path.Combine(root, "objects"),
            Disks =
            [
                Path.Combine(root, "disk1"),
                Path.Combine(root, "disk2"),
                Path.Combine(root, "disk3")
            ],
            DeploymentId = "unit-test",
            SetId = "set-1",
            ErasureDataShards = 2,
            ErasureParityShards = 1,
            WriteQuorum = 2,
            ReadQuorum = 1,
            DefaultAccessKey = "meansadmin",
            DefaultSecretKey = "meansadminsecret"
        }));
    }

    private static string CreateTempRoot()
    {
        return Path.Combine(Path.GetTempPath(), "means-xlfs-tests", Guid.NewGuid().ToString("N"));
    }

    private static void DeleteTempRoot(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xffffffffu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 1) == 1 ? (crc >> 1) ^ 0xedb88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}

namespace Means.ContractTests;

public sealed class SdkSpecTests
{
    [Fact]
    public void MachineContractDeclaresAllRequiredOperations()
    {
        var root = FindRepoRoot();
        var yaml = File.ReadAllText(Path.Combine(root, "SDKs", "spec", "means-sdk-v1.yaml"));

        foreach (var operation in new[]
                 {
                     "listBuckets",
                     "createBucket",
                     "headBucket",
                     "deleteBucket",
                     "listObjects",
                     "putObject",
                     "getObject",
                     "headObject",
                     "deleteObject",
                     "copyObject",
                     "initiateMultipartUpload",
                     "uploadPart",
                     "completeMultipartUpload",
                     "abortMultipartUpload",
                     "listParts",
                     "listMultipartUploads",
                     "createPresignedGetUrl",
                     "createPresignedPutUrl",
                     "createPresignedUploadPartUrl"
                 })
        {
            Assert.Contains(operation + ":", yaml);
        }
    }

    [Fact]
    public void GoldenFixturesExist()
    {
        var root = FindRepoRoot();
        var fixtures = Path.Combine(root, "SDKs", "spec", "fixtures");

        Assert.True(File.Exists(Path.Combine(fixtures, "sigv4-canonical-request.txt")));
        Assert.True(File.Exists(Path.Combine(fixtures, "addressing.json")));
        Assert.True(File.Exists(Path.Combine(fixtures, "list-buckets.xml")));
        Assert.True(File.Exists(Path.Combine(fixtures, "list-objects-v2.xml")));
        Assert.True(File.Exists(Path.Combine(fixtures, "error-access-denied.xml")));
    }

    [Fact]
    public void MachineContractDeclaresNamingAndPresignLimits()
    {
        var root = FindRepoRoot();
        var yaml = File.ReadAllText(Path.Combine(root, "SDKs", "spec", "means-sdk-v1.yaml"));

        Assert.Contains("maxExpiresSeconds: 604800", yaml);
        Assert.Contains("maxUtf8Bytes: 1024", yaml);
        Assert.Contains("rejectIpv4AddressShape: true", yaml);
        Assert.Contains("contentRange: \"bytes */{object-length}\"", yaml);
        Assert.Contains("minimumNonFinalPartBytes: 5242880", yaml);
        Assert.Contains("defaultSdkPartBytes: 16777216", yaml);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Means.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}

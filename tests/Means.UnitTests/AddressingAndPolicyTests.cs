using Means.Core;
using Means.Protocol.S3;
using Microsoft.AspNetCore.Http;

namespace Means.UnitTests;

public sealed class AddressingAndPolicyTests
{
    [Fact]
    public void ResolvesPathStyleAddress()
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("api.means.local");
        context.Request.Path = "/photos/2026/cat.jpg";

        var address = S3AddressResolver.Resolve(context.Request, new S3AddressingOptions());

        Assert.Equal("photos", address.BucketName);
        Assert.Equal("2026/cat.jpg", address.ObjectKey);
        Assert.False(address.IsVirtualHostedStyle);
    }

    [Fact]
    public void ResolvesVirtualHostedStyleAddress()
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("photos.means.local");
        context.Request.Path = "/2026/cat.jpg";

        var address = S3AddressResolver.Resolve(context.Request, new S3AddressingOptions());

        Assert.Equal("photos", address.BucketName);
        Assert.Equal("2026/cat.jpg", address.ObjectKey);
        Assert.True(address.IsVirtualHostedStyle);
    }

    [Fact]
    public void BucketPolicyAllowsAnonymousObjectRead()
    {
        const string policy = """
            {
              "Version": "2012-10-17",
              "Statement": [
                {
                  "Effect": "Allow",
                  "Principal": "*",
                  "Action": "s3:GetObject",
                  "Resource": "arn:aws:s3:::public/*"
                }
              ]
            }
            """;

        var evaluator = new BucketPolicyEvaluator();
        var decision = evaluator.Evaluate(policy, S3Actions.GetObject, "public", "logo.svg", principal: null);

        Assert.Equal(PolicyDecision.Allow, decision);
    }

    [Fact]
    public void BucketPolicyDenyWinsOverAllow()
    {
        const string policy = """
            {
              "Statement": [
                {
                  "Effect": "Allow",
                  "Principal": "*",
                  "Action": "s3:GetObject",
                  "Resource": "arn:aws:s3:::public/*"
                },
                {
                  "Effect": "Deny",
                  "Principal": "*",
                  "Action": "s3:GetObject",
                  "Resource": "arn:aws:s3:::public/private/*"
                }
              ]
            }
            """;

        var evaluator = new BucketPolicyEvaluator();
        var decision = evaluator.Evaluate(policy, S3Actions.GetObject, "public", "private/file.txt", principal: "meansadmin");

        Assert.Equal(PolicyDecision.Deny, decision);
    }

    [Fact]
    public void AccessKeyPolicyAllowsMissingPrincipal()
    {
        const string policy = """
            {
              "Statement": [
                {
                  "Effect": "Allow",
                  "Action": "s3:GetObject",
                  "Resource": "arn:aws:s3:::chats/*"
                }
              ]
            }
            """;

        var evaluator = new BucketPolicyEvaluator();
        var decision = evaluator.Evaluate(
            policy,
            S3Actions.GetObject,
            "chats",
            "thread-1.json",
            principal: "scoped-key",
            PolicyPrincipalMode.AccessKey);

        Assert.Equal(PolicyDecision.Allow, decision);
    }

    [Fact]
    public void AccessKeyPolicyGetObjectOnlyCannotPutObject()
    {
        const string policy = """
            {
              "Statement": [
                {
                  "Effect": "Allow",
                  "Action": "s3:GetObject",
                  "Resource": "arn:aws:s3:::chats/*"
                }
              ]
            }
            """;

        var evaluator = new BucketPolicyEvaluator();
        var decision = evaluator.Evaluate(
            policy,
            S3Actions.PutObject,
            "chats",
            "thread-1.json",
            principal: "scoped-key",
            PolicyPrincipalMode.AccessKey);

        Assert.Equal(PolicyDecision.Neutral, decision);
    }

    [Fact]
    public void AccessKeyPolicyResourceScopeBlocksOtherBuckets()
    {
        const string policy = """
            {
              "Statement": [
                {
                  "Effect": "Allow",
                  "Action": "s3:*",
                  "Resource": ["arn:aws:s3:::chats", "arn:aws:s3:::chats/*"]
                }
              ]
            }
            """;

        var evaluator = new BucketPolicyEvaluator();
        var decision = evaluator.Evaluate(
            policy,
            S3Actions.GetObject,
            "other",
            "file.txt",
            principal: "scoped-key",
            PolicyPrincipalMode.AccessKey);

        Assert.Equal(PolicyDecision.Neutral, decision);
    }

    [Fact]
    public void AccessKeyPolicyDenyWinsOverAllow()
    {
        const string policy = """
            {
              "Statement": [
                {
                  "Effect": "Allow",
                  "Action": "s3:*",
                  "Resource": "arn:aws:s3:::chats/*"
                },
                {
                  "Effect": "Deny",
                  "Action": "s3:DeleteObject",
                  "Resource": "arn:aws:s3:::chats/*"
                }
              ]
            }
            """;

        var evaluator = new BucketPolicyEvaluator();
        var decision = evaluator.Evaluate(
            policy,
            S3Actions.DeleteObject,
            "chats",
            "thread-1.json",
            principal: "scoped-key",
            PolicyPrincipalMode.AccessKey);

        Assert.Equal(PolicyDecision.Deny, decision);
    }

    [Fact]
    public void EmptyAccessKeyPolicyIsNeutral()
    {
        var evaluator = new BucketPolicyEvaluator();
        var decision = evaluator.Evaluate(
            null,
            S3Actions.GetObject,
            "chats",
            "thread-1.json",
            principal: "scoped-key",
            PolicyPrincipalMode.AccessKey);

        Assert.Equal(PolicyDecision.Neutral, decision);
    }

    [Fact]
    public void AccessKeyPolicyMatchesListAllMyBucketsResource()
    {
        const string policy = """
            {
              "Statement": [
                {
                  "Effect": "Allow",
                  "Action": "s3:ListAllMyBuckets",
                  "Resource": "arn:aws:s3:::*"
                }
              ]
            }
            """;

        var evaluator = new BucketPolicyEvaluator();
        var decision = evaluator.Evaluate(
            policy,
            S3Actions.ListAllMyBuckets,
            bucketName: null,
            key: null,
            principal: "scoped-key",
            PolicyPrincipalMode.AccessKey);

        Assert.Equal(PolicyDecision.Allow, decision);
    }

    [Fact]
    public void InvalidPolicyJsonIsDenied()
    {
        var evaluator = new BucketPolicyEvaluator();
        var decision = evaluator.Evaluate(
            "{not-json",
            S3Actions.GetObject,
            "chats",
            "thread-1.json",
            principal: "scoped-key",
            PolicyPrincipalMode.AccessKey);

        Assert.Equal(PolicyDecision.Deny, decision);
    }

    [Fact]
    public void CompressionIsDisabledForRangeRequests()
    {
        var encoding = S3Compression.Negotiate("br, gzip", "text/plain", 4096, hasRangeHeader: true);

        Assert.Null(encoding);
    }

    [Fact]
    public void CompressionPrefersBrotliWhenAvailable()
    {
        var encoding = S3Compression.Negotiate("gzip, br", "application/json", 4096, hasRangeHeader: false);

        Assert.Equal("br", encoding);
    }

    [Theory]
    [InlineData("photos")]
    [InlineData("static-assets")]
    [InlineData("media.example")]
    public void BucketNameValidatorAcceptsDnsCompatibleNames(string bucketName)
    {
        S3NameValidator.ValidateBucketName(bucketName);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("UpperCase")]
    [InlineData("bad_name")]
    [InlineData("bad..dots")]
    [InlineData("192.168.0.1")]
    public void BucketNameValidatorRejectsUnsupportedNames(string bucketName)
    {
        var ex = Assert.Throws<MeansException>(() => S3NameValidator.ValidateBucketName(bucketName));

        Assert.Equal(MeansErrorCodes.InvalidArgument, ex.Code);
    }

    [Fact]
    public void ObjectKeyValidatorRejectsEmptyAndOversizedKeys()
    {
        Assert.Throws<MeansException>(() => S3NameValidator.ValidateObjectKey(""));
        Assert.Throws<MeansException>(() => S3NameValidator.ValidateObjectKey(new string('x', 1025)));
    }
}

param(
    [string]$Endpoint = "http://localhost:5000/s3",
    [string]$Bucket = "means-bench",
    [string]$AccessKey = "meansadmin",
    [string]$SecretKey = "meansadminsecret",
    [int]$ObjectCount = 100,
    [int]$ObjectSizeBytes = 1048576,
    [string]$Region = "us-east-1"
)

$ErrorActionPreference = "Stop"
if (-not (Get-Command aws -ErrorAction SilentlyContinue)) {
    throw "AWS CLI is required for this benchmark."
}

$env:AWS_ACCESS_KEY_ID = $AccessKey
$env:AWS_SECRET_ACCESS_KEY = $SecretKey
$env:AWS_DEFAULT_REGION = $Region

$temp = Join-Path ([System.IO.Path]::GetTempPath()) ("means-bench-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $temp | Out-Null
$payload = Join-Path $temp "payload.bin"
$download = Join-Path $temp "download.bin"
$buffer = New-Object byte[] $ObjectSizeBytes
[System.Random]::Shared.NextBytes($buffer)
[System.IO.File]::WriteAllBytes($payload, $buffer)

function Invoke-AwsS3Api([string[]]$Arguments) {
    & aws --endpoint-url $Endpoint @Arguments | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "aws s3api failed: $($Arguments -join ' ')"
    }
}

try {
    Invoke-AwsS3Api @("s3api", "create-bucket", "--bucket", $Bucket)

    $putWatch = [System.Diagnostics.Stopwatch]::StartNew()
    for ($i = 0; $i -lt $ObjectCount; $i++) {
        Invoke-AwsS3Api @("s3api", "put-object", "--bucket", $Bucket, "--key", "objects/$i.bin", "--body", $payload)
    }
    $putWatch.Stop()

    $listWatch = [System.Diagnostics.Stopwatch]::StartNew()
    Invoke-AwsS3Api @("s3api", "list-objects-v2", "--bucket", $Bucket, "--prefix", "objects/", "--max-keys", "1000")
    $listWatch.Stop()

    $getWatch = [System.Diagnostics.Stopwatch]::StartNew()
    for ($i = 0; $i -lt $ObjectCount; $i++) {
        Invoke-AwsS3Api @("s3api", "get-object", "--bucket", $Bucket, "--key", "objects/$i.bin", $download)
    }
    $getWatch.Stop()

    $totalBytes = [int64]$ObjectCount * [int64]$ObjectSizeBytes
    [pscustomobject]@{
        endpoint = $Endpoint
        bucket = $Bucket
        objectCount = $ObjectCount
        objectSizeBytes = $ObjectSizeBytes
        putSeconds = [math]::Round($putWatch.Elapsed.TotalSeconds, 3)
        getSeconds = [math]::Round($getWatch.Elapsed.TotalSeconds, 3)
        listMilliseconds = [math]::Round($listWatch.Elapsed.TotalMilliseconds, 3)
        putMiBPerSecond = [math]::Round(($totalBytes / 1MB) / [math]::Max($putWatch.Elapsed.TotalSeconds, 0.001), 2)
        getMiBPerSecond = [math]::Round(($totalBytes / 1MB) / [math]::Max($getWatch.Elapsed.TotalSeconds, 0.001), 2)
    } | ConvertTo-Json -Depth 4
}
finally {
    for ($i = 0; $i -lt $ObjectCount; $i++) {
        & aws --endpoint-url $Endpoint s3api delete-object --bucket $Bucket --key "objects/$i.bin" | Out-Null
    }
    & aws --endpoint-url $Endpoint s3api delete-bucket --bucket $Bucket | Out-Null
    Remove-Item -Recurse -Force $temp -ErrorAction SilentlyContinue
}

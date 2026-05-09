param(
    [string]$Endpoint = "http://localhost:5000/s3",
    [string]$Bucket = "means-compat",
    [string]$AccessKey = "meansadmin",
    [string]$SecretKey = "meansadminsecret",
    [string]$Region = "us-east-1"
)

$ErrorActionPreference = "Stop"
$env:AWS_ACCESS_KEY_ID = $AccessKey
$env:AWS_SECRET_ACCESS_KEY = $SecretKey
$env:AWS_DEFAULT_REGION = $Region

$results = [ordered]@{}

function Add-Result($Name, $Status, $Message = "") {
    $results[$Name] = [ordered]@{ status = $Status; message = $Message }
}

if (Get-Command aws -ErrorAction SilentlyContinue) {
    try {
        aws --endpoint-url $Endpoint s3api create-bucket --bucket $Bucket | Out-Null
        aws --endpoint-url $Endpoint s3api put-object --bucket $Bucket --key aws-cli.txt --body $PSCommandPath | Out-Null
        aws --endpoint-url $Endpoint s3api head-object --bucket $Bucket --key aws-cli.txt | Out-Null
        aws --endpoint-url $Endpoint s3api delete-object --bucket $Bucket --key aws-cli.txt | Out-Null
        Add-Result "aws-cli" "passed"
    } catch {
        Add-Result "aws-cli" "failed" $_.Exception.Message
    }
} else {
    Add-Result "aws-cli" "skipped" "aws command not found"
}

if (Get-Command python -ErrorAction SilentlyContinue) {
    $script = @"
import os, sys
try:
    import boto3
except Exception as exc:
    print(f'boto3 unavailable: {exc}')
    sys.exit(2)
s3 = boto3.client('s3', endpoint_url='$Endpoint', aws_access_key_id='$AccessKey', aws_secret_access_key='$SecretKey', region_name='$Region')
s3.put_object(Bucket='$Bucket', Key='boto3.txt', Body=b'boto3')
s3.head_object(Bucket='$Bucket', Key='boto3.txt')
s3.delete_object(Bucket='$Bucket', Key='boto3.txt')
"@
    try {
        $script | python -
        if ($LASTEXITCODE -eq 2) { Add-Result "boto3" "skipped" "boto3 module not installed" }
        elseif ($LASTEXITCODE -eq 0) { Add-Result "boto3" "passed" }
        else { Add-Result "boto3" "failed" "python exited with $LASTEXITCODE" }
    } catch {
        Add-Result "boto3" "failed" $_.Exception.Message
    }
} else {
    Add-Result "boto3" "skipped" "python command not found"
}

foreach ($tool in @("rclone", "mc")) {
    if (Get-Command $tool -ErrorAction SilentlyContinue) {
        Add-Result $tool "available" "Install-specific smoke tests are documented but not auto-configured by this script."
    } else {
        Add-Result $tool "skipped" "$tool command not found"
    }
}

try { aws --endpoint-url $Endpoint s3api delete-bucket --bucket $Bucket | Out-Null } catch {}
$results | ConvertTo-Json -Depth 4

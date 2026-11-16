param(
    [string] $Dotnet = 'dotnet',
    [string] $ServerDll = (Join-Path $PSScriptRoot '../src/ScreenAssistant.Server/bin/Release/net8.0/ScreenAssistant.Server.dll')
)

$ErrorActionPreference = 'Stop'
$ServerDll = (Resolve-Path $ServerDll).Path
$probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$probe.Start()
$port = $probe.LocalEndpoint.Port
$probe.Stop()
$baseUrl = "http://127.0.0.1:$port"
$tokenBytes = [byte[]]::new(32)
[Security.Cryptography.RandomNumberGenerator]::Fill($tokenBytes)
$token = [Convert]::ToHexString($tokenBytes)
$directory = Join-Path ([IO.Path]::GetTempPath()) ('screen-assistant-licensing-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $directory | Out-Null
$db = Join-Path $directory 'licenses.db'

function Start-Server {
    $start = [Diagnostics.ProcessStartInfo]::new($Dotnet)
    $start.ArgumentList.Add($ServerDll)
    $start.ArgumentList.Add('--urls')
    $start.ArgumentList.Add($baseUrl)
    $start.Environment['Hosting__AllowLoopbackHttp'] = 'true'
    $start.Environment['Licensing__DatabasePath'] = $db
    $start.Environment['Licensing__OperatorToken'] = $token
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    [Diagnostics.Process]::Start($start)
}

function Wait-Server($process) {
    for ($i = 0; $i -lt 40; $i++) {
        if ($process.HasExited) { throw "Server exited with code $($process.ExitCode)." }
        try {
            if ((Invoke-WebRequest "$baseUrl/health" -TimeoutSec 2).StatusCode -eq 204) { return }
        } catch {
            Start-Sleep -Milliseconds 150
        }
    }
    throw 'Server did not become ready.'
}

function Request($method, $path, $status, $payload, [switch] $Operator) {
    $headers = @{}
    if ($Operator) { $headers.Authorization = "Bearer $token" }
    $parameters = @{}
    if ($null -ne $payload) {
        $parameters.Body = $payload | ConvertTo-Json -Compress
        $parameters.ContentType = 'application/json'
    }
    $result = Invoke-WebRequest -Uri "$baseUrl$path" -Method $method -Headers $headers -SkipHttpErrorCheck -TimeoutSec 10 @parameters
    if ($result.StatusCode -ne $status) {
        throw "$method $path returned $($result.StatusCode), expected $status."
    }
    if ($result.Content -and $status -eq 200) { $result.Content | ConvertFrom-Json }
}

$device = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
$server = $null
try {
    $server = Start-Server
    Wait-Server $server
    Request POST '/v1/operator/licenses' 401
    Request GET '/v1/operator/analysis/pending' 401
    Request GET '/v1/operator/audit' 401
    Request GET '/v1/operator/audit?after=-1' 400 -Operator
    if (@(Request GET '/v1/operator/analysis/pending' 200 -Operator).Count -ne 0) {
        throw 'New database has unexpected pending reservations.'
    }
    $license = Request POST '/v1/operator/licenses' 200 -Operator
    $activation = @{
        activationCode = $license.activationCode
        publicKey = [Convert]::ToBase64String($device.ExportSubjectPublicKeyInfo())
    }
    $challenge = Request POST '/v1/activation/challenge' 200 $activation
    $proof = @{ challengeId = $challenge.challengeId; signature = [Convert]::ToBase64String([byte[]]::new(64)) }
    Request POST '/v1/activation/complete' 400 $proof
    $prefix = [Text.Encoding]::UTF8.GetBytes("ScreenAssistant/device-activation/v1" + [char]0)
    $message = [byte[]]($prefix + [Convert]::FromBase64String($challenge.nonce))
    $proof.signature = [Convert]::ToBase64String($device.SignData($message,
        [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation))
    $bound = Request POST '/v1/activation/complete' 200 $proof
    if ($bound.licenseId -ne $license.licenseId) { throw 'Wrong license activated.' }
    Request POST '/v1/activation/complete' 400 $proof
    Request POST '/v1/activation/challenge' 400 $activation
    $server.Kill()
    $server.WaitForExit()
    $server.Dispose()
    $server = Start-Server
    Wait-Server $server
    Request POST '/v1/activation/challenge' 400 $activation
    Request DELETE "/v1/operator/licenses/$($license.licenseId)" 204 -Operator
    Request DELETE "/v1/operator/licenses/$($license.licenseId)" 404 -Operator
    $secondLicense = Request POST '/v1/operator/licenses' 200 -Operator
    Add-Type -Path (Join-Path (Split-Path $ServerDll) 'ScreenAssistant.Core.dll')
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $http = [Net.Http.HttpClient]::new($handler)
    try {
        $client = [ScreenAssistant.Core.Licensing.ActivationClient]::new($http)
        $secondId = $client.ActivateAsync([uri]$baseUrl, $secondLicense.activationCode, $device,
            [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        if ($secondId.ToString('N') -ne $secondLicense.licenseId) { throw 'Unexpected client activation receipt.' }
        $cancellation = [Threading.CancellationToken]::None
        if (!$client.VerifyStatusAsync([uri]$baseUrl, $secondId, $device, $cancellation).GetAwaiter().GetResult()) {
            throw 'Active license status was rejected.'
        }
        $initialBalance = $client.ReadAvailableCreditsAsync([uri]$baseUrl, $secondId, $device,
            $cancellation).GetAwaiter().GetResult()
        if ($null -eq $initialBalance -or $initialBalance -ne 0) { throw 'Expected a zero starting balance.' }
        $grantId = [guid]::NewGuid()
        $grantPath = "/v1/operator/licenses/$secondId/credit-grants/$grantId"
        Request PUT $grantPath 401 @{ credits = 20 }
        Request PUT $grantPath 204 @{ credits = 20 } -Operator
        Request PUT $grantPath 204 @{ credits = 20 } -Operator
        Request PUT $grantPath 409 @{ credits = 21 } -Operator
        Request PUT "/v1/operator/licenses/$secondId/credit-grants/$([guid]::NewGuid())" 400 @{ credits = 0 } -Operator
        $balance = $client.ReadAvailableCreditsAsync([uri]$baseUrl, $secondId, $device,
            $cancellation).GetAwaiter().GetResult()
        if ($balance -ne 20) { throw "Expected a balance of 20 credits, received $balance." }
        $other = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
        try {
            if ($client.VerifyStatusAsync([uri]$baseUrl, $secondId, $other, $cancellation).GetAwaiter().GetResult()) {
                throw 'License status was accepted for a different device.'
            }
        } finally { $other.Dispose() }
        $stale = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds() - 180
        $staleSignature = [ScreenAssistant.Core.Licensing.DeviceActivationProof]::SignLicenseStatus($device, $secondId, $stale)
        Request POST '/v1/licenses/status' 401 @{
            licenseId = $secondId
            timestamp = $stale
            signature = [Convert]::ToBase64String($staleSignature)
        }
        Request DELETE "/v1/operator/licenses/$secondId" 204 -Operator
        if ($client.VerifyStatusAsync([uri]$baseUrl, $secondId, $device, $cancellation).GetAwaiter().GetResult()) {
            throw 'Revoked license status remained active.'
        }
    } finally {
        $http.Dispose()
    }
    $audit = @(Request GET '/v1/operator/audit' 200 -Operator)
    if (($audit.action -join ',') -ne 'license-issued,license-revoked,license-issued,credit-granted,license-revoked') {
        throw 'Unexpected operator audit sequence.'
    }
    if ($audit[3].credits -ne 20 -or $audit[3].referenceId -ne $grantId) {
        throw 'Credit grant audit entry did not match the operation.'
    }
    $page = @(Request GET "/v1/operator/audit?after=$($audit[3].id)" 200 -Operator)
    if ($page.Count -ne 1 -or $page[0].id -ne $audit[4].id) { throw 'Audit cursor returned the wrong page.' }
    Write-Output 'License API and client: authorization, activation, replay, restart, credit grants and revocation passed.'
} finally {
    if ($server) {
        if (!$server.HasExited) { $server.Kill(); $server.WaitForExit() }
        $server.Dispose()
    }
    $device.Dispose()
    Get-ChildItem -LiteralPath $directory -File | Remove-Item
    Remove-Item -LiteralPath $directory
}

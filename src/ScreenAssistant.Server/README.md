# License API

Run the server against a private directory with persistent storage. Set
`Licensing__DatabasePath` to an absolute file path in that directory and
`Licensing__OperatorToken` to 32 random bytes encoded as 64 hexadecimal
characters. Set operator credentials through environment configuration or the
hosting secret store. The initial schema is applied on startup.

The server rejects plain HTTP by default. For local development, bind to loopback and explicitly opt in:

```powershell
$env:Hosting__AllowLoopbackHttp = "true"
dotnet run --project src/ScreenAssistant.Server -- --urls http://127.0.0.1:5080
```

- `POST /v1/operator/licenses` with the operator bearer token returns a license ID and an activation code. Give the code to the user through a private channel. The server stores the hash.
- `POST /v1/activation/challenge` takes `activationCode` and a base64 `publicKey` in P-256 SubjectPublicKeyInfo format. It returns the challenge ID, nonce and expiry. The challenge expires after two minutes.
- `POST /v1/activation/complete` takes `challengeId` and a base64 `signature` produced by `DeviceActivationProof.Sign`. The transaction binds the key, invalidates the code and consumes the challenge.
- `DELETE /v1/operator/licenses/{id}` revokes a license. Operator access is required.
- `PUT /v1/operator/licenses/{licenseId}/credit-grants/{grantId}` accepts `credits` as a positive integer. Reuse the same grant ID for retries. Retrying with a different amount returns 409. Credits are pilot limits without a monetary conversion.
- `POST /v1/licenses/status` accepts the license ID, a Unix timestamp and a device signature. A valid active license returns 204 and the balance in `X-Available-Credits`. An invalid proof, a timestamp outside the one-minute window, or a revoked license returns 401. This proof is scoped to this read-only endpoint.
- `GET /health` reports that the process started and initialized its database.

Build the solution in Release and run `pwsh -File scripts/Test-LicenseApi.ps1`
from the repository root to check the HTTP flow against a temporary database.

In the Windows client, open «Активировать лицензию…» in settings, enter the server address and the code, and select the key storage. The client saves the license ID, server address and storage selection locally. The HTTP test also exercises the activation client with an ephemeral key.

Authenticated analysis requests, usage accounting, operator audit logs and production hosting are still required for the pilot.

For a controlled analysis pilot, set `Analysis__ProviderKey`,
`Analysis__Model`, and a positive `Analysis__DailyCreditLimit` on the server. The route remains disabled when both are
absent. `POST /v1/analysis` accepts a license ID, a new operation ID, a
Unix timestamp, a base64 signature, the media type and a base64 frame.
Use `HashAnalysisFrame` and `SignAnalysis` from the core library. PNG
and JPEG up to 8 MiB are accepted. For a long page, send ordered `frames`
with `mediaType` and `bytes` in each entry, and sign `AnalysisFrames.HashSeries`.
The maximum is eight frames and 20 MiB of decoded images. The provider URL and model are chosen
on the server. A successful request spends one pilot credit per frame and returns
the answer. Insufficient credits return 402. The server-wide daily ceiling returns 429 before calling the provider. It counts settled charges and active reserves by reservation date in UTC, across all licenses. A repeated operation
returns 409 without another provider call. An uncertain provider outcome
returns 502 and keeps the reservation pending reconciliation. After activation, desktop periodic analysis and the long-page hotkey use this route. The activation dialog can read the current balance.

### Reconciliation

`GET /v1/operator/analysis/pending` lists up to 100 unsettled reservations
older than two minutes. The list contains identifiers, reserved credits
and reservation time. For reservations created before migration 004,
the time is unavailable.

After verifying the outcome, send `{"actualCredits": 0}` to
`PUT /v1/operator/licenses/{licenseId}/analysis/{operationId}/settlement`
to return the full reserve. To charge, send the confirmed credit amount
up to the reserved amount. The difference returns to the balance.
A successful settlement returns 204. A fresh, missing, or already
settled reservation, or a charge above the reserve, returns 409.
Both operations require the operator bearer token. The database records
the settlement amount, time, and whether the operator settled it.

### Backup and restore

With `Licensing__DatabasePath` set to the live database, run:

```powershell
dotnet ScreenAssistant.Server.dll --backup /private/backups/licenses-2026-11-16.db
```

The command snapshots SQLite, validates integrity and foreign keys, then
moves the copy into place. The target directory must already exist with
restricted access. The command refuses to replace an existing copy.
Keep an encrypted copy off-host. To test restoration, copy the snapshot
to a separate writable directory and start an isolated server using that
copy. Keep provider access disabled during the drill. Check that a known
device can read its expected balance and that unresolved operation IDs
remain available for reconciliation. For a real recovery, stop live traffic
before switching database paths and reconcile activity after the snapshot
before re-enabling provider calls.

### Operator audit

`GET /v1/operator/audit?after=0` returns up to 100 entries in ascending ID
order. Use the last returned ID as `after` for the next page. The route
requires the operator bearer token. It records issuance, revocation,
credit grants and manual settlements from migration 006 onward. Entries
include the action, time, license ID and, where relevant, operation ID
and credits. Secrets, frames and responses are excluded. The current
shared token does not distinguish individual operators.


### HTTPS for a pilot host

Publish the server with `dotnet publish src/ScreenAssistant.Server -c Release -o ./publish/server`
and run `dotnet ScreenAssistant.Server.dll` on a host with the ASP.NET Core 8 runtime.
Configure Kestrel with `Kestrel__Endpoints__Https__Url` (for example,
`https://0.0.0.0:8443`) and `Kestrel__Endpoints__Https__Certificate__Path`.
If the PFX is encrypted, supply `Kestrel__Endpoints__Https__Certificate__Password`
through the deployment secret store. Use a certificate recognized by the
client and set its DNS name as the activation server address. Leave
`Hosting__AllowLoopbackHttp` unset on the pilot host. The local opt-in is
for tests on loopback and is not a reverse proxy configuration.
See [Kestrel endpoint configuration](https://learn.microsoft.com/en-au/aspnet/core/fundamentals/servers/kestrel/endpoints?view=aspnetcore-8.0).

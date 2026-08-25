# ProfileSvr

A profile microservice built with ASP.NET Core (.NET 9) minimal APIs using the **vertical slice architecture** pattern, backed by a **DigitalOcean Managed MySQL** database via EF Core (Pomelo provider).

## Structure

```
src/ProfileSvr/
├── Program.cs                     # Host setup: EF Core, validation, endpoint discovery
├── Common/
│   ├── IEndpoint.cs               # Contract each slice implements to map its route
│   └── EndpointExtensions.cs      # Reflection-based endpoint auto-registration
├── Database/
│   ├── AppDbContext.cs            # EF Core context + profiles table mapping
│   └── DesignTimeDbContextFactory.cs
├── Domain/
│   ├── Profile.cs                 # Profile entity
│   └── Device.cs                  # Device entity
├── Features/
│   ├── Auth/
│   │   ├── Login.cs               # POST   /api/auth/login    (SSO password grant; requires registered deviceId)
│   │   ├── RefreshToken.cs        # POST   /api/auth/refresh  (refresh-token grant)
│   │   ├── Me.cs                  # GET    /api/auth/me 🔒 (reload profile from token, no password)
│   │   ├── InitiatePasswordReset.cs # POST /api/auth/initiate-password-reset 🔒 (email OTP)
│   │   ├── PasswordReset.cs       # POST   /api/auth/password-reset  🔒 (OTP → new password on SSO)
│   │   ├── InitiateDeviceChange.cs # POST  /api/auth/initiate-device-change (credentials → email OTP)
│   │   ├── DeviceChange.cs        # POST   /api/auth/change-device (OTP → new device registered + bound)
│   │   └── PasswordChange.cs      # POST   /api/auth/password-change 🔒 (current → new password on SSO)
│   ├── Onboarding/
│   │   ├── InitiateOnboarding.cs  # POST   /api/onboarding/initiate       (email + device → checks + email OTP)
│   │   ├── VerifyAuth.cs          # POST   /api/onboarding/verify-auth    (OTP → SSO auth created; profile pending)
│   │   ├── InitiatePhoneOtp.cs    # POST   /api/onboarding/initiate-phone   (set phone + WhatsApp OTP)
│   │   ├── CreateProfile.cs       # POST   /api/onboarding/create-profile (names + DOB → profile Active)
│   │   └── InitiateKyc.cs         # POST   /api/onboarding/initiate-kyc 🔒 (BVN/NIN → profile loaded, OTP to KYC phone)
│   ├── Otp/
│   │   └── VerifyOtp.cs           # POST   /api/otp/verify 🔒 (one endpoint; section enum: Email | Phone | Kyc)
│   ├── Profiles/                  # One file per vertical slice
│   │   ├── GetProfile.cs          # GET    /api/profiles/{id}
│   │   ├── ListProfiles.cs        # GET    /api/profiles?page=&pageSize=
│   │   ├── ListProfileDevices.cs  # GET    /api/profiles/devices 🔒 (caller's device history; profile from token)
│   │   ├── SetTransactionPin.cs   # POST   /api/profiles/set-pin 🔒 (first-time PIN; verified during transactions)
│   │   ├── PinChange.cs           # POST   /api/profiles/pin-change 🔒 (current PIN → new PIN)
│   │   ├── InitiatePinReset.cs    # POST   /api/profiles/initiate-pin-reset 🔒 (email OTP)
│   │   ├── PinReset.cs            # POST   /api/profiles/pin-reset 🔒 (OTP → new PIN)
│   │   ├── ListActivities.cs      # GET    /api/profiles/activities 🔒 (caller's audit feed, paged)
│   │   ├── SetPhoneNumber.cs      # PUT    /api/profiles/{id}/phone
│   │   ├── RequestEmailOtp.cs     # POST   /api/profiles/{id}/request-email-otp   (OTP via email)
│   │   └── RequestPhoneOtp.cs     # POST   /api/profiles/{id}/request-phone-otp   (OTP via WhatsApp)
│   └── Devices/
│       └── GetDevice.cs           # GET    /api/devices/{id}  (registration happens only inside OTP-gated flows)
└── Migrations/                    # EF Core migrations (create profiles + devices tables)

tests/ProfileSvr.Tests/
├── UnitTests/                     # OTP + transaction-PIN crypto primitives
├── IntegrationTests/ApiTests.cs   # full flows against the real app (in-memory SQLite)
└── Infrastructure/TestAppFactory.cs # WebApplicationFactory + fakes (SSO, Message Centre, auth)
```

## Tests

```bash
dotnet test
```

The integration suite boots the whole service with an in-memory SQLite database, a fake SSO,
a fake Message Centre that captures OTP codes (so tests can read them like a user reads email),
and a header-driven test auth scheme. Covered: full email onboarding, cooldowns, wrong-OTP and
lockout, login (tokens + profile DTO, wrong password, unknown/foreign device), /me, the phone leg
via the unified /api/otp/verify, the KYC path to tier 1, PIN set/change/reset with guards,
password change/reset, device change with binding history, and the activity feed.

Each slice file is self-contained: request/response contracts, validation, handler, and route mapping live together. Adding a feature means adding one file — no shared service or repository layers to touch.

## Database schema

Table `profiles` (created automatically by migration on startup):

| Column                   | Type          | Notes                    |
|--------------------------|---------------|--------------------------|
| `id`                     | char(36) PK   | GUID                     |
| `status`                 | varchar(16)   | `AuthCreated` (staging) → `Active` (create-profile done) |
| `email_address`          | varchar(320)  | unique index             |
| `phone_number`           | varchar(32)   | unique index             |
| `email_confirmed`        | tinyint(1)    | default false            |
| `phone_number_confirmed` | tinyint(1)    | default false            |
| `first_name`             | varchar(100)  | nullable — set at the complete-profile step |
| `last_name`              | varchar(100)  | nullable                 |
| `middle_name`            | varchar(100)  | nullable                 |
| `date_of_birth`          | date          | nullable                 |
| `gender`                 | varchar(16)   | nullable — from KYC or profile completion |
| `tier`                   | int           | 0 = unverified, 1 = basic KYC passed |
| `cif`                    | varchar(64)   | nullable — core-banking customer id, assigned externally |
| `address`                | varchar(256)  | nullable — from the KYC record |
| `bvn`                    | varchar(11)   | nullable; masked in API responses |
| `nin`                    | varchar(11)   | nullable; masked in API responses |
| `bvn_is_verified`        | tinyint(1)    | default false            |
| `nin_is_verified`        | tinyint(1)    | default false            |
| `transaction_pin_salt`   | varchar(64)   | nullable — per-profile random salt (PBKDF2) |
| `transaction_pin_hash`   | varchar(128)  | nullable — PBKDF2-SHA256 of the 4-digit PIN |
| `has_set_transaction_pin`| tinyint(1)    | default false            |
| `device_id`              | char(36)      | FK → `devices.id`; device that created the profile |
| `device_changed_at_utc`  | datetime(6)   | nullable — set on each OTP-approved device change |
| `created_at_utc`         | datetime(6)   |                          |
| `updated_at_utc`         | datetime(6)   | nullable                 |

Table `devices`:

| Column              | Type          | Notes                                        |
|---------------------|---------------|----------------------------------------------|
| `id`                | char(36) PK   | GUID                                         |
| `device_identifier` | varchar(128)  | unique; client-supplied installation id      |
| `name`              | varchar(128)  |                                              |
| `platform`          | varchar(64)   | e.g. ios, android, web                       |
| `os_name`           | varchar(64)   | nullable — e.g. iOS, Android                 |
| `os_version`        | varchar(64)   | nullable                                     |
| `manufacturer`      | varchar(128)  | nullable                                     |
| `model`             | varchar(128)  | nullable                                     |
| `app_version`       | varchar(32)   | nullable — app build on the device           |
| `created_at_utc`    | datetime(6)   |                                              |
| `updated_at_utc`    | datetime(6)   | nullable — set when metadata is refreshed    |

Table `user_devices` (active profile↔device binding — exclusive on both sides; released rows are history):

| Column            | Type         | Notes                                              |
|-------------------|--------------|----------------------------------------------------|
| `id`              | char(36) PK  | GUID                                               |
| `profile_id`      | char(36)     | FK → `profiles.id`                                 |
| `device_id`       | char(36)     | FK → `devices.id`                                  |
| `linked_at_utc`   | datetime(6)  |                                                    |
| `released_at_utc` | datetime(6)  | null while active; only one active row per profile and per device |

Bindings are created at onboarding verify (profile ↔ onboarding device). Moving to a new device is
built for the **locked-out user** (login from the new device is 403, so no token or profile id exists):
`POST /api/auth/initiate-device-change` `{username, password}` verifies the credentials and emails a
code, then `POST /api/auth/change-device` `{username, retrievalCode, otp, deviceId and/or device}`
verifies it, registers the new device and rebinds — releasing the profile's previous device and, if the new device was mapped to another
profile, releasing it there too (released rows are kept as history). Each change stamps
`device_changed_at_utc`, and responses expose the auto-computed `deviceRecentlyChanged` flag (true while
the latest change is under 24 hours old). Login refuses a device other than the active one (403) —
it never moves a binding itself; it only creates one when the profile has none yet.
`GET /api/profiles/{id}` exposes `activeDeviceId`, `deviceChangedAtUtc` and `deviceRecentlyChanged`,
and `GET /api/profiles/devices` 🔒 lists the caller's own device history (profile resolved from the token).
The active binding's `linked_at_utc` is the **transaction gate timestamp**: profile responses expose
`activeDeviceLinkedAtUtc` and the computed `deviceRecentlyLinked` (true while the binding is under
24 hours old) — a transaction service should apply reduced limits while it is true, covering both
newly added and newly changed devices.

Table `activities` (audit trail of every significant action; read via `GET /api/profiles/activities` 🔒):

| Column           | Type         | Notes                                                                 |
|------------------|--------------|-----------------------------------------------------------------------|
| `id`             | char(36) PK  | GUID                                                                  |
| `profile_id`     | char(36)     | FK → `profiles.id`                                                    |
| `device_id`      | char(36)     | nullable — device the action was performed from                       |
| `type`           | varchar(32)  | `ProfileCreated`, `EmailVerified`, `PhoneNumberSet`, `PhoneVerified`, `ProfileCompleted`, `LoggedIn`, `PasswordChanged`, `PasswordReset`, `PinSet`, `PinChanged`, `PinReset`, `DeviceChanged` |
| `description`    | varchar(256) | human-readable summary                                                |
| `ip_address`     | varchar(45)  | nullable — client IP (first X-Forwarded-For entry when proxied)       |
| `created_at_utc` | datetime(6)  | indexed with profile_id                                               |

Table `otp_codes` (codes are stored as SHA-256 hashes bound to purpose + target, never in plain text):

| Column            | Type         | Notes                                                    |
|-------------------|--------------|----------------------------------------------------------|
| `id`              | char(36) PK  | GUID                                                     |
| `retrieval_code`  | varchar(32)  | unique public reference; client quotes it at verification |
| `purpose`         | varchar(24)  | `Onboarding`, `EmailConfirmation` or `PhoneConfirmation` |
| `profile_id`      | char(36)     | nullable — FK → `profiles.id`; null for onboarding codes until verified |
| `device_id`       | char(36)     | nullable — FK → `devices.id`; device the OTP was issued for |
| `target`          | varchar(320) | the email/phone the code was sent to                     |
| `channel`         | varchar(16)  | `Email` or `WhatsApp`                                    |
| `code_hash`       | varchar(64)  | SHA-256 of `code \| purpose \| target` — verifies only for the exact purpose and owner it was issued for |
| `attempts`        | int          | failed verification attempts (max 5)    |
| `expires_at_utc`  | datetime(6)  | 10 minutes after creation               |
| `consumed_at_utc` | datetime(6)  | set when verified, superseded or locked |
| `created_at_utc`  | datetime(6)  |                                         |

## Configuration — DigitalOcean Managed MySQL

Get the connection details from your DigitalOcean control panel: **Databases → your MySQL cluster → Connection Details** (host, port — usually `25060` — user, password, database). TLS is required by DigitalOcean, so keep `SslMode=Required`.

Set the connection string via environment variable (preferred — keeps the password out of files):

```bash
export PROFILESVR_DB="Server=YOUR-CLUSTER-do-user-XXXX.db.ondigitalocean.com;Port=25060;Database=defaultdb;User=doadmin;Password=YOUR_PASSWORD;SslMode=Required"
```

Alternatively put it in `ConnectionStrings:ProfileDb` via user-secrets:

```bash
dotnet user-secrets init --project src/ProfileSvr
dotnet user-secrets set "ConnectionStrings:ProfileDb" "Server=...;SslMode=Required" --project src/ProfileSvr
```

> If your cluster has **Trusted Sources** enabled, add your machine's IP address in the DigitalOcean control panel or the connection will be rejected.

## Response model

Every endpoint returns the same envelope:

```json
{ "isSuccess": true,  "data": { ... },  "message": null }
{ "isSuccess": false, "data": null,     "message": "why it failed" }
```

HTTP status codes still carry the semantics (200/201/202 on success; 400/404/409/422/429/502/503 on failure).

## Onboarding flow

Profiles are created through onboarding, not directly:

0. **Identity pre-allocation** — `initiate` mints a unique `sourceId` (GUID) on the onboarding OTP.
   It becomes the SSO user's `sourceId` at verify-auth **and** the profile's id — so the JWT's
   `SourceId` claim always resolves the profile directly, and `resume` reclaims the same id
   from the token when rebuilding an interrupted record.
1. **`POST /api/onboarding/initiate`** `{emailAddress, device: {deviceIdentifier, name, platform, ...}}` —
   first-time devices send full device info; known devices can send just `{emailAddress, deviceId}`
   (sending both is fine — the device resolves from either). Re-initiating before the profile is
   created simply resends a fresh OTP (subject to a 60-second cooldown), invalidating earlier codes.
   The service rejects the request if the email already has a profile in the database (409) or is
   already registered on the SSO (409). Otherwise it registers the device (first time only) and
   sends a 6-digit OTP to the email. The OTP record stores its **purpose** (`Onboarding`), the
   **device id**, and the target email.
2. **`POST /api/onboarding/verify-auth`** `{emailAddress, deviceId, retrievalCode, otp, password, username?}` —
   verifies the OTP (must be the same device it was issued to), creates the **SSO auth only**, and
   returns **tokens immediately** (`tokens` + `type: onboarding`) so onboarding continues without a
   separate login call. Every authenticated request derives a `type` claim from the profile status —
   `onboarding` (AuthCreated) reaches **only** the onboarding endpoints; `profile-active` (Active)
   reaches everything **except** onboarding; `temporary` (no profile) only bootstraps resume.
   Crossing the line returns 403
   (`POST /api/users/{clientId}/client/create`, username defaults to the email, `sourceId` = profile id).
   A staging record with `status: AuthCreated` and `emailConfirmed: true` holds the state; the profile
   is not considered created until the final create-profile step.
3. **`POST /api/auth/login`** — with the profile created, the client logs in (password grant) to obtain
   a bearer token; the phone leg below requires it.
4. **`POST /api/onboarding/initiate-phone`** `{phoneNumber, deviceId}` 🔒 — the phone leg: the profile is
   resolved from the device's active binding (an unlinked device → 422). Stores the phone number on the
   profile (unconfirmed, uniqueness-checked) and sends an OTP over **WhatsApp**. No/invalid token → 401.
5. **`POST /api/otp/verify`** `{deviceId, retrievalCode, otp, section: "Phone"}` 🔒 — the single OTP
   verification endpoint; `section` (`Email` | `Phone` | `Kyc`) selects what gets confirmed.
6. **`POST /api/onboarding/create-profile`** `{deviceId, firstName, lastName, middleName?, dateOfBirth}` 🔒 —
   the final step: creates the profile proper (personal details + `status: Active`). Identified **from
   the bearer token** (SourceId claim, email fallback); `deviceId` must match the active binding (403),
   and both `emailConfirmed` and `phoneNumberConfirmed` must be true (422). Flow:
   **CreateAuth (initiate) → VerifyAuth → InitiatePhone → VerifyPhone → CreateProfile.**
7. **Recovery** — an account existing on the SSO but missing here (interrupted verify-auth, or a user
   from another system) cannot re-initiate (409). Instead: log in with the existing password, then
   **POST /api/onboarding/initiate-resume** 🔒 sends an email OTP approving the device, and
   **POST /api/onboarding/resume** 🔒 {retrievalCode, otp, device} verifies it, registers the device
   (the only way a resume device gets registered) and recreates the staging record
   (AuthCreated, email confirmed). Onboarding then continues from the phone leg.
   Idempotent when a record already exists. There is no standalone device-registration endpoint —
   devices are only ever registered inside OTP-protected flows.
   (`PUT /api/profiles/{id}/phone` + `request-phone-otp` + `confirm-phone` remain for changing the number later.)

## Authentication

After onboarding, clients sign in through the service, which fronts the SSO's OAuth2 token endpoint (`/connect/token`):

- **`POST /api/auth/login`** `{username, password, deviceId}` — password grant against the SSO using the
  configured client credentials. The `deviceId` must belong to a device already registered during onboarding
  (unknown → 422); the sign-in is stamped on the device. The response carries `accessToken`, `refreshToken`,
  `idToken`, `tokenType`, `expiresIn`, a `deviceStatus` (`New` — linked at this login ·
  `Existing` — already the active device · `Unlinked` — no profile resolved), plus a **`profile` DTO** (when the username is the
  profile's email): identity (names, gender, dateOfBirth, tier, cif, address, masked bvn/nin), statuses
  (`emailConfirmed`, `phoneNumberConfirmed`, `bvnIsVerified`, `ninIsVerified`, `hasSetTransactionPin`,
  `profileCompleted`), and device state (`activeDeviceId`, `deviceChangedAtUtc`, `deviceRecentlyChanged`).
  Bad credentials → 401.
- **`POST /api/auth/refresh`** `{token}` — exchanges a refresh token for fresh tokens. Invalid/expired → 401.

## KYC onboarding path (alternative to steps 4–6)

After the email is verified and the user is logged in, they can onboard via BVN/NIN instead of
entering details manually:

1. **`POST /api/onboarding/initiate-kyc`** `{deviceId, bvn | nin}` 🔒 — looks the identity up on the
   KYC service, loads the profile (names, date of birth, gender, address) from the record, takes the
   **phone number from the BVN/NIN — never from the client** — and sends the OTP to that phone over
   WhatsApp. The response reveals only a masked phone number.
2. **`POST /api/otp/verify`** `{deviceId, retrievalCode, otp, section: "Kyc"}` 🔒 — confirms the phone,
   marks the submitted BVN/NIN verified, and moves the profile to **tier 1**.

Configure the provider via `Kyc:BaseUrl` (+ optional `Kyc:ApiKey`, sent as `X-Api-Key`); expected
contract is `GET {BaseUrl}/api/kyc/bvn/{bvn}` / `GET {BaseUrl}/api/kyc/nin/{nin}` returning
`{isSuccess, data: {firstName, lastName, middleName, dateOfBirth, gender, phoneNumber, address}}` —
adjust [KycHttpClient](src/ProfileSvr/Common/Kyc/KycHttpClient.cs) if the real provider differs.
**When `Kyc:BaseUrl` is unset, a mock provider** returns deterministic fake identities (numbers
ending `00` simulate not-found) so the flow is testable locally.

## Payload encryption

With `Encryption:Enabled` set, every API request and response body travels as an encrypted envelope:

```json
{ "data": "base64( nonce[12] || AES-256-GCM ciphertext || tag[16] )" }
```

Requests with a JSON body must arrive in this envelope (plaintext → 400); responses — including error
envelopes — are wrapped the same way. `/health`, `/swagger` and `/openapi` stay in plaintext so
monitoring and docs keep working. The key (`Encryption:Key`, base64 32 bytes — `openssl rand -base64 32`)
is shared with the client app; a fresh random nonce is used per message and GCM authentication rejects
any tampered ciphertext. Disabled by default for local development.

## Configuration — SSO (Auth Service)

```json
"Sso": {
  "BaseUrl": "https://sso-dev.digitvanttechnology.com",
  "BearerToken": "optional — sent as Authorization: Bearer {token}",
  "ClientId": "required for onboarding — the SSO client the users are created under"
}
```

## Configuration — Message Centre

OTPs are delivered through your Message Centre service. Configure it in `appsettings.json` or via environment variables:

```json
"MessageCentre": {
  "BaseUrl": "https://your-message-centre.example.com",
  "ApiKey": "optional-api-key-sent-as-X-Api-Key"
}
```

ProfileSvr calls `POST {BaseUrl}/api/messages` with:

```json
{ "channel": "email" | "whatsapp", "to": "recipient", "subject": "email only", "body": "Your verification code is 123456. ..." }
```

If the Message Centre's actual contract differs, adjust [MessageCentreClient](src/ProfileSvr/Common/MessageCentre/MessageCentreClient.cs) — it is the only file that knows the wire format.

**When `MessageCentre:BaseUrl` is not set**, a dev fallback logs the OTP to the console instead of sending it, so the full flow is testable locally.

## Run

```bash
dotnet run --project src/ProfileSvr
```

Pending migrations are applied automatically on startup. **Swagger UI: `/swagger`** (OpenAPI document at `/openapi/v1.json`). Health check: `GET /health`.

## Try it

```bash
# 1. initiate onboarding — first time on this device: full device info
curl -s -X POST http://localhost:5000/api/onboarding/initiate \
  -H 'Content-Type: application/json' \
  -d '{"emailAddress":"jane@example.com","device":{"deviceIdentifier":"install-abc-123","name":"Jane'\''s iPhone","platform":"ios","osName":"iOS","osVersion":"18.2","manufacturer":"Apple","model":"iPhone 16","appVersion":"1.0.0"}}'

#    subsequent onboardings from a known device: just the device id
curl -s -X POST http://localhost:5000/api/onboarding/initiate \
  -H 'Content-Type: application/json' \
  -d '{"emailAddress":"jane@example.com","deviceId":"<device-id>"}'

# 2. verify the emailed code — quote the retrievalCode returned by initiate;
#    creates the SSO user, then the profile (emailConfirmed: true)
curl -s -X POST http://localhost:5000/api/onboarding/verify \
  -H 'Content-Type: application/json' \
  -d '{"emailAddress":"jane@example.com","deviceId":"<device-id>","retrievalCode":"<from-initiate>","otp":"123456","password":"S3cure-Pass!"}'

# 3. add the phone number, then confirm it via the WhatsApp OTP
curl -s -X PUT http://localhost:5000/api/profiles/{id}/phone \
  -H 'Content-Type: application/json' -d '{"phoneNumber":"+15551234567"}'
curl -s -X POST http://localhost:5000/api/profiles/{id}/request-phone-otp
curl -s -X POST http://localhost:5000/api/profiles/{id}/confirm-phone \
  -H 'Content-Type: application/json' -d '{"retrievalCode":"<from-request>","otp":"123456"}'

# get
curl -s http://localhost:5000/api/profiles/{id}

# list
curl -s "http://localhost:5000/api/profiles?page=1&pageSize=20"
```

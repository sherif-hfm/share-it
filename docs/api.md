# HTTP interface

All production requests use HTTPS. Private responses specify `Cache-Control: no-store`; raw text uses `text/plain; charset=utf-8`, files use `application/octet-stream` and attachment disposition.

| Method/path | Authentication | Purpose |
|---|---|---|
| GET `/api/antiforgery` | Anonymous | Establish antiforgery cookie and return request token |
| POST `/api/v1/sessions` | Antiforgery | `{ "minutes": 60 }`; return code, PIN, immutable ID, and expiry; set browser cookie |
| POST `/api/v1/join` | Antiforgery | `{ "code": "w3r-yub", "pin": "0047" }`; grant access and set browser cookie |
| GET `/api/v1/sessions/{code}` | Browser grant or Basic | Manifest containing stable text/file numbers and metadata |
| GET `/api/v1/sessions/{code}/texts/{number}/raw` | Browser grant or Basic | Exact UTF-8 saved content |
| GET `/api/v1/sessions/{code}/files/{number}` | Browser grant or Basic | Download a file |
| POST `/api/v1/sessions/{code}/files` | Browser grant + antiforgery | One streaming multipart file; `X-File-Size` gives expected byte length |
| GET `/health` | Anonymous | Process readiness |

Browser mutations send `X-CSRF-TOKEN`. A new token is fetched after identity changes. Text editing, expiry changes, and session closure use authorized Blazor application-service calls; there is no write-capable curl interface in v1.

Basic credentials are the session code and four-digit PIN. Codes ignore hyphens and letter case; PINs are strings with exactly four ASCII digits. Failed attempts use a generic message. Item numbers are unique within a session and are never reassigned.

The curl dialog provides separate Bash/macOS, PowerShell, and Windows CMD commands. CMD commands use double quotes around URLs; single quotes are literal characters in Command Prompt and can cause curl's port-number parsing error. A CMD file command uses a safe `download-<number>` name when the original name contains Windows-invalid characters or shell variable expansions.

Errors include a machine-readable `code` and human-readable `detail`. Statuses: 400 invalid input/antiforgery, 401 missing or invalid credentials, 403 wrong session/read-only access, 404 unavailable item, 409 edit conflict, 410 ended session for an existing grant, 413 quota exceeded, 429 throttled, 503 capacity exhausted. A closed/expired session presented with Basic returns the same generic 401 as invalid credentials.

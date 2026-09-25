# HTTP interface

All production requests use HTTPS. Private responses specify `Cache-Control: no-store`; raw text uses `text/plain; charset=utf-8`, files use `application/octet-stream` and attachment disposition.

| Method/path | Authentication | Purpose |
|---|---|---|
| GET `/api/antiforgery` | Anonymous | Establish antiforgery cookie and return request token |
| POST `/api/v1/sessions` | Antiforgery | `{ "minutes": 60 }`; return code, PIN, immutable ID, and expiry; set browser cookie |
| POST `/api/v1/sessions/{id}/cancel` | Browser grant + antiforgery | Cancel from the new-session dialog using its immutable ID; close access and request immediate purge; safe to retry after removal |
| POST `/api/v1/join` | Antiforgery | `{ "code": "w3r-yub", "pin": "0047" }`; grant access and set browser cookie |
| GET `/api/v1/sessions/{code}` | Browser grant or Basic | Manifest containing stable text/file numbers and metadata |
| GET `/api/v1/sessions/{code}/texts/{number}/raw` | Browser grant or Basic | Exact UTF-8 saved content |
| GET `/api/v1/sessions/{code}/files/{number}` | Browser grant or Basic | Download a file |
| POST `/api/v1/sessions/{code}/files` | Browser grant + antiforgery | One streaming multipart file; `X-File-Size` gives expected byte length |
| GET `/health` | Anonymous | Process readiness |

Browser mutations send `X-CSRF-TOKEN`. A new token is fetched after identity changes. Text editing, expiry changes, and workspace session closure use authorized Blazor application-service calls. Cancelling the new-session dialog uses HTTP so it sees the browser cookie issued during creation. Basic credentials remain read-only.

Basic credentials are the session code and four-digit PIN. Codes ignore hyphens and letter case; PINs are strings with exactly four ASCII digits. Failed attempts use a generic message. Item numbers are unique within a session and are never reassigned.

The curl dialog provides separate Bash/macOS, PowerShell, and Windows CMD commands. CMD commands use double quotes around URLs; single quotes are literal characters in Command Prompt and can cause curl's port-number parsing error. A CMD file command uses a safe `download-<number>` name when the original name contains Windows-invalid characters or shell variable expansions.

Errors include a machine-readable `code` and human-readable `detail`. Statuses: 400 invalid input/antiforgery, 401 missing or invalid credentials, 403 wrong session/read-only access, 404 unavailable item, 409 edit conflict, 410 ended session for an existing grant, 413 quota exceeded, 429 throttled, 503 capacity exhausted. A closed/expired session presented with Basic returns the same generic 401 as invalid credentials.

## Read-only WebDAV

`GET /{code}/mount.sh` and `GET /{code}/mount.ps1` return public mount helpers as `text/plain; charset=utf-8`, with `Cache-Control: no-store`. Valid codes are normalized without checking session existence; invalid code formats return 400. These endpoints require no authentication and contain no private session data. The scripts prompt locally for the PIN and authenticate through WebDAV. See [the mounting guide](mounting.md) for the Bash and PowerShell one-line commands.

`/dav/{code}/` exposes one session through Basic authentication (code/PIN); browser cookies alone do not authorize it. Use [the mounting guide](mounting.md) to configure rclone with vendor `other`. There is no session directory at `/dav/`.

| Resource | Methods | Result |
|---|---|---|
| Session root, `texts/`, `files/` | OPTIONS, PROPFIND | Read capabilities and virtual collections |
| `texts/{number}-{title}.{format}` | OPTIONS, PROPFIND, GET, HEAD | Exact UTF-8 text, without BOM or newline conversion |
| `files/{number}-{name}` | OPTIONS, PROPFIND, GET, HEAD | Streamed uploaded file |

PROPFIND returns `207 application/xml`, supports Depth 0 and 1, and supports empty-body/allprop, propname, requested properties, and allprop/include. Missing Depth means infinity; infinite collection requests return 403 with `DAV:propfind-finite-depth`. Requests are limited to 64 KiB, with DTDs and external entities prohibited. Unknown XML extensions and their descendants are ignored; unknown requested properties have a separate 404 propstat. Available item properties are `displayname`, `resourcetype`, `getcontentlength`, `getcontenttype`, `getlastmodified`, and `getetag`; collections expose displayname/resourcetype. Collection hrefs end with `/`; URL segments and XML values are escaped separately.

GET/HEAD provide strong ETags, last-modified dates, conditional reads, and single byte ranges (206 or 416). Text validators use the saved version; file validators use SHA-256. Private responses retain `Cache-Control: no-store`, though mounting clients/applications may cache data locally.

Supported methods also evaluate the WebDAV `If` header after authentication and session authorization. ETag conditions use strong comparison, with `Not`, conjunctions within a list, and alternatives across lists. Tagged conditions resolve absolute paths or same-origin URLs against the same authorized session snapshot used for the response; they never fetch external URLs or inspect another session. Unmapped resources have no matching state, and lock tokens never match because locking is unavailable. A false condition returns 412, malformed syntax returns 400, and headers larger than 8 KiB return 431. Future write operations must evaluate conditions atomically with their mutations; this read-only check does not provide write locking.

Authenticated PUT, DELETE, MKCOL, COPY, MOVE, PROPPATCH, POST, and PATCH return 403 without consuming mutation bodies. Unsupported methods, including LOCK/UNLOCK, return 405 with Allow on existing resources. DAV class 1 is advertised; class 2/locking is not. This endpoint does not offer dead-property storage, mutable collections, or a write-capable identity.

Errors use XML under `DAV:error`, with application codes in `urn:share-it`; finite-depth errors use the DAV namespace. Missing/invalid credentials and ended sessions return generic 401 with a Basic challenge, cross-session access returns 403, unavailable items return 404, and throttling returns 429 with Retry-After. DAV has a separate configurable per-IP limit (`Limits:WebDavRequestsPerMinute`, default 600/minute); PIN throttling is shared with existing authentication. Reads revalidate server access on each request; an already-open read may complete after closure.

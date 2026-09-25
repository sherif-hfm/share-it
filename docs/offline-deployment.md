# Private-network deployment without internet

Share-It can run entirely inside a private network. Browsers and curl need access to the Share-It server over the LAN; they do not need internet access. Blazor uses a live connection to that server, so disconnecting from the LAN is different from disconnecting from the internet.

The app serves its own JavaScript, CSS, fonts, icons, and Blazor framework. Authentication, session codes/PINs, SQLite, file storage, and expiration cleanup run locally. There are no required external accounts, CDN assets, analytics services, or cloud APIs.

Use `deploy/offline/compose.yml` for this deployment. It runs Production with HTTPS, persists all data and keys, and disables image pulls. The app is confined to an internal Docker network. Caddy also joins a separate bridge so LAN clients can reach its published HTTPS port; the server/network firewall can deny internet egress while allowing LAN clients. DNS forwarding is restricted to container loopback; Docker's local service discovery still resolves the app. Caddy uses its own certificate authority with `tls internal`, so certificate issuance and renewal require no public DNS, ACME service, or internet connection. See the [Caddy TLS documentation](https://caddyserver.com/docs/caddyfile/directives/tls), [Docker internal networks](https://docs.docker.com/reference/compose-file/networks/#internal), and [image pull policy](https://docs.docker.com/reference/compose-file/services/#pull_policy).

## 1. Prepare once on a connected machine

Use a machine with Docker and Compose installed. Build for the offline server's CPU architecture (for example, Linux amd64). From the checkout root:

```powershell
docker build --platform linux/amd64 -f deploy/Dockerfile -t shareit:offline .
docker pull --platform linux/amd64 caddy:2
New-Item -ItemType Directory -Force .artifacts/offline-bundle/deploy/offline, .artifacts/offline-bundle/docs
docker image save --output .artifacts/offline-bundle/shareit-images.tar shareit:offline caddy:2
Copy-Item deploy/offline/compose.yml, deploy/offline/Caddyfile, deploy/offline/.env.example .artifacts/offline-bundle/deploy/offline/
Copy-Item docs/offline-deployment.md, docs/verification.md .artifacts/offline-bundle/docs/
Copy-Item LICENSE .artifacts/offline-bundle/
Get-FileHash .artifacts/offline-bundle/shareit-images.tar -Algorithm SHA256
```

For an arm64 server, use `linux/arm64` for both image commands. Transfer the whole bundle to the private network and verify the archive's SHA-256 after transfer. The archive contains the app, .NET runtime, Caddy, and dependencies; the offline server does not need NuGet, a .NET SDK, a GitHub connection, or a container registry. Install Docker Engine/Desktop and the Compose plugin on the destination beforehand, or bring their offline installation packages too. Offline containers use Linux, including when hosted by Docker Desktop on Windows.

Building from source or downloading upgrades needs packages/images obtained beforehand. Running the prepared release does not. For versioned releases, use the same versioned image tags in the build/pull, save, and `.env` steps. The defaults above work together as written; the saved archive provides the exact local images even if an upstream tag later changes.

## 2. Install on the isolated server

From the transferred bundle's root:

```powershell
docker image load --input shareit-images.tar
Copy-Item deploy/offline/.env.example deploy/offline/.env
```

Edit `deploy/offline/.env`:

- Set `SHAREIT_HOST` to a name resolved by your internal DNS, such as `shareit.home.arpa`, or to the server's static private IP. Do not include `https://` or a port. Clients must use that exact hostname/IP so the certificate matches.
- If using a name, add it to internal DNS or each client's hosts file, pointing at the server's LAN IP. Public DNS is unnecessary. Docker's private `172.30.13.x` addresses are not the addresses clients should use.
- Optionally bind `SHAREIT_BIND_IP` to the server's LAN interface. Allow inbound TCP 443 from the required LAN clients; port 80 only redirects to HTTPS. Keep the default HTTPS port 443 for ordinary use. If you change `SHAREIT_HTTPS_PORT`, include that port in the URL; the HTTP redirect uses the configured port too.
- If the Docker subnet overlaps another LAN/VPN/Docker subnet, change both `SHAREIT_SUBNET` and `SHAREIT_PROXY_IP` together.

Start with local images only:

```powershell
docker compose --env-file deploy/offline/.env -f deploy/offline/compose.yml up -d --no-build --pull never
docker compose --env-file deploy/offline/.env -f deploy/offline/compose.yml ps
```

A missing image causes a clear startup error; Compose does not attempt a download. One app instance is supported with SQLite and the current in-process coordination.

## 3. Trust the private HTTPS certificate

After Caddy starts, export **only its public root certificate**:

```powershell
docker compose --env-file deploy/offline/.env -f deploy/offline/compose.yml cp caddy:/data/caddy/pki/authorities/local/root.crt ./shareit-root.crt
```

Distribute that certificate through your normal IT trust process to each client. Import it into Windows Trusted Root Certification Authorities, the macOS System keychain, or your Linux/browser CA trust store. Firefox may use a separate trust store depending on enterprise configuration. Verify the certificate fingerprint with the server administrator. Keep the Caddy data volume and all private keys on the server; do not distribute them. Do not work around certificate errors with `--insecure` or browser bypasses.

Open `https://shareit.home.arpa` (or your configured IP/name). Trusted HTTPS keeps browser uploads, clipboard access, and secure cookies working on remote machines. A plain HTTP private IP is not equivalent to `localhost`: browsers restrict secure-context features there.

Copy buttons try a browser compatibility method when the modern Clipboard API is unavailable or denied. If both methods fail, a selected text box appears for manual Ctrl+C/Cmd+C copying; press Escape to dismiss it. Use trusted HTTPS for reliable access to the modern Clipboard API.

curl can use the exported root directly without changing its machine-wide trust store. The PIN is entered at its password prompt:

```bash
curl --cacert shareit-root.crt -fu w3r-yub "https://shareit.home.arpa/t/1"
```

Use `curl.exe` in Windows shells. Windows curl using Schannel may report `CERT_TRUST_REVOCATION_STATUS_UNKNOWN` for the local CA. In that case, add `--ssl-revoke-best-effort`; certificate-chain and hostname validation still apply, while unavailable revocation information is tolerated. This is different from `--insecure`. See [curl's option reference](https://curl.se/docs/manpage.html#--ssl-revoke-best-effort).

```powershell
curl.exe --ssl-revoke-best-effort --cacert shareit-root.crt -fu w3r-yub "https://shareit.home.arpa/t/1"
```

You can omit `--cacert` after the CA is trusted by curl's certificate store. Apply the same options to commands copied from the app when needed. If your organization already has a private CA, it can supply the server certificate/key instead: mount them read-only into Caddy and replace `tls internal` with `tls /certs/server.crt /certs/server.key`. Provision its trust chain and any revocation endpoints locally as your CA requires.

## Operation and upgrades

Named volumes retain the SQLite database, files, PIN pepper, browser-cookie keys, and local certificate authority across restarts. `docker compose down` keeps them; `down -v` deletes them. Preserve Caddy's volume during upgrades so clients continue trusting the same CA. Maintain the server's clock using a local time source or normal offline administration so expiry and TLS validity remain accurate.

To upgrade, prepare a new image archive on the connected build machine, transfer/load it, update the image tag in `.env`, and run the same startup command. Do not rebuild or pull inside the isolated environment. Keep session data out of the transfer bundle.

## Verification

The automated offline browser regression starts with empty browser contexts, rejects HTTP/WebSocket requests outside the app origin, and covers create/join, live text sharing, local fonts, file upload/download, terminal-style Basic authentication, and ending a session. Run it after restoring test tools and installing browser engines on a connected preparation machine:

```powershell
dotnet test tests/ShareIt.Web.Tests --no-restore --filter FullyQualifiedName~OfflineBrowserTests
```

To target a prepared HTTPS test deployment, set `SHAREIT_E2E_URL` to its URL. On Windows, also set `SHAREIT_E2E_CA_FILE` to the exported root certificate's absolute path to run real curl text/file downloads with certificate validation. Use a disposable deployment because this test creates and ends a synthetic session. See [verification results](verification.md) for the tested egress restrictions and cold certificate startup.

The deployment should also be tested with server egress denied and clients able to reach only the LAN service. Verify HTTPS health, creating/joining from a second machine, copying text, file upload/download, curl reads, and deletion. The browser test's request blocking covers application traffic; host-level browser/OS update traffic is outside the app's control.

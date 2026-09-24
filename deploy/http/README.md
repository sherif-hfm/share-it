# VM deployment from main

The `Deploy Share-It to VM` GitHub Actions workflow runs on the `shareit-vm` self-hosted runner. Pushes to `main` trigger it automatically, and it can also be started manually from the GitHub Actions page. Pull requests do not trigger it. Each run checks out that commit on the VM, builds the Docker image, recreates the app if its image changed, and checks the HTTP health endpoint. The runner needs Docker access and outbound access to GitHub; no inbound SSH or registry is needed.

The VM keeps its configuration in `/home/sherif/share-it/deploy/http/.env`, outside the runner's temporary checkout. Copy `.env.example` there before the first deployment. Port 80 is occupied on this VM, so the app binds `172.16.16.106:8083`. The Compose stack reuses the existing `shareit-offline_app_data` and `shareit-offline_app_secrets` volumes, preserving sessions, files, and keys.

The app runs in `Production` and accepts both `172.16.16.106` and `share-it.sherif.online`. `ShareIt__AllowHttp=true` preserves direct LAN HTTP access; public traffic uses HTTPS terminated by Nginx/OpenResty. Session cookies are secure when accessed over HTTPS. Other deployments still require HTTPS by default.

## Public HTTPS through Nginx/OpenResty

Keep these settings in `/home/sherif/share-it/deploy/http/.env`:

```dotenv
SHAREIT_HOST=172.16.16.106
SHAREIT_PUBLIC_HOST=share-it.sherif.online
SHAREIT_BIND_IP=172.16.16.106
SHAREIT_HTTP_PORT=8083
SHAREIT_TRUSTED_PROXY=<proxy source IP as seen by the app>
```

Set `SHAREIT_TRUSTED_PROXY` to the proxy's actual source IP, not the public domain's DNS address unless they are the same. The app only accepts forwarded HTTPS/client-IP headers from that address or loopback. This lets the Blazor origin check and secure cookies work through the proxy. Do not enable unrestricted forwarded-header trust. See Microsoft's [proxy configuration guidance](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0).

For Nginx Proxy Manager, configure the proxy host with domain `share-it.sherif.online`, forwarding scheme `http`, host `172.16.16.106`, port `8083`, and **Websockets Support** enabled. Assign the domain's TLS certificate and enable **Force SSL**. The proxy must preserve the original `Host` and send `X-Forwarded-Proto: https` and `X-Forwarded-For`.

For a manually managed Nginx HTTPS server block, the equivalent location is:

```nginx
location / {
    proxy_pass http://172.16.16.106:8083;
    proxy_http_version 1.1;
    proxy_set_header Host $http_host;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection "upgrade";
    proxy_read_timeout 3600s;
    client_max_body_size 26m;
}
```

Keep the backend port reachable only from the private network and proxy. After updating the VM's environment file, run the deployment workflow again, or recreate the service using the current checkout:

```bash
docker compose --env-file /home/sherif/share-it/deploy/http/.env -f deploy/http/compose.yml up -d --build app
curl --fail http://172.16.16.106:8083/health
curl --fail -H 'Host: share-it.sherif.online' http://172.16.16.106:8083/health
curl --fail https://share-it.sherif.online/health
```

Verify that a browser can create and join a session through the public URL and the LAN URL. The deployment workflow checks both hostnames against the backend. `Bad Request - Invalid Hostname` means the incoming `Host` is missing from `AllowedHosts`; a Blazor connection failing with HTTP 403 after the page loads usually means the forwarded scheme, original host, or trusted-proxy IP is wrong.

The runner is specific to this repository. Only trusted changes should be merged into `main`, since deployment jobs can control Docker on the VM.

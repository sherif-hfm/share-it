# VM deployment from main

The **Deploy Share-It through Dokploy** GitHub Actions workflow runs on the `shareit-vm` self-hosted runner. A push to `main` or a manual run on `main` calls the local Dokploy webhook. Dokploy fetches `main`, builds the image, and replaces the container on the VM's Docker Engine. The workflow checks that the replacement is serving requests and that the public HTTPS connection works.

The `Share-it` Compose service in Dokploy uses the public Git provider, branch `main`, and Compose path `./deploy/http/compose.yml`. Enable **Create Environment File** and **Autodeploy**. In **Advanced → Run Command**, append `--force-recreate` to the default Compose command so the workflow can detect a replacement even when the image is unchanged.

Copy the Compose webhook URL from Dokploy's **Deployments** tab using the VM's `http://localhost:3000` dashboard. Store it as a single line in `~/.config/share-it/dokploy-webhook` with mode `600` under the runner account. The runner calls Dokploy over loopback and checks the container through Docker's `default` context.

The Compose file expects `shareit-offline_app_data` and `shareit-offline_app_secrets` as external volumes on that Docker Engine. Create them before the first deployment. They persist sessions, files, and keys across future deploys.

The app runs in `Production` and accepts both `172.16.16.106` and `share-it.sherif.online`. `ShareIt__AllowHttp=true` preserves direct LAN HTTP access; public traffic uses HTTPS terminated by Nginx/OpenResty. Session cookies are secure when accessed over HTTPS. Other deployments still require HTTPS by default.

## Public HTTPS through Nginx/OpenResty

Set these values in the Share-it service's **Environment** tab in Dokploy:

```dotenv
SHAREIT_HOST=172.16.16.106
SHAREIT_PUBLIC_HOST=share-it.sherif.online
SHAREIT_BIND_IP=172.16.16.106
SHAREIT_HTTP_PORT=8083
SHAREIT_TRUSTED_PROXY=172.16.16.110
```

Nginx at `172.16.16.110` terminates public HTTPS and connects to the app over HTTP at `172.16.16.106:8083`. Share-It trusts forwarded headers from that proxy address, which lets the Blazor origin check and secure cookies work through the proxy. See Microsoft's [proxy configuration guidance](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0).

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

Keep the backend port reachable from the private network and proxy. After updating the environment in Dokploy, redeploy through its dashboard or run the workflow again. To verify the route manually:

```bash
curl --fail http://172.16.16.106:8083/health
curl --fail -H 'Host: share-it.sherif.online' http://172.16.16.106:8083/health
curl --fail https://share-it.sherif.online/health
```

Verify that a browser can create and join a session through the public URL and the LAN URL. The deployment workflow checks both hostnames against the backend. `Bad Request - Invalid Hostname` means the incoming `Host` is missing from `AllowedHosts`; a Blazor connection failing with HTTP 403 after the page loads usually means the forwarded scheme, original host, or trusted-proxy IP is wrong.

The runner is specific to this repository. Restrict changes to `main` to trusted contributors because deployment jobs can access Docker on the VM.

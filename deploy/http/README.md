# VM deployment from main

The `Deploy Share-It to VM` GitHub Actions workflow runs on the `shareit-vm` self-hosted runner. Pushes to `main` trigger it automatically, and it can also be started manually from the GitHub Actions page. Pull requests do not trigger it. Each run checks out that commit on the VM, builds the Docker image, recreates the app if its image changed, and checks the HTTP health endpoint. The runner needs Docker access and outbound access to GitHub; no inbound SSH or registry is needed.

The VM keeps its configuration in `/home/sherif/share-it/deploy/http/.env`, outside the runner's temporary checkout. Copy `.env.example` there before the first deployment. Port 80 is occupied on this VM, so the app binds `172.16.16.106:8083`. The Compose stack reuses the existing `shareit-offline_app_data` and `shareit-offline_app_secrets` volumes, preserving sessions, files, and keys.

The app currently uses the `Testing` environment to allow HTTP session cookies. When moving to public HTTPS behind Nginx, change the deployment environment to `Production` and configure forwarded HTTPS headers and the trusted proxy.

The runner is specific to this repository. Only trusted changes should be merged into `main`, since deployment jobs can control Docker on the VM.

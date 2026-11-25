#!/usr/bin/env bash
set -euo pipefail
domain="${1:-}"
ip_mode=false
if [[ "$domain" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  IFS='.' read -r -a octets <<< "$domain"
  for octet in "${octets[@]}"; do
    if (( 10#$octet > 255 )); then printf '%s\n' 'Invalid IPv4 address.' >&2; exit 1; fi
  done
  ip_mode=true
elif [[ ! "$domain" =~ ^([a-zA-Z0-9]([a-zA-Z0-9-]*[a-zA-Z0-9])?\.)+[a-zA-Z]{2,}$ ]]; then
  printf '%s\n' 'Usage: bash deploy.sh YOUR_DOMAIN_OR_IPV4' >&2
  exit 1
fi
if [ "$(id -u)" != 0 ]; then printf '%s\n' 'Run as root.' >&2; exit 1; fi
cd "$(dirname "$0")"
if [ ! -f public/logo.png ]; then printf '%s\n' 'Missing website assets.' >&2; exit 1; fi
if command -v docker >/dev/null && { docker container inspect craftpilot-site >/dev/null 2>&1 || docker container inspect craftpilot-proxy >/dev/null 2>&1; }; then
  printf '%s\n' 'CraftPilot is already deployed. Preserve its environment and database; use the documented update procedure.' >&2
  exit 1
fi
for port in 80 443 25565; do
  if ss -ltn | awk '{print $4}' | grep -Eq ":${port}$"; then
    printf '%s\n' "Port $port is already used. Existing services were not changed." >&2
    exit 1
  fi
done
if ! command -v docker >/dev/null; then
  apt-get update
  apt-get install -y docker.io openssl
  systemctl enable --now docker
fi
umask 077
if [ ! -f .env ]; then
  password="$(openssl rand -hex 24)"
  printf 'ADMIN_PASSWORD=%s\nPUBLIC_ORIGIN=https://%s\nMC_GATEWAY_ENABLED=true\n' "$password" "$domain" > .env
fi
if ! grep -Fxq "PUBLIC_ORIGIN=https://$domain" .env; then
  printf '%s\n' 'Existing .env origin differs from the requested domain. Check configuration before deployment.' >&2
  exit 1
fi
docker build -t craftpilot-site:local .
docker network inspect craftpilot >/dev/null 2>&1 || docker network create craftpilot >/dev/null
docker volume create craftpilot-data >/dev/null
docker volume create craftpilot-caddy-data >/dev/null
docker volume create craftpilot-caddy-config >/dev/null
if "$ip_mode"; then
  printf '{\n  default_sni %s\n}\n%s {\n  tls internal\n  reverse_proxy craftpilot-site:3210\n}\n' "$domain" "$domain" > Caddyfile
else
  printf '%s {\n  reverse_proxy craftpilot-site:3210\n}\n' "$domain" > Caddyfile
fi
docker run -d --name craftpilot-site --restart unless-stopped --network craftpilot \
  -p 25565:25565 --env MC_GATEWAY_ENABLED=true \
  --env-file .env --mount source=craftpilot-data,target=/app/data craftpilot-site:local >/dev/null
docker run -d --name craftpilot-proxy --restart unless-stopped --network craftpilot \
  -p 80:80 -p 443:443 \
  --mount "type=bind,source=$PWD/Caddyfile,target=/etc/caddy/Caddyfile,readonly" \
  --mount source=craftpilot-caddy-data,target=/data \
  --mount source=craftpilot-caddy-config,target=/config caddy:2 >/dev/null
printf '%s\n' "Website: https://$domain" "Administration: https://$domain/admin" \
  'Administrator password is in the root-readable .env file. Change it after first login.'
printf '%s\n' 'Allow TCP 80/443/25565 in the cloud security group. Point *.cpts.pw to this server. Caddy obtains HTTPS certificates automatically.'
if "$ip_mode"; then printf '%s\n' 'IP mode uses a private CA. Install its root certificate or configure a domain before public distribution.'; fi

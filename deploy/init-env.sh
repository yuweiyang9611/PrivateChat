#!/usr/bin/env bash
# 生成 deploy/.env（只需运行一次）
set -euo pipefail
cd "$(dirname "$0")"
if [ -f .env ]; then
  echo ".env 已存在，如需重新生成请先删除。"
  exit 1
fi
read -rp "访问域名（如 chat.example.com）: " DOMAIN
read -rp "管理员邮箱（登录后自动成为管理员）: " ADMIN_EMAIL
read -rp "Cloudflare 隧道 token: " TUNNEL_TOKEN
read -rp "Zero Trust 团队名（https://<团队名>.cloudflareaccess.com）: " CF_ACCESS_TEAM
read -rp "Access 应用的 AUD Tag: " CF_ACCESS_AUD
read -rp "Realtime SFU App ID: " CF_REALTIME_APP_ID
read -rp "Realtime SFU App Secret: " CF_REALTIME_APP_SECRET
read -rp "Realtime TURN Key ID: " CF_TURN_KEY_ID
read -rp "Realtime TURN API Token: " CF_TURN_KEY_API_TOKEN
echo "远程协助：电脑上的程序过 Access 门禁要用服务令牌（Zero Trust → Access → Service Auth，并在应用上加一条 Service Auth 策略）。没有可以先留空。"
read -rp "Access 服务令牌 Client ID（可留空）: " REMOTE_AGENT_ACCESS_CLIENT_ID
read -rp "Access 服务令牌 Client Secret（可留空）: " REMOTE_AGENT_ACCESS_CLIENT_SECRET
cat > .env <<EOF
DOMAIN=${DOMAIN}
ADMIN_EMAIL=${ADMIN_EMAIL}
TUNNEL_TOKEN=${TUNNEL_TOKEN}
CF_ACCESS_TEAM=${CF_ACCESS_TEAM}
CF_ACCESS_AUD=${CF_ACCESS_AUD}
CF_REALTIME_APP_ID=${CF_REALTIME_APP_ID}
CF_REALTIME_APP_SECRET=${CF_REALTIME_APP_SECRET}
CF_TURN_KEY_ID=${CF_TURN_KEY_ID}
CF_TURN_KEY_API_TOKEN=${CF_TURN_KEY_API_TOKEN}
REMOTE_AGENT_ACCESS_CLIENT_ID=${REMOTE_AGENT_ACCESS_CLIENT_ID}
REMOTE_AGENT_ACCESS_CLIENT_SECRET=${REMOTE_AGENT_ACCESS_CLIENT_SECRET}
POSTGRES_PASSWORD=$(openssl rand -hex 24)
EOF
chmod 600 .env
echo "已生成 .env，下一步：docker compose up -d"

# 生成 deploy/.env（Windows PowerShell 版，只需运行一次）
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
if (Test-Path .env) { Write-Host ".env 已存在，如需重新生成请先删除。"; exit 1 }
$domain = Read-Host "访问域名（如 chat.example.com）"
$email  = Read-Host "管理员邮箱（登录后自动成为管理员）"
$tunnel = Read-Host "Cloudflare 隧道 token"
$team   = Read-Host "Zero Trust 团队名（https://<团队名>.cloudflareaccess.com）"
$aud    = Read-Host "Access 应用的 AUD Tag"
$appId  = Read-Host "Realtime SFU App ID"
$appSec = Read-Host "Realtime SFU App Secret"
$turnId = Read-Host "Realtime TURN Key ID"
$turnTk = Read-Host "Realtime TURN API Token"
Write-Host "远程协助：电脑上的程序过 Access 门禁要用服务令牌（Zero Trust → Access → Service Auth，并在应用上加一条 Service Auth 策略）。没有可以先留空。"
$agentId  = Read-Host "Access 服务令牌 Client ID（可留空）"
$agentSec = Read-Host "Access 服务令牌 Client Secret（可留空）"
$buf = [byte[]]::new(24)
[System.Security.Cryptography.RandomNumberGenerator]::Fill($buf)
$pg = -join ($buf | ForEach-Object { $_.ToString('x2') })
@"
DOMAIN=$domain
ADMIN_EMAIL=$email
TUNNEL_TOKEN=$tunnel
CF_ACCESS_TEAM=$team
CF_ACCESS_AUD=$aud
CF_REALTIME_APP_ID=$appId
CF_REALTIME_APP_SECRET=$appSec
CF_TURN_KEY_ID=$turnId
CF_TURN_KEY_API_TOKEN=$turnTk
REMOTE_AGENT_ACCESS_CLIENT_ID=$agentId
REMOTE_AGENT_ACCESS_CLIENT_SECRET=$agentSec
POSTGRES_PASSWORD=$pg
"@ | Set-Content -Path .env -Encoding utf8NoBOM
Write-Host "已生成 .env，下一步：docker compose up -d"

#!/usr/bin/env node
// 在开发者的笔记本上构建、签名被控端发布版，放到 deploy/agent-release/PrivateChat.Agent.exe（不进仓库）：
//   node tools/build-agent-release.mjs
// 之后部署时把这个文件拷到服务器上仓库目录的同一位置（deploy/agent-release/），再构建 web 镜像——Dockerfile.web 有它就用它、
// 核对签名里的版本和代码的版本一样，没有就在镜像里编一个不带签名的。装成系统服务的电脑只装签名有效的版本（docs/DESIGN.md"远程协助：UAC、锁屏"）。
// 私钥在 %USERPROFILE%\.privatechat\agent-release-primary.pem（见 tools/sign-agent-release.mjs）。
import { execFileSync } from 'node:child_process'
import { copyFileSync, mkdirSync, mkdtempSync, readFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const project = join(root, 'src/PrivateChat.Agent/PrivateChat.Agent.csproj')
const version = readFileSync(project, 'utf8').match(/<Version>([^<]+)<\/Version>/)?.[1]
if (!version) throw new Error('csproj 里找不到 <Version>')

const out = mkdtempSync(join(tmpdir(), 'privatechat-agent-'))
try {
  console.log(`构建被控端 ${version}……`)
  execFileSync('dotnet', ['publish', project, '-c', 'Release', '-r', 'win-x64', '-o', out], { stdio: 'inherit' })
  const exe = join(out, 'PrivateChat.Agent.exe')
  console.log('签名……')
  const manifest = execFileSync(process.execPath, [join(root, 'tools/sign-agent-release.mjs'), exe, version], { encoding: 'utf8' }).trim()
  const target = join(root, 'deploy/agent-release/PrivateChat.Agent.exe')
  mkdirSync(dirname(target), { recursive: true })
  copyFileSync(exe, target)
  console.log(`好了：${target}`)
  console.log(`清单：${manifest}`)
} finally {
  rmSync(out, { recursive: true, force: true })
}

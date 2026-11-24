#!/usr/bin/env node
// 给被控端 exe 附上发布签名（格式见 src/PrivateChat.Agent/ReleaseSignature.cs）。只在开发者的笔记本上跑：私钥不进仓库、不上服务器。
//   node tools/sign-agent-release.mjs <exe> <版本号> [--key primary|backup|<pem 文件>] [--key-id N]
// 默认钥匙：%USERPROFILE%\.privatechat\agent-release-primary.pem（0 号）；backup 是 1 号（离线备份，平时不用）。
// 已经签过的文件先去掉旧签名再签。最后打印清单（版本、签好的文件的 SHA-256、大小），和服务器上 PrivateChat.Agent.json 一个格式。
import { createHash, createPrivateKey, sign } from 'node:crypto'
import { readFileSync, writeFileSync } from 'node:fs'
import { homedir } from 'node:os'
import { join } from 'node:path'

const MAGIC = 'PCSIGN01'
const DOMAIN = 'PrivateChat.Agent release v1\n'

const args = process.argv.slice(2)
const option = (name) => {
  const i = args.indexOf(name)
  return i >= 0 ? args.splice(i, 2)[1] : undefined
}
const keyOption = option('--key') ?? 'primary'
const keyIdOption = option('--key-id')
const [exe, version] = args
if (!exe || !version || !/^\d+\.\d+(\.\d+){0,2}$/.test(version)) {
  console.error('用法：node tools/sign-agent-release.mjs <exe> <版本号，如 0.9.0> [--key primary|backup|文件] [--key-id N]')
  process.exit(2)
}
const named = { primary: 0, backup: 1 }
const keyFile = keyOption in named ? join(homedir(), '.privatechat', `agent-release-${keyOption}.pem`) : keyOption
const keyId = keyIdOption !== undefined ? Number(keyIdOption) : (named[keyOption] ?? 0)

let data = readFileSync(exe)
// 去掉已有的签名
if (data.length > 12 && data.subarray(data.length - 8).toString('ascii') === MAGIC) {
  const versionLength = data.readUInt16LE(data.length - 12)
  data = data.subarray(0, data.length - (versionLength + 64 + 12))
}
const payloadHash = createHash('sha256').update(data).digest()
const message = Buffer.concat([Buffer.from(`${DOMAIN}${version}\n`, 'utf8'), payloadHash])
const signature = sign('sha256', message, { key: createPrivateKey(readFileSync(keyFile)), dsaEncoding: 'ieee-p1363' })
if (signature.length !== 64) throw new Error(`签名长度不对：${signature.length}`)
const versionBytes = Buffer.from(version, 'utf8')
const tail = Buffer.alloc(12)
tail.writeUInt16LE(versionBytes.length, 0)
tail.writeUInt8(keyId, 2)
tail.writeUInt8(1, 3)
tail.write(MAGIC, 4, 'ascii')
const signed = Buffer.concat([data, versionBytes, signature, tail])
writeFileSync(exe, signed)
console.log(JSON.stringify({ version, sha256: createHash('sha256').update(signed).digest('hex'), size: signed.length }))

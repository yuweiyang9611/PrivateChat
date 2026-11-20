// 测试栈放在服务器上，不放在写代码的电脑上：这个脚本把工作区的代码（含没提交的改动）同步过去，
// 在那边构建、启动一套和线上隔离的 compose 栈，并让 e2e 脚本经它去操作那套栈。
//
// 隔离靠的是：项目名 privatechat-test（容器、网络、数据卷都带这个前缀），镜像标签 :test，
// 端口只绑服务器的本机回环（网页 3509），不接隧道，用自己的 test.env。线上那套（项目名 privatechat）不受影响。
//
// 写代码的电脑上只有三样临时的东西：一条 SSH 隧道、测试用的浏览器、被控端程序（它是 Windows 程序，服务器上跑不了）。
//
// 用法（在 web/ 下）：
//   node e2e/test-host.cjs up            同步代码 → 构建 → 启动（改了代码之后再跑一次就是更新）
//   node e2e/test-host.cjs tunnel        开隧道，开着别关（Ctrl+C 结束）：本机 18080 → 测试栈网页，
//                                        服务器上的 8799 → 本机 8799（假 SFU，见 fake-sfu.cjs）
//   node e2e/test-host.cjs env           打印跑 e2e 要带的环境变量（E2E_BASE、E2E_COMPOSE、E2E_SFU_PORT）
//   node e2e/test-host.cjs dotnet-test … 在服务器上跑 .NET 测试（临时起一个 SDK 容器，数据库用测试栈的）；后面的参数原样交给 dotnet test
//   node e2e/test-host.cjs compose ...   对测试栈执行 docker compose（E2E_COMPOSE 指的就是它；CF_ 开头的环境变量会带过去）
//   node e2e/test-host.cjs down [-v]     停掉并删除容器（-v 连数据卷一起删）
//
// 连哪台服务器写在 e2e/.test-host.json 里（不进仓库），例如：
//   { "ssh": ["-i", "C:/Users/me/.ssh/key", "-p", "22", "user@192.168.1.10"], "prodEnv": "/opt/PrivateChat/deploy/.env" }
// prodEnv 可以不写：写了就把那份配置里 Cloudflare Realtime / TURN 的四项抄进测试栈的 test.env（只在服务器上进行，不经过这台电脑），
// 这样 E2E_REAL_CF=1 的实测才能跑；不写的话测试栈只能用假 SFU。
const { spawn, spawnSync } = require('child_process')
const fs = require('fs')
const os = require('os')
const path = require('path')

const ROOT = path.resolve(__dirname, '../..')
const CONFIG = path.join(__dirname, '.test-host.json')
const PROJECT = 'privatechat-test'
/** 服务器上（回环）和这台电脑上（隧道这一头）的端口 */
const PORT = { web: 3509, localWeb: 18080, sfu: 8799 }
const REMOTE_DIR = '$HOME/privatechat-test'
const COMPOSE = `docker compose -p ${PROJECT} --env-file ../test.env -f deploy/docker-compose.yml -f web/e2e/test-host.override.yml`
/** 明确列出服务名：不写的话 compose 会把文件里所有服务（包括 cloudflared）都拉起来 */
const SERVICES = 'web api postgres sfu-relay'

function config() {
  if (!fs.existsSync(CONFIG)) {
    console.error(`缺少 ${CONFIG}：写上连测试服务器用的 ssh 参数，格式见本文件开头的说明`)
    process.exit(2)
  }
  const c = JSON.parse(fs.readFileSync(CONFIG, 'utf8'))
  if (!Array.isArray(c.ssh) || c.ssh.length === 0) throw new Error(`${CONFIG} 里的 ssh 要是参数数组`)
  return { exe: c.sshExe || 'ssh', args: c.ssh, prodEnv: c.prodEnv || '' }
}

/** 给远端的 sh 用：整个参数包进单引号 */
const quote = (s) => `'${String(s).replace(/'/g, `'\\''`)}'`

/** 在服务器上执行一段 sh；返回退出码 */
function remote(script, { stdio = 'inherit', extra = [] } = {}) {
  const c = config()
  const r = spawnSync(c.exe, [...extra, ...c.args, script], { stdio })
  if (r.error) throw r.error
  return r.status ?? 1
}

function git(args, env = {}) {
  const r = spawnSync('git', args, { cwd: ROOT, env: { ...process.env, ...env }, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 })
  if (r.status !== 0) throw new Error(`git ${args.join(' ')} 失败：${r.stderr}`)
  return r.stdout.trim()
}

/** 工作区现在的样子（已跟踪的 + 没跟踪也没被忽略的文件）做成一个 tree，不动真正的暂存区 */
function workingTree() {
  const index = path.join(os.tmpdir(), `privatechat-test-index-${process.pid}`)
  fs.copyFileSync(path.resolve(ROOT, git(['rev-parse', '--git-dir']), 'index'), index)
  try {
    const env = { GIT_INDEX_FILE: index }
    git(['add', '-A'], env)
    return git(['write-tree'], env)
  } finally {
    fs.rmSync(index, { force: true })
  }
}

/** 把代码打包传过去，整个替换掉服务器上的那份 */
function sync() {
  const c = config()
  const tree = workingTree()
  const head = git(['rev-parse', '--short', 'HEAD'])
  const dirty = git(['status', '--porcelain']).split('\n').filter(Boolean).length
  console.log(`同步代码：${head}${dirty ? ` + ${dirty} 处没提交的改动` : ''}`)
  const script = `set -e; D=${REMOTE_DIR}; mkdir -p "$D"; rm -rf "$D/src.new"; mkdir "$D/src.new"; tar -xf - -C "$D/src.new"; rm -rf "$D/src"; mv "$D/src.new" "$D/src"`
  return new Promise((resolve, reject) => {
    const archive = spawn('git', ['archive', '--format=tar', tree], { cwd: ROOT, stdio: ['ignore', 'pipe', 'inherit'] })
    const ssh = spawn(c.exe, [...c.args, script], { stdio: ['pipe', 'inherit', 'inherit'] })
    archive.stdout.pipe(ssh.stdin)
    archive.on('error', reject)
    ssh.on('error', reject)
    ssh.on('exit', (code) => (code === 0 ? resolve() : reject(new Error(`同步失败（ssh 退出码 ${code}）`))))
  })
}

/** 测试栈自己的配置：第一次用时在服务器上生成。都是测试值；Cloudflare 那四项从 prodEnv 抄（有的话） */
function ensureEnv() {
  const c = config()
  const copy = c.prodEnv
    ? `grep -E '^(CF_REALTIME_APP_ID|CF_REALTIME_APP_SECRET|CF_TURN_KEY_ID|CF_TURN_KEY_API_TOKEN)=' ${quote(c.prodEnv)} 2>/dev/null || true`
    : 'true'
  const script = `set -e; D=${REMOTE_DIR}; if [ ! -f "$D/test.env" ]; then umask 077; {
echo DOMAIN=chat.example.com
echo ADMIN_EMAIL=admin@example.com
echo ADMIN_PASSWORD=admin123
echo TUNNEL_TOKEN=
echo CF_ACCESS_TEAM=yourteam
echo CF_ACCESS_AUD=
echo POSTGRES_PASSWORD=privatechat
${copy}
} > "$D/test.env"; echo "已生成 $D/test.env"; fi`
  if (remote(script) !== 0) throw new Error('生成 test.env 失败')
}

function compose(args, { env = {}, stdio = 'inherit' } = {}) {
  const assignments = Object.entries(env).map(([k, v]) => `${k}=${quote(v)}`).join(' ')
  const script = `cd ${REMOTE_DIR}/src && ${assignments ? `env ${assignments} ` : ''}${COMPOSE} ${args.map(quote).join(' ')}`
  // -n：不把这边的标准输入接过去（docker compose exec 会把它当成输入读掉）
  return remote(script, { stdio, extra: ['-n'] })
}

async function main() {
  const [command, ...rest] = process.argv.slice(2)
  switch (command) {
    case 'up': {
      await sync()
      ensureEnv()
      const code = compose(['up', '-d', '--build', '--wait', ...SERVICES.split(' ')])
      if (code === 0) console.log(`测试栈已启动。下一步：node e2e/test-host.cjs tunnel（开着），然后带上 node e2e/test-host.cjs env 打印的环境变量跑测试`)
      process.exit(code)
      break
    }
    case 'compose': {
      // e2e 脚本要把 api 临时指到假 SFU 上时，会把 CF_ 开头的变量放进环境里再调过来：带到服务器上去
      const env = Object.fromEntries(Object.entries(process.env).filter(([k]) => k.startsWith('CF_')))
      process.exit(compose(rest, { env }))
      break
    }
    case 'tunnel': {
      const c = config()
      console.log(`隧道：本机 ${PORT.localWeb} → 测试栈网页，服务器 ${PORT.sfu} → 本机 ${PORT.sfu}（假 SFU）。Ctrl+C 结束`)
      const r = spawnSync(c.exe, ['-N', '-o', 'ExitOnForwardFailure=yes', '-o', 'ServerAliveInterval=15',
        '-L', `127.0.0.1:${PORT.localWeb}:127.0.0.1:${PORT.web}`,
        '-R', `127.0.0.1:${PORT.sfu}:127.0.0.1:${PORT.sfu}`, ...c.args], { stdio: 'inherit' })
      process.exit(r.status ?? 1)
      break
    }
    case 'env': {
      // 用 127.0.0.1 不用 localhost：隧道只听 IPv4，.NET 连 localhost 会先试 IPv6，每条新连接白等两秒
      console.log(`export E2E_BASE=http://127.0.0.1:${PORT.localWeb}`)
      console.log(`export E2E_COMPOSE='node "${__filename.replace(/\\/g, '/')}" compose'`)
      console.log(`export E2E_SFU_PORT=${PORT.sfu}`)
      break
    }
    case 'dotnet-test': {
      // 和 CI 一样只跑测试项目（被控端是 Windows 程序，Linux 上不编它）。源码只读挂进去、拷一份再编：编出来的东西不留在服务器的目录里
      const args = ['tests/PrivateChat.Api.Tests', '-nologo', '-v', 'q', ...rest].map(quote).join(' ')
      const script = `docker run --rm --network ${PROJECT}_default -v ${REMOTE_DIR}/src:/src:ro -v ${PROJECT}_nuget:/root/.nuget/packages `
        + `-e PC_TEST_PG='Host=postgres;Port=5432;Username=privatechat;Password=privatechat;Database=postgres;Timeout=15' -e DOTNET_CLI_TELEMETRY_OPTOUT=1 `
        + `mcr.microsoft.com/dotnet/sdk:11.0 sh -c ${quote(`cp -r /src /work && cd /work && dotnet test ${args}`)}`
      process.exit(remote(script, { extra: ['-n'] }))
      break
    }
    case 'down':
      process.exit(compose(['down', ...(rest.includes('-v') ? ['-v'] : [])]))
      break
    default:
      console.error('用法：node e2e/test-host.cjs up | tunnel | env | compose <参数…> | dotnet-test [参数…] | down [-v]')
      process.exit(2)
  }
}

main().catch((e) => {
  console.error(e.message)
  process.exit(1)
})

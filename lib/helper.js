import { spawn, execFileSync } from 'node:child_process'
import { existsSync, mkdirSync, readdirSync, statSync } from 'node:fs'
import { createInterface } from 'node:readline'
import { join } from 'node:path'

const FRAMEWORK_DIRS = [
  'C:\\Windows\\Microsoft.NET\\Framework64\\v4.0.30319',
  'C:\\Windows\\Microsoft.NET\\Framework\\v4.0.30319',
]
const GAC_MSIL = 'C:\\Windows\\Microsoft.NET\\assembly\\GAC_MSIL'

function gacDll(name) {
  const base = join(GAC_MSIL, name)
  if (!existsSync(base)) return null
  try {
    for (const version of readdirSync(base)) {
      if (!version.startsWith('v4.0')) continue
      const path = join(base, version, `${name}.dll`)
      if (existsSync(path)) return path
    }
  } catch {}
  return null
}

/**
 * 常驻 C# helper：首次运行时用系统自带 csc.exe 现场编译，
 * 之后 stdin/stdout 一行一个 JSON，进程一直活着。
 */
export class Helper {
  constructor({ pluginRoot, dataDir, log }) {
    this.dir = join(dataDir, 'helper')
    this.source = join(pluginRoot, 'helper', 'CCHelper.cs')
    this.exe = join(this.dir, 'CCHelper.exe')
    this.log = log || (() => {})
    this.proc = null
    this.seq = 0
    this.pending = new Map()
    this.starting = null
    this.onEvent = null
    this.onReady = null
  }

  compiled() {
    if (!existsSync(this.exe)) return false
    try {
      return statSync(this.exe).mtimeMs >= statSync(this.source).mtimeMs
    } catch {
      return false
    }
  }

  compile() {
    mkdirSync(this.dir, { recursive: true })
    let csc = null
    let framework = null
    for (const dir of FRAMEWORK_DIRS) {
      const candidate = join(dir, 'csc.exe')
      if (existsSync(candidate)) {
        csc = candidate
        framework = dir
        break
      }
    }
    if (!csc) throw new Error('找不到 csc.exe（需要 .NET Framework 4）')
    const args = [
      '/nologo',
      '/target:exe',
      '/platform:anycpu',
      '/optimize+',
      `/out:${this.exe}`,
      `/r:${join(framework, 'System.dll')}`,
      `/r:${join(framework, 'System.Core.dll')}`,
      `/r:${join(framework, 'System.Drawing.dll')}`,
      `/r:${join(framework, 'System.Windows.Forms.dll')}`,
      `/r:${join(framework, 'System.Web.Extensions.dll')}`,
    ]
    for (const name of ['UIAutomationClient', 'UIAutomationTypes', 'WindowsBase']) {
      const path = gacDll(name)
      if (path) args.push(`/r:${path}`)
    }
    args.push(this.source)
    this.log('compiling CCHelper.exe …')
    // 用 ignore 而不是 pipe：有些受限环境不允许子进程走命名管道，编译只需要退出码。
    execFileSync(csc, args, { windowsHide: true, stdio: 'ignore', timeout: 180000 })
    if (!existsSync(this.exe)) throw new Error('csc 编译失败，没生成 CCHelper.exe')
    this.log('CCHelper.exe compiled')
  }

  async start() {
    if (this.proc) return
    if (this.starting) {
      await this.starting
      return
    }
    this.starting = (async () => {
      if (!this.compiled()) this.compile()
      const proc = spawn(this.exe, [], { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] })
      proc.stdout.setEncoding('utf8')
      proc.stderr.setEncoding('utf8')
      this.proc = proc
      const lines = createInterface({ input: proc.stdout })
      lines.on('line', (line) => this.onLine(line))
      proc.stderr.on('data', (chunk) => {
        const text = String(chunk).trim()
        if (text) this.log(`helper stderr: ${text.slice(0, 500)}`)
      })
      proc.on('exit', (code) => {
        this.proc = null
        for (const [, pending] of this.pending) {
          clearTimeout(pending.timer)
          pending.reject(new Error(`helper 退出（code ${code}）`))
        }
        this.pending.clear()
      })
      proc.on('error', (error) => this.log(`helper spawn 失败：${error.message}`))
      const pong = await this.rawCall('ping', {}, 20000)
      this.log(`helper ready pid=${pong.pid}`)
      if (typeof this.onReady === 'function') {
        try { await this.onReady() } catch (error) { this.log(`onReady failed: ${error.message}`) }
      }
    })()
    try {
      await this.starting
    } finally {
      this.starting = null
    }
  }

  rawCall(cmd, args, timeoutMs) {
    return new Promise((resolve, reject) => {
      const id = ++this.seq
      const timer = setTimeout(() => {
        this.pending.delete(id)
        reject(new Error(`helper 超时（${cmd}，${timeoutMs}ms）`))
        this.kill()
      }, timeoutMs)
      this.pending.set(id, { resolve, reject, timer })
      try {
        this.proc.stdin.write(`${JSON.stringify({ id, cmd, args })}\n`)
      } catch (error) {
        clearTimeout(timer)
        this.pending.delete(id)
        reject(error)
      }
    })
  }

  onLine(line) {
    let message = null
    try {
      message = JSON.parse(line)
    } catch {
      this.log(`helper 输出无法解析：${String(line).slice(0, 200)}`)
      return
    }
    const pending = this.pending.get(message.id)
    if (!pending) {
      // 没有 id 的是悬浮窗推来的事件（停止、拖动、锁定）
      if (message.event && typeof this.onEvent === 'function') {
        try { this.onEvent(String(message.event), message) } catch {}
      }
      return
    }
    this.pending.delete(message.id)
    clearTimeout(pending.timer)
    if (message.ok) pending.resolve(message.result)
    else pending.reject(new Error(String(message.error || 'helper 调用失败')))
  }

  async call(cmd, args = {}, timeoutMs = 30000) {
    if (!this.proc) await this.start()
    return this.rawCall(cmd, args, timeoutMs)
  }

  /**
   * 当前是不是正在观看或录制。清理轮询用：helper 没启动就直接当空闲，
   * 这样定时清理不会为了问一句而把常驻进程拉起来。
   */
  async busy() {
    if (!this.proc) return { watching: false, recording: false }
    try {
      const watch = await this.call('watch.status', {}, 8000)
      const learn = await this.call('learn.status', {}, 8000)
      return { watching: Boolean(watch?.running), recording: Boolean(learn?.recording) }
    } catch {
      return { watching: false, recording: false }
    }
  }

  kill() {
    if (!this.proc) return
    try {
      this.proc.stdin.end()
      this.proc.kill()
    } catch {}
    this.proc = null
  }

  dispose() {
    this.kill()
  }
}

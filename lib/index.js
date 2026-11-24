import { homedir } from 'node:os'
import { join, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'
import { existsSync, readdirSync, readFileSync, writeFileSync, mkdirSync, statSync, unlinkSync, rmSync, appendFileSync, openSync, readSync, closeSync } from 'node:fs'
import { zstdDecompressSync } from 'node:zlib'
import { randomUUID } from 'node:crypto'
import { spawn } from 'node:child_process'
import { State } from './state.js'
import { Helper } from './helper.js'
import { createTools } from './tools.js'
import { dirUsage, sessionDirs, cleanArea } from './store.js'

export const name = 'computer-control'
export const inject = ['tools', 'attachments', 'systemPrompt']

const API_ROUTE = '/api/computer-control'
const PKG = JSON.parse(readFileSync(new URL('../package.json', import.meta.url), 'utf8'))

const stamp = () => {
  const d = new Date()
  const pad = (n) => String(n).padStart(2, '0')
  return `${d.getFullYear()}${pad(d.getMonth() + 1)}${pad(d.getDate())}-${pad(d.getHours())}${pad(d.getMinutes())}${pad(d.getSeconds())}`
}
const safeName = (name) => String(name || 'task').replace(/[\\/:*?"<>|\s]+/g, '-').slice(0, 40) || 'task'

/**
 * computer control —— 控制电脑 / 观看学习 两种状态，可主动切换。
 *
 * 清理策略：不跑固定周期定时器。每次工具响应结束后排一个 30 分钟后的一次性清理；
 * 重复排期只保留最晚的那个（后一次调用会把前一次取消）。到点时如果正在观看/录制，
 * 就顺延 30 分钟再试。设置页里可以改保留天数、清理延迟并手动清理。
 */
export function apply(ctx, config = {}) {
  const pluginRoot = dirname(dirname(fileURLToPath(import.meta.url)))
  const dataDir = config.dataDir || join(process.env.USERPROFILE || homedir(), '.dsh', 'computer-control')

  const log = (text) => {
    try {
      console.error(`[computer-control] ${text}`)
      const file = join(dataDir, 'debug.log')
      try {
        if (existsSync(file) && statSync(file).size > 512 * 1024) rmSync(file, { force: true })
        appendFileSync(file, `${new Date().toISOString()} ${text}\n`)
      } catch {}
    } catch {}
  }

  mkdirSync(dataDir, { recursive: true })
  const state = new State(dataDir)
  const helper = new Helper({ pluginRoot, dataDir, log })

  // ---------- 可编辑设置（config.json 覆盖 apply 配置） ----------
  const settingsFile = join(dataDir, 'config.json')
  const settings = {
    retentionDays: 30,
    cleanupDelayMinutes: 30,
    overlayEnabled: true,
    overlayPosition: 'top-center',
    overlayX: 0,
    overlayY: 0,
    overlayLocked: false,
    overlayWidth: 460,
    overlayOpacity: 0.85,
    precisionClick: true,
    userPriority: true,
    shakePause: false,
    watchNarrate: true,
  }
  if (Number.isFinite(Number(config.retentionDays))) settings.retentionDays = Number(config.retentionDays)
  if (Number.isFinite(Number(config.cleanupDelayMinutes)) && Number(config.cleanupDelayMinutes) > 0) {
    settings.cleanupDelayMinutes = Number(config.cleanupDelayMinutes)
  }
  try {
    const saved = JSON.parse(readFileSync(settingsFile, 'utf8'))
    if (Number.isFinite(Number(saved.retentionDays))) settings.retentionDays = Math.max(0, Math.min(3650, Number(saved.retentionDays)))
    if (Number.isFinite(Number(saved.cleanupDelayMinutes)) && Number(saved.cleanupDelayMinutes) > 0) {
      settings.cleanupDelayMinutes = Math.max(1, Math.min(1440, Number(saved.cleanupDelayMinutes)))
    }
    if (typeof saved.overlayEnabled === 'boolean') settings.overlayEnabled = saved.overlayEnabled
    if (['top-center', 'top-left', 'top-right', 'custom'].includes(saved.overlayPosition)) settings.overlayPosition = saved.overlayPosition
    if (Number.isFinite(Number(saved.overlayX))) settings.overlayX = Number(saved.overlayX)
    if (Number.isFinite(Number(saved.overlayY))) settings.overlayY = Number(saved.overlayY)
    if (typeof saved.overlayLocked === 'boolean') settings.overlayLocked = saved.overlayLocked
    if (Number.isFinite(Number(saved.overlayWidth))) settings.overlayWidth = Math.max(300, Math.min(900, Math.round(Number(saved.overlayWidth))))
    if (Number.isFinite(Number(saved.overlayOpacity))) settings.overlayOpacity = Math.max(0.3, Math.min(1, Number(saved.overlayOpacity)))
    if (typeof saved.precisionClick === 'boolean') settings.precisionClick = saved.precisionClick
    if (typeof saved.userPriority === 'boolean') settings.userPriority = saved.userPriority
    if (typeof saved.shakePause === 'boolean') settings.shakePause = saved.shakePause
    if (typeof saved.watchNarrate === 'boolean') settings.watchNarrate = saved.watchNarrate
  } catch {}
  const saveSettings = () => {
    try { writeFileSync(settingsFile, JSON.stringify(settings, null, 2), 'utf8') } catch (error) { log(`save settings failed: ${error.message}`) }
  }
  saveSettings()

  // 悬浮岛的「暂停」和晃动暂停都只影响本次运行，不写盘
  let paused = false

  // 会话接入：工具调用时拿到的 agent，用来把抽屉里的输入送进对话
  let currentAgent = null
  let currentSessionId = ''
  let watchNote = ''
  let lastWatchAt = 0
  let narrating = false
  let narrationCooldownUntil = 0
  let replyTimer = null
  let watchReplyUntil = 0

  const sessionsRoot = join(homedir(), '.dsh', 'sessions')
  const transcriptState = new Map()

  function findSessionFile(id) {
    if (!id) return null
    const cached = transcriptState.get(id)
    if (cached && existsSync(cached.file)) return cached
    try {
      for (const dir of readdirSync(sessionsRoot)) {
        const file = join(sessionsRoot, dir, id, 'session.v4.jsonl.zstd')
        if (existsSync(file)) {
          const next = { file, offset: 0 }
          transcriptState.set(id, next)
          return next
        }
      }
    } catch {}
    return null
  }

  /** 从会话 transcript 里读新增事件（zstd 多帧格式，按 magic 切帧）。 */
  function readNewTranscript(id) {
    const st = findSessionFile(id)
    if (!st) return []
    let size = 0
    try { size = statSync(st.file).size } catch { return [] }
    if (size <= st.offset) return []
    const length = size - st.offset
    const buf = Buffer.alloc(length)
    let fd = null
    try {
      fd = openSync(st.file, 'r')
      readSync(fd, buf, 0, length, st.offset)
    } catch { return [] } finally { if (fd !== null) try { closeSync(fd) } catch {} }

    const magic = Buffer.from([0x28, 0xb5, 0x2f, 0xfd])
    const starts = []
    for (let i = 0; i <= buf.length - 4; i++) {
      if (buf[i] === 0x28 && buf[i + 1] === 0xb5 && buf[i + 2] === 0x2f && buf[i + 3] === 0xfd) starts.push(i)
    }
    if (!starts.length) return []
    const events = []
    for (let k = 0; k < starts.length; k++) {
      const end = k + 1 < starts.length ? starts[k + 1] : buf.length
      if (k + 1 === starts.length) break // 最后一帧可能还没写完，下次再读
      try {
        const text = zstdDecompressSync(buf.subarray(starts[k], end)).toString('utf8')
        for (const line of text.split('\n')) {
          if (!line.trim()) continue
          try { events.push(JSON.parse(line)) } catch {}
        }
      } catch {}
    }
    if (starts.length > 1) st.offset += starts[starts.length - 1]
    return events
  }

  function assistantTextOf(event) {
    if (!event || event.type !== 'assistant/message') return ''
    const content = event.data?.message?.content
    if (!Array.isArray(content)) return ''
    return content.filter((b) => b && b.type === 'text' && b.text).map((b) => b.text).join('\n').trim()
  }

  function appendChat(line) {
    try { void helper.call('overlay.chatlog', { line }, 3000) } catch {}
  }

  function startReplyWatch() {
    watchReplyUntil = Date.now() + 120000
    if (replyTimer) return
    replyTimer = setInterval(async () => {
      try {
        const id = currentSessionId
        if (!id) return
        const texts = []
        for (const event of readNewTranscript(id)) {
          const text = assistantTextOf(event)
          if (text) texts.push(text)
        }
        for (const text of texts) {
          appendChat('AI：' + text.slice(0, 1200))
          if (narrating) {
            narrating = false
            narrationCooldownUntil = Date.now() + 10000
          }
        }
        if (texts.length) watchReplyUntil = Date.now() + 5000
        if (Date.now() > watchReplyUntil) {
          clearInterval(replyTimer)
          replyTimer = null
          if (narrating) {
            narrating = false
            narrationCooldownUntil = Date.now() + 10000
          }
        }
      } catch {}
    }, 900)
    if (typeof replyTimer.unref === 'function') replyTimer.unref()
  }

  function sendToAgent(text, { narration = false } = {}) {
    if (!currentAgent) return false
    try {
      const message = {
        role: 'user',
        content: [{ type: 'text', text }],
        source: { kind: 'user' },
        id: randomUUID(),
      }
      const agent = currentAgent
      const running = agent.status === 'running'
      if (running && typeof agent.inject === 'function') agent.inject(message)
      else if (typeof agent.steer === 'function') agent.steer(message)
      else if (typeof agent.followup === 'function') agent.followup(message)
      else return false
      if (narration) narrating = true
      startReplyWatch()
      return true
    } catch (error) {
      log(`sendToAgent failed: ${error?.message || error}`)
      return false
    }
  }

  function pct(value) {
    const n = Number(value)
    if (!Number.isFinite(n)) return '0%'
    return `${(n * 100).toFixed(1)}%`
  }

  // ---------- 悬浮岛抽屉：走主会话 ----------
  function primeTranscriptOffset(id) {
    const st = findSessionFile(id)
    if (!st) return
    try { st.offset = statSync(st.file).size } catch {}
  }

  function sendDrawerMessage(text) {
    primeTranscriptOffset(currentSessionId)
    const ok = sendToAgent(text)
    if (!ok) appendChat('AI：主会话还没接上，先在左边对话里叫我一次再试。')
    return ok
  }

  /** 画面变化时自动解说：只在观看模式、空闲、冷却结束后触发。 */
  function maybeNarrate(payload) {
    const changeRatio = Number(payload?.ratio)
    // 第一帧只是基线（0.0%），以及微小变化都不值得解说
    if (!Number.isFinite(changeRatio) || changeRatio < 0.005) return
    if (!settings.watchNarrate || state.mode !== 'watch' || paused || narrating) return
    if (!currentAgent || currentAgent.status === 'running') return
    if (Date.now() < narrationCooldownUntil) return
    if (Date.now() - lastWatchAt < 8000) return
    lastWatchAt = Date.now()
    const ratio = pct(payload?.ratio)
    const bbox = payload?.bbox
    const where = bbox
      ? `（区域 ${Math.round(bbox.x)},${Math.round(bbox.y)} ${Math.round(bbox.w)}x${Math.round(bbox.h)}）`
      : ''
    const prompt = `【自动观看】刚检测到画面变化 ${ratio}${where}。请用 cc_watch peek 看最近的变化帧，用一两句话简短描述画面里发生了什么；不要做控制动作。`
    sendToAgent(prompt, { narration: true })
  }

  // ---------- 悬浮岛 ----------
  const overlayStateArgs = () => ({
    mode: state.mode,
    label: state.modeLabel,
    action: state.mode === 'watch' && watchNote ? watchNote : (state.data.lastAction || ''),
    userPriority: settings.userPriority !== false,
    paused,
    delayHideMs: 2000,
  })

  const pushOverlayConfig = async () => {
    if (!helper.proc) return
    try {
      await helper.call('overlay.config', {
        enabled: settings.overlayEnabled,
        position: settings.overlayPosition,
        x: settings.overlayX,
        y: settings.overlayY,
        locked: settings.overlayLocked,
        width: settings.overlayWidth,
        opacity: settings.overlayOpacity,
      }, 12000)
    } catch {}
  }

  const pushOverlayState = async () => {
    if (!helper.proc) return
    try { await helper.call('overlay.state', overlayStateArgs(), 12000) } catch {}
  }

  // 任何地方切状态（工具、设置页、悬浮岛）都同步到悬浮窗。
  state.onModeChange = () => { void pushOverlayState() }

  helper.onReady = async () => {
    await pushOverlayConfig()
    await pushOverlayState()
  }

  // helper 崩溃时自动重启：观看继续采集，控制模式恢复悬浮岛，不用手动 idle 再切回来。
  let recoveringWatch = false
  helper.onExit = async (code) => {
    log(`helper exited code=${code} mode=${state.mode}`)
    if (state.mode === 'idle' || recoveringWatch) return
    recoveringWatch = true
    try {
      await new Promise((resolve) => setTimeout(resolve, 1200))
      if (state.mode === 'idle') return
      if (state.mode === 'watch') {
        const res = await helper.call('watch.start', {
          dir: join(dataDir, 'watch'),
          fps: 2,
          idleFps: 0.5,
          maxWidth: 1600,
        }, 20000)
        log(`watch restored frames=${res?.frames ?? 0}`)
        watchNote = '采集刚重启过'
      } else {
        await helper.call('ping', {}, 20000)
        log('helper restarted for control')
      }
      await pushOverlayState()
    } catch (error) {
      log(`helper recovery failed: ${error.message}`)
    } finally {
      recoveringWatch = false
    }
  }

  helper.onEvent = (name, payload) => {
    if (name === 'overlay.pause') {
      paused = Boolean(payload?.paused)
      void pushOverlayState()
      return
    }
    if (name === 'overlay.chat') {
      const text = String(payload?.text || '').trim()
      if (!text) return
      void sendDrawerMessage(text)
      return
    }
    if (name === 'watch.change') {
      const ratio = Number(payload?.ratio) || 0
      const bbox = payload?.bbox
      watchNote = `变化 ${pct(ratio)}${bbox ? ` · ${Math.round(bbox.x)},${Math.round(bbox.y)} ${Math.round(bbox.w)}x${Math.round(bbox.h)}` : ''}`
      void pushOverlayState()
      maybeNarrate(payload)
      return
    }
    if (name === 'overlay.shake') {
      if (settings.shakePause === false || paused || state.mode !== 'control') return
      paused = true
      void (async () => {
        await pushOverlayState()
        try { await helper.call('overlay.alert', { text: '已暂停：检测到鼠标晃动', durationMs: 3000 }, 4000) } catch {}
      })()
      return
    }
    if (name === 'overlay.stop') {
      void (async () => {
        try {
          if (helper.proc) {
            const learn = await helper.call('learn.status', {}, 8000).catch(() => null)
            if (learn?.recording) await helper.call('learn.stop', {}, 30000).catch(() => null)
            await helper.call('watch.stop', {}, 10000).catch(() => null)
          }
        } catch {}
        state.setMode('idle', '悬浮岛停止')
        await pushOverlayState()
      })()
      return
    }
    if (name === 'overlay.close') {
      // 用户点悬浮岛上的 ×：后面的控制/观看都不再弹，直到设置页点「重置浮窗位置」。
      settings.overlayEnabled = false
      paused = false
      saveSettings()
      void (async () => {
        await pushOverlayConfig()
        await pushOverlayState()
      })()
      return
    }
    if (name === 'overlay.moved') {
      settings.overlayPosition = 'custom'
      settings.overlayX = Number(payload?.x) || 0
      settings.overlayY = Number(payload?.y) || 0
      saveSettings()
      return
    }
    if (name === 'overlay.resized') {
      const width = Number(payload?.width)
      if (Number.isFinite(width)) settings.overlayWidth = Math.max(300, Math.min(900, Math.round(width)))
      if (settings.overlayPosition === 'custom') {
        settings.overlayX = Number(payload?.x) || settings.overlayX
        settings.overlayY = Number(payload?.y) || settings.overlayY
      }
      saveSettings()
      return
    }
    if (name === 'overlay.locked') {
      settings.overlayLocked = Boolean(payload?.locked)
      saveSettings()
    }
  }

  let cleanupTimer = null
  let cleanupDueAt = 0
  let cleaning = false

  const runCleanup = async () => {
    cleanupDueAt = 0
    if (cleaning) {
      scheduleCleanup(60 * 1000)
      return
    }
    cleaning = true
    try {
      const busy = await helper.busy()
      if (busy.watching || busy.recording) {
        log(`cleanup postponed（${busy.watching ? 'watching' : 'recording'}）`)
        scheduleCleanup(settings.cleanupDelayMinutes * 60 * 1000)
        return
      }

      const tmp = join(dataDir, 'tmp')
      if (existsSync(tmp)) {
        // 和「自动清理延迟」保持一致：最后一次调用后过了这么久，旧截图一起清掉
        const cutoff = Date.now() - Math.max(1, settings.cleanupDelayMinutes) * 60 * 1000
        try {
          for (const file of readdirSync(tmp)) {
            const path = join(tmp, file)
            try {
              if (statSync(path).isFile() && statSync(path).mtimeMs < cutoff) unlinkSync(path)
            } catch {}
          }
        } catch {}
      }

      if (settings.retentionDays > 0) {
        const sessions = join(dataDir, 'sessions')
        if (existsSync(sessions)) {
          const cutoff = Date.now() - settings.retentionDays * 24 * 60 * 60 * 1000
          try {
            for (const name of readdirSync(sessions)) {
              const path = join(sessions, name)
              try {
                if (statSync(path).isDirectory() && statSync(path).mtimeMs < cutoff) {
                  rmSync(path, { recursive: true, force: true })
                  log(`pruned old session ${name}`)
                }
              } catch {}
            }
          } catch {}
        }
      }
    } finally {
      cleaning = false
    }
  }

  // 重复排期只保留最晚的：新的一次调用直接取消旧的，重新计时。
  const scheduleCleanup = (delayMs = settings.cleanupDelayMinutes * 60 * 1000) => {
    if (cleanupTimer) {
      clearTimeout(cleanupTimer)
      cleanupTimer = null
    }
    cleanupDueAt = Date.now() + delayMs
    cleanupTimer = setTimeout(() => {
      cleanupTimer = null
      void runCleanup()
    }, delayMs)
    if (typeof cleanupTimer.unref === 'function') cleanupTimer.unref()
  }

  // ---------- 设置页用到的动作 ----------

  const switchMode = async (target, reason = '') => {
    if (!['idle', 'control', 'watch'].includes(target)) return { ok: false, error: `未知状态：${target}` }
    if (helper.proc) {
      const learn = await helper.call('learn.status', {}, 8000).catch(() => null)
      if (learn?.recording && target !== 'watch') {
        return { ok: false, error: '正在学习录制，先停止录制再切状态' }
      }
    }
    if (target !== 'watch') {
      try { await helper.call('watch.stop', {}, 8000) } catch {}
    }
    state.setMode(target, reason)
    paused = false
    watchNote = ''
    void pushOverlayState()
    return { ok: true }
  }

  const watchStart = async () => {
    if (state.mode !== 'watch') state.setMode('watch', '设置页开始观看')
    if (helper.proc) {
      const status = await helper.call('watch.status', {}, 8000).catch(() => null)
      if (status?.running) return status
    }
    return helper.call('watch.start', {
      dir: join(dataDir, 'watch'),
      fps: 2,
      idleFps: 0.5,
      maxWidth: 1600,
    }, 20000).then(async (res) => {
      await pushOverlayState()
      return res
    })
  }

  const watchStop = async () => {
    const res = await helper.call('watch.stop', {}, 10000)
    void pushOverlayState()
    return res
  }

  const learnStart = async (title) => {
    const name = String(title || '未命名任务').trim() || '未命名任务'
    const dir = join(dataDir, 'sessions', `${stamp()}-${safeName(name)}`)
    mkdirSync(join(dir, 'frames'), { recursive: true })
    if (state.mode !== 'watch') state.setMode('watch', `学习录制：${name}`)
    const res = await helper.call('learn.start', { dir, name }, 25000)
    void pushOverlayState()
    return res
  }

  const learnStop = async () => {
    const res = await helper.call('learn.stop', {}, 30000)
    void pushOverlayState()
    return res
  }

  const buildStatus = async () => {
    const out = {
      ok: true,
      version: PKG.version,
      mode: state.mode,
      modeLabel: state.modeLabel,
      step: state.step,
      note: state.data.note || '',
      since: state.data.since || 0,
      cleanupDueAt,
      settings: { retentionDays: settings.retentionDays, cleanupDelayMinutes: settings.cleanupDelayMinutes, precisionClick: settings.precisionClick, userPriority: settings.userPriority, shakePause: settings.shakePause, watchNarrate: settings.watchNarrate },
      paused,
      overlay: {
        enabled: settings.overlayEnabled,
        position: settings.overlayPosition,
        locked: settings.overlayLocked,
        x: settings.overlayX,
        y: settings.overlayY,
        width: settings.overlayWidth,
        opacity: settings.overlayOpacity,
      },
      helper: null,
      watch: { running: false, frames: 0 },
      learn: { recording: false, inputs: 0, elapsedMs: 0 },
      storage: null,
    }
    if (helper.proc) {
      try {
        const info = await helper.call('info', {}, 8000)
        out.helper = {
          pid: info.pid,
          uptimeMs: info.uptimeMs || 0,
          screen: info.screen,
          cursor: info.cursor,
          foreground: info.foreground?.title || '',
          idleMs: info.idleMs ?? 0,
        }
      } catch {}
      try {
        const watch = await helper.call('watch.status', {}, 6000)
        out.watch = { running: Boolean(watch.running), frames: watch.frames || 0, startedMs: watch.startedMs || 0 }
      } catch {}
      try {
        const learn = await helper.call('learn.status', {}, 6000)
        out.learn = { recording: Boolean(learn.recording), inputs: learn.inputs || 0, elapsedMs: learn.elapsedMs || 0 }
      } catch {}
      try {
        out.overlayRuntime = await helper.call('overlay.status', {}, 6000)
      } catch {}
    }
    const tmp = dirUsage(join(dataDir, 'tmp'))
    const watchDir = dirUsage(join(dataDir, 'watch'))
    const sessions = dirUsage(join(dataDir, 'sessions'))
    const skills = dirUsage(join(dataDir, 'skills'))
    let steps = 0
    try { steps = statSync(state.stepsFile).size } catch {}
    out.storage = {
      tmp,
      watch: watchDir,
      sessions: { ...sessions, count: sessionDirs(join(dataDir, 'sessions')).length },
      skills,
      steps,
      total: tmp.bytes + watchDir.bytes + sessions.bytes + skills.bytes + steps,
    }
    return out
  }

  // ---------- 设置页接口（同源，只挂在本机 DSH 上） ----------
  const sameOrigin = (req) => {
    const origin = req.headers.origin
    if (!origin) return true
    try { return new URL(origin).host === req.headers.host } catch { return false }
  }

  const handler = async (req, res) => {
    const send = (code, body) => {
      res.writeHead(code, { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store' })
      res.end(JSON.stringify(body))
    }
    try {
      if (!sameOrigin(req)) return send(403, { ok: false, error: '只接受本机 DSH 页面的请求' })
      if (req.method === 'GET') return send(200, await buildStatus())
      if (req.method !== 'POST') return send(405, { ok: false, error: '只支持 GET / POST' })

      let raw = ''
      for await (const chunk of req) raw += chunk
      const body = raw ? JSON.parse(raw) : {}
      const action = String(body.action || '')

      switch (action) {
        case 'setMode': {
          const result = await switchMode(String(body.mode || ''), '设置页切换')
          if (!result.ok) return send(400, { ok: false, error: result.error })
          return send(200, await buildStatus())
        }
        case 'watchStart': {
          await watchStart()
          return send(200, await buildStatus())
        }
        case 'watchStop': {
          await watchStop()
          return send(200, await buildStatus())
        }
        case 'learnStart': {
          await learnStart(body.name)
          return send(200, await buildStatus())
        }
        case 'learnStop': {
          await learnStop()
          return send(200, await buildStatus())
        }
        case 'clean': {
          const area = String(body.area || 'tmp')
          if (!['tmp', 'watch', 'sessions', 'all'].includes(area)) return send(400, { ok: false, error: '未知清理范围' })
          if ((area === 'watch' || area === 'all') && state.mode === 'watch') {
            return send(400, { ok: false, error: '正在观看，先停止观看再清理变化帧' })
          }
          const days = Number.isFinite(Number(body.days)) ? Number(body.days) : settings.retentionDays
          const result = cleanArea(dataDir, area, days)
          log(`storage cleaned: ${area} removed=${result.removed} freed=${result.freed}`)
          return send(200, { ...(await buildStatus()), cleaned: result })
        }
        case 'saveSettings': {
          if (body.retentionDays !== undefined) {
            const value = Number(body.retentionDays)
            if (!Number.isFinite(value) || value < 0 || value > 3650) return send(400, { ok: false, error: '保留天数要是 0–3650' })
            settings.retentionDays = Math.round(value)
          }
          if (body.cleanupDelayMinutes !== undefined) {
            const value = Number(body.cleanupDelayMinutes)
            if (!Number.isFinite(value) || value < 1 || value > 1440) return send(400, { ok: false, error: '清理延迟要是 1–1440 分钟' })
            settings.cleanupDelayMinutes = Math.round(value)
          }
          if (body.precisionClick !== undefined) settings.precisionClick = Boolean(body.precisionClick)
          if (body.userPriority !== undefined) settings.userPriority = Boolean(body.userPriority)
          if (body.shakePause !== undefined) settings.shakePause = Boolean(body.shakePause)
          if (body.watchNarrate !== undefined) settings.watchNarrate = Boolean(body.watchNarrate)
          saveSettings()
          if (cleanupDueAt > 0) scheduleCleanup(settings.cleanupDelayMinutes * 60 * 1000)
          if (body.userPriority !== undefined) await pushOverlayState()
          return send(200, await buildStatus())
        }
        case 'togglePause': {
          paused = !paused
          await pushOverlayState()
          return send(200, await buildStatus())
        }
        case 'chatlog': {
          const line = String(body.line || '')
          if (line) appendChat(line)
          return send(200, { ok: true })
        }
        case 'drawerToggle': {
          try { await helper.call('overlay.drawer', {}, 5000) } catch {}
          return send(200, { ok: true, ...(await buildStatus()) })
        }
        case 'chat': {
          const text = String(body.text || '').trim()
          if (!text) return send(400, { ok: false, error: 'text 不能为空' })
          appendChat('你：' + text)
          const ok = sendDrawerMessage(text)
          return send(200, { ok, ...(await buildStatus()) })
        }
        case 'resetOverlay': {
          // 只重置位置并重新显示；锁定、长度、透明度都是用户的记忆，保留
          settings.overlayEnabled = true
          settings.overlayPosition = 'top-center'
          settings.overlayX = 0
          settings.overlayY = 0
          paused = false
          saveSettings()
          await pushOverlayConfig()
          await pushOverlayState()
          return send(200, await buildStatus())
        }
        case 'saveOverlay': {
          if (body.enabled !== undefined) {
            settings.overlayEnabled = Boolean(body.enabled)
            if (!settings.overlayEnabled) paused = false
          }
          if (body.locked !== undefined) settings.overlayLocked = Boolean(body.locked)
          if (body.width !== undefined) {
            const width = Number(body.width)
            if (!Number.isFinite(width) || width < 300 || width > 900) {
              return send(400, { ok: false, error: '悬浮岛长度要是 300–900' })
            }
            settings.overlayWidth = Math.round(width)
          }
          if (body.opacity !== undefined) {
            const opacity = Number(body.opacity)
            if (!Number.isFinite(opacity) || opacity < 0.3 || opacity > 1) {
              return send(400, { ok: false, error: '透明度要是 30%–100%' })
            }
            settings.overlayOpacity = Math.round(opacity * 100) / 100
          }
          if (body.position !== undefined) {
            const position = String(body.position)
            if (!['top-center', 'top-left', 'top-right', 'custom'].includes(position)) {
              return send(400, { ok: false, error: '位置只能是 top-center / top-left / top-right / custom' })
            }
            settings.overlayPosition = position
          }
          saveSettings()
          await pushOverlayConfig()
          await pushOverlayState()
          return send(200, await buildStatus())
        }
        case 'overlayPreview': {
          const ms = Number(body.durationMs)
          await helper.call('overlay.preview', { durationMs: Number.isFinite(ms) ? ms : 5000 }, 12000)
          return send(200, { ok: true })
        }
        case 'openDataDir': {
          try { spawn('explorer.exe', [dataDir], { detached: true, stdio: 'ignore' }).unref() } catch {}
          return send(200, { ok: true })
        }
        default:
          return send(400, { ok: false, error: `未知 action：${action}` })
      }
    } catch (error) {
      log(`settings api error: ${error?.message || error}`)
      send(500, { ok: false, error: String(error?.message || error) })
    }
  }

  ctx.inject(['webServer'], (sub) => sub.webServer.register({ kind: 'exact', path: API_ROUTE, handler }))

  // ---------- 工具 ----------
  const tools = createTools({
    helper,
    state,
    dataDir,
    attachments: ctx.attachments,
    cleanupDue: () => cleanupDueAt,
    preciseClick: () => settings.precisionClick !== false,
    userPriority: () => settings.userPriority !== false,
    isPaused: () => paused,
  })

  for (const tool of tools) {
    const run = tool.execute
    tool.execute = async (args, exec) => {
      try {
        const execAgent = exec && exec.agent
        if (execAgent) {
          currentAgent = execAgent
          if (execAgent.id) currentSessionId = String(execAgent.id)
        }
        const value = await run(args, exec)
        return JSON.parse(JSON.stringify(value ?? {}))
      } finally {
        // 响应结束后才排清理；失败也算一次活动。悬浮岛顺便刷新步数。
        scheduleCleanup()
        void pushOverlayState()
      }
    }
    ctx.effect(() => ctx.tools.register(tool), `computer-control: tool ${tool.name}`)
  }

  ctx.effect(
    () =>
      ctx.systemPrompt.section({
        name: 'computer-control',
        order: 55,
        text: () => state.promptLine(),
      }),
    'computer-control: prompt section',
  )

  ctx.effect(
    () => () => {
      if (cleanupTimer) {
        clearTimeout(cleanupTimer)
        cleanupTimer = null
        cleanupDueAt = 0
      }
      helper.dispose()
    },
    'computer-control: helper process and cleanup timer',
  )

  log(`loaded, mode=${state.mode}, data=${dataDir}`)
}

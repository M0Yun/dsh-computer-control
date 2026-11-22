import { mkdirSync, readFileSync, writeFileSync, readdirSync, existsSync, statSync } from 'node:fs'
import { join, basename } from 'node:path'
import { dirUsage, fmtBytes, sessionDirs, cleanArea } from './store.js'
import { MODES } from './state.js'

const ACTIONS = ['click', 'double_click', 'right_click', 'move', 'drag', 'scroll', 'key', 'type', 'wait']
const CHANGE_RATIO = 0.0025
const TIGHT_RATIO = 0.004

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms))
const pct = (value) => `${(Number(value || 0) * 100).toFixed(2)}%`

function output() {
  return {
    schema: {
      type: 'object',
      additionalProperties: true,
      properties: {
        text: { type: 'string' },
        images: { type: 'array', items: { type: 'object', additionalProperties: true } },
        data: { type: 'object', additionalProperties: true },
      },
      required: ['text'],
    },
    render: (_args, value) => {
      const blocks = [{ type: 'text', text: String(value?.text ?? '') }]
      for (const image of value?.images ?? []) blocks.push({ type: 'image', attachment: image })
      return blocks
    },
  }
}

export function createTools({ helper, state, dataDir, attachments, cleanupDue }) {
  const tmpDir = join(dataDir, 'tmp')
  const watchDir = join(dataDir, 'watch')
  const sessionsDir = join(dataDir, 'sessions')
  const skillsDir = join(dataDir, 'skills')
  for (const dir of [tmpDir, watchDir, sessionsDir, skillsDir]) mkdirSync(dir, { recursive: true })

  let snapshot = { hwnd: null, title: '', elements: [], at: 0 }
  let seq = 0

  const tmpFile = (prefix, ext = '.jpg') => join(tmpDir, `${prefix}-${Date.now()}-${(++seq).toString(36)}${ext}`)

  async function attach(path, name) {
    try {
      if (!path || !existsSync(path)) return null
      const data = readFileSync(path)
      const ref = await attachments.saveImage({ data, mediaType: 'image/jpeg', name: name || basename(path) })
      return {
        attachmentId: String(ref.attachmentId),
        mediaType: ref.mediaType,
        bytes: ref.bytes,
        width: ref.width,
        height: ref.height,
      }
    } catch {
      return null
    }
  }

  async function shoot(path, opts = {}) {
    return helper.call('shot', {
      path,
      maxWidth: opts.maxWidth ?? 1400,
      ...(opts.region ? { region: opts.region } : {}),
      ...(opts.mark ? { mark: opts.mark } : {}),
      ...(opts.label ? { label: opts.label } : {}),
      ...(opts.grid ? { grid: true } : {}),
    }, 25000)
  }

  async function info() {
    return helper.call('info', {}, 10000)
  }

  // ---------- 状态 ----------

  async function modeText() {
    let stateInfo = null
    let watch = null
    let learn = null
    try { stateInfo = await info() } catch {}
    try { watch = await helper.call('watch.status', {}, 8000) } catch {}
    try { learn = await helper.call('learn.status', {}, 8000) } catch {}
    const lines = []
    lines.push(`状态：${state.modeLabel}${state.mode === 'idle' ? '（暂时不碰电脑）' : ''}`)
    lines.push(`累计动作：${state.step} 步`)
    if (stateInfo) {
      lines.push(`屏幕：${stateInfo.screen.w}x${stateInfo.screen.h}（物理像素，缩放 ${Math.round((stateInfo.screen.w * 0 + (stateInfo.dpiSystem || 96)) / 96 * 100) / 100}x），光标 (${stateInfo.cursor.x}, ${stateInfo.cursor.y})`)
      lines.push(`前台：${stateInfo.foreground.title || '(无标题)'}${stateInfo.foreground.exe ? ` [${stateInfo.foreground.exe}]` : ''}`)
      lines.push(`你的空闲：${stateInfo.idleMs}ms`)
    }
    lines.push(`观看采集：${watch?.running ? `开（已存 ${watch.frames} 帧）` : '关'}`)
    lines.push(`学习录制：${learn?.recording ? `开（${learn.inputs} 个输入事件）` : '关'}`)
    if (state.data.note) lines.push(`备注：${state.data.note}`)
    return lines.join('\n')
  }

  async function setMode(args) {
    const target = String(args.mode || '')
    if (!MODES[target]) return { text: `✗ 未知状态「${target}」，只能是 control / watch / idle。` }
    let learn = null
    try { learn = await helper.call('learn.status', {}, 8000) } catch {}
    if (learn?.recording && target !== 'watch') {
      return { text: '✗ 正在学习录制，先把这次录完：cc_learn {action:"stop"}，再切状态。' }
    }
    const previous = state.mode
    if (previous === target) return { text: `状态已经是「${MODES[target]}」，没有变化。\n${await modeText()}` }
    if (target !== 'watch') {
      try { await helper.call('watch.stop', {}, 10000) } catch {}
    }
    state.setMode(target, String(args.reason || ''))
    const extra = {
      control: '可以动了：先 cc_look 看清屏幕，再 cc_act。',
      watch: '现在只看不动：cc_watch start/peek 看画面变化，cc_learn 记录并学习你的操作。',
      idle: '已停手，不再采集也不动作。',
    }[target]
    return {
      text: `✓ 状态切换：${MODES[previous]} → ${MODES[target]}${args.reason ? `（${args.reason}）` : ''}\n${extra}`,
      data: { from: previous, to: target },
    }
  }

  // ---------- 看 ----------

  function elementLine(e) {
    const flags = [e.invoke ? '可点' : '', e.toggle ? '可切换' : '', e.enabled === false ? '禁用' : ''].filter(Boolean).join(',')
    return `#${e.ref} ${e.type || '元素'}「${e.name}」 (${e.cx},${e.cy}) ${e.w}x${e.h}${flags ? ` [${flags}]` : ''}`
  }

  async function look(args = {}) {
    const detail = typeof args.detail === 'string' ? args.detail : 'both'
    let current = null
    try { current = await info() } catch (error) {
      return { text: `✗ helper 不可用：${error.message}` }
    }
    const path = tmpFile('look')
    try {
      await shoot(path, { maxWidth: 1400, grid: !!args.grid, region: args.region })
    } catch (error) {
      return { text: `✗ 截图失败：${error.message}` }
    }
    const image = await attach(path)
    let elements = []
    let elementError = ''
    if (detail !== 'screen') {
      try {
        const res = await helper.call('elements', { max: Math.min(400, args.max_elements || 150) }, 15000)
        elements = Array.isArray(res.elements) ? res.elements : []
      } catch (error) {
        elementError = String(error.message || error)
      }
    }
    if (detail !== 'screen') {
      snapshot = { hwnd: current.foreground.hwnd, title: current.foreground.title, elements, at: Date.now() }
    }

    const lines = []
    lines.push(`状态：${state.modeLabel}｜累计 ${state.step} 步`)
    lines.push(`屏幕 ${current.screen.w}x${current.screen.h}，前台：${current.foreground.title || '(无标题)'}${current.foreground.exe ? ` [${current.foreground.exe}]` : ''}`)
    lines.push(`光标 (${current.cursor.x}, ${current.cursor.y})；截图坐标就是物理像素，可直接给 cc_act 的 x/y`)
    if (detail !== 'screen') {
      if (elementError) {
        lines.push(`UI Automation 读取失败：${elementError}（可以直接看图估坐标）`)
      } else {
        const visible = elements.filter((e) => !e.offscreen)
        lines.push(`可点元素 ${elements.length} 个（显示前 60 个，ref 可用于 cc_act）：`)
        for (const e of visible.slice(0, 60)) lines.push('  ' + elementLine(e))
        if (visible.length > 60) lines.push(`  …还有 ${visible.length - 60} 个`)
        if (!visible.length) lines.push('  （这个窗口没有暴露可点元素，看截图判断位置）')
      }
    }
    const targetY = current.foreground.rect ? `${current.foreground.rect.x},${current.foreground.rect.y} ${current.foreground.rect.w}x${current.foreground.rect.h}` : ''
    if (targetY) lines.push(`窗口位置：${targetY}`)

    return {
      text: lines.join('\n'),
      images: image ? [image] : [],
      data: { mode: state.mode, elements: elements.length },
    }
  }

  // ---------- 动作 ----------

  // 周边证据区域：优先用目标元素自己的范围（文字会出现在元素里的任意位置，
  // 不能只看点击点周围）；元素太大（比如整个浏览器内容区）时退回点击点附近。
  function evidenceRegion(target) {
    if (!target) return null
    const element = target.element
    if (element && element.w >= 40 && element.h >= 30 && element.w * element.h <= 1600000) {
      return { x: element.x - 8, y: element.y - 8, w: element.w + 16, h: element.h + 16 }
    }
    return { x: Math.round(target.x - 230), y: Math.round(target.y - 170), w: 460, h: 340 }
  }

  async function waitUserIdle(maxMs = 2500) {
    const deadline = Date.now() + maxMs
    while (Date.now() < deadline) {
      try {
        const cur = await info()
        if ((cur.idleMs ?? 9999) >= 220) return true
      } catch {}
      await sleep(120)
    }
    return false
  }

  async function resolvePoint(args) {
    if (Number.isFinite(args.x) && Number.isFinite(args.y)) {
      const x = Math.round(args.x)
      const y = Math.round(args.y)
      return { x, y, label: `坐标 (${x}, ${y})` }
    }
    if (Number.isInteger(args.ref)) {
      const element = snapshot.elements.find((e) => e.ref === args.ref)
      if (!element) throw new Error(`ref ${args.ref} 不在最近的 cc_look 快照里，先重新 cc_look`)
      const current = await info()
      if (snapshot.hwnd && current.foreground.hwnd !== snapshot.hwnd) {
        throw new Error(`界面可能变了：快照是「${snapshot.title}」，现在前台是「${current.foreground.title}」，先重新 cc_look`)
      }
      return { x: element.cx, y: element.cy, label: `#${element.ref} ${element.type}「${element.name}」`, element }
    }
    if (typeof args.name === 'string' && args.name.trim()) {
      const needle = args.name.trim()
      const res = await helper.call('elements', { max: 300 }, 15000)
      const all = Array.isArray(res.elements) ? res.elements : []
      const items = all.filter((e) => String(e.name || '').includes(needle) && e.enabled !== false && !e.offscreen && e.w > 0 && e.h > 0)
      if (!items.length) throw new Error(`屏幕上没找到名字含「${needle}」的可点元素：改用 cc_look 拿 ref，或直接给 x/y`)
      const element = items[0]
      const rest = items.slice(1, 4).map((e) => `#${e.ref}「${e.name}」`).join('、')
      return {
        x: element.cx,
        y: element.cy,
        label: `#${element.ref} ${element.type}「${element.name}」`,
        note: rest ? `同名候选还有 ${rest}，下次可以用 ref 明确指一个` : '',
        element,
      }
    }
    throw new Error('缺少目标：ref / name / x+y 任选一个')
  }

  async function compareWith(beforePath, afterPath, region) {
    return helper.call('compare', {
      pathA: beforePath,
      pathB: afterPath,
      ...(region ? { regions: [region] } : {}),
    }, 20000)
  }

  async function verifyChange(beforePath, beforeTitle, region, timeoutMs) {
    const deadline = Date.now() + timeoutMs
    let bestRatio = 0
    let bestTight = 0
    let lastPath = null
    let lastTitle = beforeTitle
    while (Date.now() < deadline) {
      await sleep(140)
      const probe = tmpFile('probe')
      try { await shoot(probe, { maxWidth: 1200 }) } catch { continue }
      let cmp = null
      try { cmp = await compareWith(beforePath, probe, region) } catch { continue }
      const ratio = Number(cmp.ratio || 0)
      const tight = Number(cmp.regions?.[0]?.ratio || 0)
      const cells = Number(cmp.changedCells || 0)
      if (ratio > bestRatio) bestRatio = ratio
      if (tight > bestTight) bestTight = tight
      lastPath = probe
      let current = null
      try { current = await info() } catch {}
      lastTitle = current?.foreground?.title ?? lastTitle
      const titleChanged = beforeTitle !== lastTitle
      // 64×36 网格里 4 格约等于 0.17%：打字这类细变化全屏比例很低，但格子数是明确的。
      if (ratio >= CHANGE_RATIO || cells >= 4 || tight >= TIGHT_RATIO || titleChanged) {
        return {
          changed: true,
          ratio,
          tight,
          cells,
          bbox: cmp.bbox,
          titleChanged,
          title: lastTitle,
          path: probe,
          waitedMs: timeoutMs - Math.max(0, deadline - Date.now()),
        }
      }
    }
    return { changed: false, ratio: bestRatio, tight: bestTight, title: lastTitle, path: lastPath }
  }

  async function verifyWindow(titleNeedle, timeoutMs) {
    const deadline = Date.now() + timeoutMs
    let last = ''
    while (Date.now() < deadline) {
      await sleep(160)
      try {
        const current = await info()
        last = current.foreground.title || ''
        if (last.includes(titleNeedle)) return { changed: true, title: last }
      } catch {}
    }
    return { changed: false, title: last }
  }

  async function verifyText(needle, timeoutMs) {
    const deadline = Date.now() + timeoutMs
    let last = ''
    while (Date.now() < deadline) {
      await sleep(450)
      try {
        const res = await helper.call('text', { tail: 6000 }, 12000)
        last = String(res.text || '')
        if (last.includes(needle)) return { changed: true, text: last }
      } catch {}
    }
    return { changed: false, text: last }
  }

  async function verifyAfter(beforePath, beforeTitle, target, args) {
    const expect = String(args.expect || 'change')
    const timeoutMs = Math.max(200, Math.min(15000, Number(args.timeout_ms) || 1600))
    if (expect === 'none') return { changed: null, skipped: true }
    if (expect === 'window') {
      const res = await verifyWindow(String(args.expect_value || ''), timeoutMs)
      return { ...res, window: true }
    }
    if (expect === 'text') {
      const res = await verifyText(String(args.expect_value || ''), timeoutMs)
      return { ...res, textFound: res.changed }
    }
    if (expect === 'still') {
      await sleep(300)
      const probe = tmpFile('still')
      try {
        await shoot(probe, { maxWidth: 1200 })
        const cmp = await compareWith(beforePath, probe, target ? evidenceRegion(target) : null)
        const ratio = Number(cmp.ratio || 0)
        return { changed: ratio < CHANGE_RATIO, still: true, ratio, bbox: cmp.bbox, path: probe }
      } catch (error) {
        return { changed: null, error: String(error.message || error) }
      }
    }
    return verifyChange(beforePath, beforeTitle, target ? evidenceRegion(target) : null, timeoutMs)
  }

  async function performAction(action, args, point) {
    switch (action) {
      case 'click':
        return helper.call('click', { x: point.x, y: point.y, button: args.button || 'left', count: 1 }, 15000)
      case 'double_click':
        return helper.call('click', { x: point.x, y: point.y, button: args.button || 'left', count: 2 }, 15000)
      case 'right_click':
        return helper.call('click', { x: point.x, y: point.y, button: 'right', count: 1 }, 15000)
      case 'move':
        return helper.call('move', { x: point.x, y: point.y }, 10000)
      case 'drag': {
        const x2 = Number.isFinite(args.x2) ? Math.round(args.x2) : point.x
        const y2 = Number.isFinite(args.y2) ? Math.round(args.y2) : point.y
        return helper.call('drag', { x: point.x, y: point.y, x2, y2, duration: Number(args.duration) || 350 }, 20000)
      }
      case 'scroll':
        return helper.call('scroll', { x: point.x, y: point.y, delta: Number(args.delta) || -120, times: Number(args.times) || 3 }, 15000)
      case 'key':
        return helper.call('key', { keys: String(args.keys || '') }, 15000)
      case 'type':
        return helper.call('type', { text: String(args.text || '') }, 30000)
      case 'wait':
        await sleep(Math.max(50, Math.min(10000, Number(args.timeout_ms) || 500)))
        return { waited: true }
      default:
        throw new Error(`不认识的 action：${action}`)
    }
  }

  /** 悬浮岛第一行显示的「当前操作」。 */
  function describeAction(action, args, point) {
    const at = point ? `(${point.x}, ${point.y})` : ''
    switch (action) {
      case 'click': return `点击 ${at}`.trim()
      case 'double_click': return `双击 ${at}`.trim()
      case 'right_click': return `右键 ${at}`.trim()
      case 'move': return `移动 ${at}`.trim()
      case 'drag': {
        const x2 = Number.isFinite(args.x2) ? Math.round(args.x2) : point?.x
        const y2 = Number.isFinite(args.y2) ? Math.round(args.y2) : point?.y
        return `拖动 (${point?.x}, ${point?.y}) → (${x2}, ${y2})`
      }
      case 'scroll': return `滚轮 ${Number(args.times) || 3} 格`
      case 'key': return `按键 ${args.keys || ''}`.trim()
      case 'type': return `打字 ${String(args.text || '').length} 字`
      case 'wait': return `等待 ${Math.round(Number(args.timeout_ms) || 500)}ms`
      default: return action
    }
  }

  async function act(args = {}) {
    const action = String(args.action || '')
    if (!ACTIONS.includes(action)) {
      return { text: `✗ action 必须是：${ACTIONS.join(' / ')}` }
    }
    if (state.mode !== 'control') {
      return {
        text: `✗ 当前是「${state.modeLabel}」，控制动作被拒绝，什么都没做。\n要动手先切状态：cc_mode {action:"set", mode:"control"}`,
      }
    }

    const needsPoint = ['click', 'double_click', 'right_click', 'move', 'drag', 'scroll'].includes(action)
    const typeWithTarget = action === 'type' && (Number.isInteger(args.ref) || Number.isFinite(args.x) || typeof args.name === 'string')

    let point = null
    if (needsPoint || typeWithTarget) {
      try {
        point = await resolvePoint(args)
      } catch (error) {
        return { text: `✗ ${error.message}` }
      }
    }

    if (['type', 'key', 'drag'].includes(action)) {
      const idle = await waitUserIdle(2500)
      if (!idle) {
        return { text: '✗ 检测到你正在操作鼠标键盘，我先停手了。等你不忙再让我继续。' }
      }
    }

    if (args.preview) {
      const path = tmpFile('preview')
      try {
        await shoot(path, { maxWidth: 1400, mark: point ? { x: point.x, y: point.y } : undefined, label: point?.label })
      } catch (error) {
        return { text: `✗ 预览截图失败：${error.message}` }
      }
      const image = await attach(path)
      return {
        text: `预览（没有执行）：${action} ${point ? point.label + ` @ (${point.x}, ${point.y})` : ''}${point?.note ? `\n${point.note}` : ''}\n确认位置没问题，再去掉 preview 重发一次就会执行。`,
        images: image ? [image] : [],
      }
    }

    const beforeTitle = (await info().catch(() => ({ foreground: { title: '' } }))).foreground.title
    const beforePath = tmpFile('before')
    try {
      await shoot(beforePath, { maxWidth: 1200 })
    } catch (error) {
      return { text: `✗ 动作前的截图失败：${error.message}` }
    }

    const step = state.nextStep()
    let actionResult = null
    let actionError = null
    try {
      if (action === 'type' && typeWithTarget) {
        await helper.call('click', { x: point.x, y: point.y, button: 'left', count: 1 }, 15000)
        await sleep(120)
      }
      actionResult = await performAction(action, args, point)
    } catch (error) {
      actionError = String(error.message || error)
    }

    if (actionError) {
      state.record({ n: step, action, target: point?.label || '', x: point?.x, y: point?.y, verdict: 'error', error: actionError })
      const hint = /SendInput|拒绝|拒绝/.test(actionError)
        ? '\n这通常是目标程序以管理员权限运行，或当前在锁屏/安全桌面，键盘鼠标注入被系统丢掉了。改用同权限启动 DSH，或换一个不需要提权的操作。'
        : ''
      return { text: `✗ 第 ${step} 步 ${action} 失败：${actionError}${hint}` }
    }

    state.setAction(describeAction(action, args, point))

    let verdict = null
    try {
      verdict = await verifyAfter(beforePath, beforeTitle, point, args)
    } catch (error) {
      verdict = { changed: null, error: String(error.message || error) }
    }

    const path = tmpFile('result')
    try {
      await shoot(path, { maxWidth: 1400, mark: point ? { x: point.x, y: point.y } : undefined, label: point?.label })
    } catch {}
    const image = await attach(path)

    const lines = []
    const targetText = point ? `${point.label} @ (${point.x}, ${point.y})` : (action === 'type' ? '当前焦点' : action === 'key' ? `按键 ${args.keys || ''}` : '')
    const verdictText = verdict?.skipped
      ? '不判定（expect=none）'
      : verdict?.changed === true
        ? `生效${verdict.window || verdict.textFound ? `（${verdict.window ? '窗口已切换' : '文本已出现'}）` : ''}｜周边变化 ${pct(verdict.tight ?? 0)} / 全屏 ${pct(verdict.ratio ?? 0)}${verdict.titleChanged ? ' / 前台窗口变了' : ''}`
        : verdict?.changed === false
          ? `没看出变化（周边 ${pct(verdict.tight ?? 0)} / 全屏 ${pct(verdict.ratio ?? 0)}），不重试`
          : `没判定出来（${verdict?.error || '未知'}）`
    lines.push(`✓ 第 ${step} 步：${action} ${targetText}`)
    lines.push(`判定：${verdictText}`)
    if (point?.note) lines.push(point.note)
    if (verdict?.changed === false) {
      lines.push('可能只是聚焦/悬停、目标没点中，或画面本来就没变。先 cc_look 再看一眼，别盲目连点。')
    }
    const nowTitle = verdict?.title || beforeTitle
    if (nowTitle) lines.push(`前台：${nowTitle}`)

    state.record({
      n: step,
      action,
      target: point?.label || targetText,
      x: point?.x,
      y: point?.y,
      verdict: verdict?.changed === true ? 'ok' : verdict?.changed === false ? 'no-change' : 'unknown',
      ratio: verdict?.ratio,
      tight: verdict?.tight,
      title: nowTitle,
    })

    return {
      text: lines.join('\n'),
      images: image ? [image] : [],
      data: { step, action, verdict: verdict?.changed, x: point?.x, y: point?.y },
    }
  }

  // ---------- 验证 ----------

  async function verify(args = {}) {
    const kind = String(args.kind || 'change')
    const timeoutMs = Math.max(200, Math.min(30000, Number(args.timeout_ms) || 3000))
    if (kind === 'window') {
      const res = await verifyWindow(String(args.value || ''), timeoutMs)
      const path = tmpFile('verify')
      try { await shoot(path, { maxWidth: 1400 }) } catch {}
      const image = await attach(path)
      return {
        text: res.changed ? `✓ 前台窗口已出现「${args.value}」：${res.title}` : `✗ ${timeoutMs}ms 内前台窗口没有出现「${args.value}」，当前：${res.title}`,
        images: image ? [image] : [],
      }
    }
    if (kind === 'text') {
      const res = await verifyText(String(args.value || ''), timeoutMs)
      return { text: res.changed ? `✓ 窗口文字里出现了「${args.value}」` : `✗ ${timeoutMs}ms 内窗口文字里没出现「${args.value}」` }
    }
    if (kind === 'still') {
      const before = tmpFile('still-before')
      await shoot(before, { maxWidth: 1200 })
      await sleep(Math.max(300, Number(args.timeout_ms) || 800))
      const after = tmpFile('still-after')
      await shoot(after, { maxWidth: 1200 })
      const cmp = await compareWith(before, after, null)
      const still = Number(cmp.ratio || 0) < CHANGE_RATIO
      const image = await attach(after)
      return {
        text: still ? `✓ 画面基本没动（变化 ${pct(cmp.ratio)}）` : `✗ 画面在动（变化 ${pct(cmp.ratio)}，区域 ${cmp.bbox ? `${cmp.bbox.x},${cmp.bbox.y} ${cmp.bbox.w}x${cmp.bbox.h}` : ''}）`,
        images: image ? [image] : [],
      }
    }
    // change
    const before = tmpFile('verify-before')
    await shoot(before, { maxWidth: 1200 })
    const current = await info().catch(() => ({ foreground: { title: '' } }))
    const res = await verifyChange(before, current.foreground.title, null, timeoutMs)
    const path = res.path || tmpFile('verify-after')
    const image = await attach(path)
    if (res.changed) {
      return {
        text: `✓ 画面有变化：${pct(res.ratio)}${res.titleChanged ? '（前台窗口也变了）' : ''}`,
        images: image ? [image] : [],
      }
    }
    return {
      text: `✗ ${timeoutMs}ms 内画面没有明显变化（最大 ${pct(res.ratio)}）`,
      images: image ? [image] : [],
    }
  }

  // ---------- 观看 ----------

  async function watch(args = {}) {
    const action = String(args.action || 'status')
    let watchStatus = null
    try { watchStatus = await helper.call('watch.status', {}, 8000) } catch {}
    if (action === 'start') {
      if (watchStatus?.running) {
        return { text: `观看采集已经在跑：${watchStatus.frames} 帧，${Math.round(watchStatus.startedMs / 1000)} 秒。` }
      }
      if (state.mode !== 'watch') {
        state.setMode('watch', '开始观看采集')
      }
      const res = await helper.call('watch.start', {
        dir: watchDir,
        fps: Number(args.fps) || 2,
        idleFps: Number(args.idle_fps) || 0.5,
        maxWidth: 1600,
      }, 20000)
      return {
        text: `✓ 已开始观看（${MODES.watch}）：画面变化时约 ${res.fps} 帧/秒，连续 ${Math.round((res.idleAfterMs || 2000) / 1000)} 秒没变化自动降到 ${res.idleFps} 帧/秒；只在变化时留图。\n用 cc_watch {action:"peek"} 看最近的变化，cc_watch {action:"stop"} 停。`,
        data: res,
      }
    }
    if (action === 'stop') {
      const res = await helper.call('watch.stop', {}, 15000)
      return { text: `✓ 观看已停，这轮一共留下 ${res.frames} 张变化帧。` }
    }
    if (action === 'peek' || action === 'snapshot') {
      if (state.mode !== 'watch') {
        return { text: `✗ 当前是「${state.modeLabel}」，peek 只用于观看状态。先 cc_mode {action:"set", mode:"watch"}，或直接 cc_look。` }
      }
      if (!watchStatus?.running) {
        await helper.call('watch.start', { dir: watchDir, fps: Number(args.fps) || 2, maxWidth: 1600 }, 20000).catch(() => {})
        watchStatus = await helper.call('watch.status', {}, 8000).catch(() => null)
      }
      if (action === 'snapshot') {
        const path = tmpFile('snapshot')
        await helper.call('watch.snapshot', { path }, 20000)
        const image = await attach(path)
        return { text: '当前屏幕：', images: image ? [image] : [] }
      }
      const res = await helper.call('watch.peek', { limit: Math.max(1, Math.min(5, Number(args.limit) || 3)) }, 25000)
      const frames = Array.isArray(res.frames) ? res.frames : []
      const images = []
      if (res.latest) {
        const latestImage = await attach(res.latest, 'latest.jpg')
        if (latestImage) images.push(latestImage)
      }
      for (const frame of frames.slice(-2)) {
        const frameImage = await attach(frame.path, frame.file || basename(frame.path || ''))
        if (frameImage) images.push(frameImage)
      }
      const lines = []
      lines.push(`状态：${state.modeLabel}｜采集${res.running ? '中' : '已停'}${watchStatus ? `｜累计 ${watchStatus.frames} 帧` : ''}`)
      if (frames.length) {
        lines.push('最近画面变化：')
        for (const frame of frames) {
          const time = String(frame.t || '').slice(0, 12)
          lines.push(`  ${time} 变化 ${pct(frame.ratio)}${frame.bbox ? ` 区域 (${frame.bbox.x},${frame.bbox.y} ${frame.bbox.w}x${frame.bbox.h})` : ''} — ${frame.title || ''}`)
        }
      } else {
        lines.push('还没有记录到画面变化。')
      }
      if (Array.isArray(res.inputs) && res.inputs.length) {
        lines.push('你最近的操作：')
        for (const input of res.inputs.slice(-8)) {
          lines.push('  ' + inputLine(input))
        }
      }
      return { text: lines.join('\n'), images }
    }
    if (!watchStatus) return { text: '✗ helper 不可用，无法查看观看状态。' }
    return {
      text: `观看采集：${watchStatus.running ? '开' : '关'}｜${watchStatus.frames} 帧｜${watchStatus.running ? Math.round(watchStatus.startedMs / 1000) + ' 秒' : ''}`,
      data: watchStatus,
    }
  }

  function inputLine(input) {
    const time = String(input.t || '')
    if (input.kind === 'mouse' && input.action === 'click') return `${time} 点击 ${input.button || ''} (${input.x},${input.y}) — ${input.title || ''}`
    if (input.kind === 'mouse' && input.action === 'wheel') return `${time} 滚轮 ${input.wheel ?? ''} — ${input.title || ''}`
    if (input.kind === 'key') return `${time} 按键 ${input.mods ? input.mods + '+' : ''}${input.key || input.vk} — ${input.title || ''}`
    return `${time} ${input.kind || ''} ${input.action || ''}`
  }

  // ---------- 学习 ----------

  const stamp = () => {
    const d = new Date()
    const pad = (n) => String(n).padStart(2, '0')
    return `${d.getFullYear()}${pad(d.getMonth() + 1)}${pad(d.getDate())}-${pad(d.getHours())}${pad(d.getMinutes())}${pad(d.getSeconds())}`
  }

  const safeName = (name) => String(name || 'task').replace(/[\\/:*?"<>|\s]+/g, '-').slice(0, 40) || 'task'

  async function learn(args = {}) {
    const action = String(args.action || 'status')
    if (action === 'start') {
      const name = String(args.name || '未命名任务')
      const dir = join(sessionsDir, `${stamp()}-${safeName(name)}`)
      mkdirSync(join(dir, 'frames'), { recursive: true })
      if (state.mode !== 'watch') state.setMode('watch', `学习录制：${name}`)
      const res = await helper.call('learn.start', { dir, name }, 25000)
      return {
        text: `✓ 开始学习录制：「${name}」\n接下来你正常操作就行，我在看屏幕变化和你的点击/按键。做完调用 cc_learn {action:"stop"}，我会把时间线整理出来。`,
        data: res,
      }
    }
    if (action === 'stop') {
      let res = null
      try { res = await helper.call('learn.stop', {}, 30000) } catch (error) {
        return { text: `✗ 停止录制失败：${error.message}` }
      }
      let timeline = ''
      try { timeline = readFileSync(res.timeline, 'utf8').slice(0, 5000) } catch {}
      const images = []
      for (const frame of (res.sampleFrames || []).slice(-2)) {
        const image = await attach(frame.path, frame.file || basename(frame.path || ''))
        if (image) images.push(image)
      }
      const lines = []
      lines.push(`✓ 学习录制结束：${Math.round((res.durationMs || 0) / 1000)} 秒，${res.events} 个输入事件，${res.frames} 张变化帧。`)
      lines.push(`记录目录：${res.dir}`)
      lines.push('')
      lines.push(timeline || '（时间线是空的：可能这段时间没有操作）')
      lines.push('')
      lines.push('想沉淀成技能就调用 cc_learn {action:"save_skill", name:"技能名", content:"markdown 内容"}。')
      return { text: lines.join('\n'), images, data: { dir: res.dir, timeline: res.timeline } }
    }
    if (action === 'status') {
      const res = await helper.call('learn.status', {}, 10000)
      return {
        text: res.recording
          ? `正在录制：${res.inputs} 个输入事件，${Math.round(res.elapsedMs / 1000)} 秒。`
          : '现在没有在录制。要开始就 cc_learn {action:"start", name:"任务名"}。',
        data: res,
      }
    }
    if (action === 'list') {
      const sessions = []
      try {
        for (const id of readdirSync(sessionsDir)) {
          const dir = join(sessionsDir, id)
          if (!statSync(dir).isDirectory()) continue
          let meta = {}
          try { meta = JSON.parse(readFileSync(join(dir, 'meta.json'), 'utf8')) } catch {}
          sessions.push({ id, name: meta.name || id, startedAt: meta.startedAt || '' })
        }
      } catch {}
      sessions.sort((a, b) => String(b.startedAt).localeCompare(String(a.startedAt)))
      const skills = []
      try {
        for (const file of readdirSync(skillsDir)) {
          if (file.endsWith('.md')) skills.push({ name: file.replace(/\.md$/, '') })
        }
      } catch {}
      const lines = []
      lines.push(`学习记录 ${sessions.length} 条：`)
      for (const item of sessions.slice(0, 12)) lines.push(`  ${item.id} — ${item.name}（${item.startedAt}）`)
      lines.push('')
      lines.push(`技能 ${skills.length} 个：`)
      for (const skill of skills) lines.push(`  ${skill.name}`)
      return { text: lines.join('\n') }
    }
    if (action === 'read') {
      const id = String(args.id || '')
      const allowed = ['timeline.md', 'meta.json', 'events.jsonl']
      const file = allowed.includes(String(args.file || '')) ? String(args.file) : 'timeline.md'
      if (!id || /[\\/]/.test(id)) return { text: '✗ 需要正确的会话 id（cc_learn {action:"list"} 里有）。' }
      const path = join(sessionsDir, id, file)
      if (!existsSync(path)) return { text: `✗ 找不到：${path}` }
      const text = readFileSync(path, 'utf8')
      return { text: `【${id}/${file}】\n${text.slice(0, 8000)}${text.length > 8000 ? '\n…（截断）' : ''}` }
    }
    if (action === 'save_skill') {
      const name = String(args.name || '').trim()
      const content = String(args.content || '').trim()
      if (!name) return { text: '✗ 需要 name（技能名）。' }
      if (!content) return { text: '✗ 需要 content（技能正文）。' }
      const file = join(skillsDir, `${safeName(name)}.md`)
      const header = `---\nname: ${name}\ncreated: ${new Date().toISOString()}\nsource: computer-control\n---\n\n`
      writeFileSync(file, header + content, 'utf8')
      return { text: `✓ 技能已保存：${file}\n以后要按这个流程做，先 cc_learn {action:"read_skill", name:"${name}"} 读出来照着做。`, data: { path: file } }
    }
    if (action === 'read_skill') {
      const name = String(args.name || '').trim()
      let file = join(skillsDir, `${safeName(name)}.md`)
      if (!existsSync(file)) {
        const list = []
        try { for (const f of readdirSync(skillsDir)) if (f.endsWith('.md')) list.push(f.replace(/\.md$/, '')) } catch {}
        return { text: `✗ 找不到技能「${name}」。现有：${list.join('、') || '（无）'}` }
      }
      const text = readFileSync(file, 'utf8')
      return { text: text.slice(0, 8000) }
    }
    return { text: `✗ 未知 action：${action}` }
  }

  // ---------- 日志 ----------

  async function log(args = {}) {
    const steps = state.recent(Math.max(1, Math.min(40, Number(args.limit) || 12)))
    if (!steps.length) return { text: '还没有动作记录。' }
    const lines = ['最近的动作：']
    for (const step of steps) {
      const verdict = { ok: '生效', 'no-change': '没变化', error: '出错', unknown: '未判定' }[step.verdict] || step.verdict || ''
      lines.push(`  #${step.n} ${step.action || ''} ${step.target || ''} → ${verdict}${step.ratio != null ? `（${pct(step.ratio)}）` : ''}`)
    }
    return { text: lines.join('\n') }
  }

  // ---------- 存储 ----------

  async function storage(args = {}) {
    const action = String(args.action || 'status')
    if (action === 'clean') {
      const area = String(args.area || 'tmp')
      const days = Number.isFinite(Number(args.days)) ? Number(args.days) : 30
      const busy = await helper.busy()
      if ((area === 'watch' || area === 'all') && busy.watching) {
        return { text: '✗ 正在观看，变化帧还在往里写。先 cc_watch stop，再清理。' }
      }
      if (!['tmp', 'watch', 'sessions', 'all'].includes(area)) {
        return { text: '✗ area 只能是 tmp / watch / sessions / all（技能不会被删）' }
      }
      const { removed, freed } = cleanArea(dataDir, area, days)
      const label = { tmp: '动作截图', watch: '观看变化帧', sessions: `学习记录（${days <= 0 ? '全部' : `${days} 天前`}）`, all: '全部（技能除外）' }[area]
      return {
        text: `✓ 已清理${label}：删了 ${removed} 项，释放约 ${fmtBytes(freed)}。\n技能和学习时手动保存的东西不受影响。`,
        data: { removed, freed },
      }
    }

    const tmp = dirUsage(tmpDir)
    const watch = dirUsage(watchDir)
    const sessions = dirUsage(sessionsDir)
    const skills = dirUsage(skillsDir)
    let stepsBytes = 0
    try { stepsBytes = statSync(state.stepsFile).size } catch {}
    const total = tmp.bytes + watch.bytes + sessions.bytes + skills.bytes + stepsBytes
    const lines = []
    lines.push(`数据目录：${dataDir}`)
    lines.push(`- tmp（动作截图，2 小时自动清）：${tmp.files} 个文件，${fmtBytes(tmp.bytes)}`)
    lines.push(`- watch（观看变化帧，每次开始观看会清空）：${watch.files} 个文件，${fmtBytes(watch.bytes)}`)
    lines.push(`- sessions（学习记录，默认保留 30 天）：${sessionDirs(sessionsDir).length} 条，${sessions.files} 个文件，${fmtBytes(sessions.bytes)}`)
    lines.push(`- skills（技能，长期保留）：${skills.files} 个，${fmtBytes(skills.bytes)}`)
    lines.push(`- 状态/动作流水：${fmtBytes(stepsBytes)}`)
    lines.push(`合计：${fmtBytes(total)}`)
    const busy = await helper.busy()
    const due = typeof cleanupDue === 'function' ? cleanupDue() : 0
    const busyNote = busy.watching || busy.recording ? '；正在观看/录制，到点会顺延 30 分钟' : ''
    const dueText = due
      ? `已排期（约 ${new Date(due).toLocaleTimeString('zh-CN', { hour12: false })}，最近一次工具调用后 30 分钟）`
      : '未排期（还没有工具调用；每次调用结束后会排一次）'
    lines.push(`自动清理：${dueText}${busyNote}`)
    lines.push('')
    lines.push('手动清理：cc_storage {action:"clean", area:"tmp|watch|sessions|all", days:30}（days=0 表示 sessions 全部清掉）')
    return { text: lines.join('\n'), data: { total, tmp, watch, sessions, skills } }
  }

  // ---------- 工具定义 ----------

  return [
    {
      name: 'cc_mode',
      description: 'computer control 的状态开关。control=控制电脑（能点鼠标、能打字），watch=观看学习（只观察、记操作、不动手），idle=待机。切到 control 会停掉观看采集；学习录制中不能切走，先 cc_learn stop。get 查看当前状态。',
      parameters: {
        type: 'object',
        additionalProperties: false,
        properties: {
          action: { type: 'string', enum: ['get', 'set'], description: 'get 查看状态，set 切换状态' },
          mode: { type: 'string', enum: ['control', 'watch', 'idle'], description: 'set 时的目标状态' },
          reason: { type: 'string', description: '切换原因，一句话' },
        },
        required: ['action'],
      },
      output: output(),
      timeoutMs: 40000,
      async execute(args) {
        const action = String(args?.action || 'get')
        if (action === 'set') return setMode(args || {})
        return { text: await modeText() }
      },
    },
    {
      name: 'cc_look',
      description: '看清现在的屏幕：截图（物理像素坐标，可选网格）+ 前台窗口 + UI Automation 可点元素清单（ref 编号、类型、名称、中心坐标）。控制模式下动手前先看这个；ref 直接给 cc_act。',
      parameters: {
        type: 'object',
        additionalProperties: false,
        properties: {
          detail: { type: 'string', enum: ['both', 'screen', 'elements'], description: 'both=截图+元素（默认），screen=只截图，elements=只读元素' },
          grid: { type: 'boolean', description: '截图叠加 100px 网格和坐标，方便估位置' },
          max_elements: { type: 'number', description: '最多返回多少元素，默认 150' },
          region: { type: 'array', items: { type: 'number' }, description: '只看一块：[x,y,w,h]，物理像素' },
        },
      },
      output: output(),
      timeoutMs: 60000,
      async execute(args) {
        return look(args || {})
      },
    },
    {
      name: 'cc_act',
      description: '控制模式下的动作：点击/双击/右键/移动/拖拽/滚轮/按键/打字/等待。每次动作前截一张、动作后截一张并判定「生效/没变化/未判定」，返回标出点击点的截图；点击点会画十字和 2 倍放大。目标用 ref（来自 cc_look）或 name 或 x/y。preview=true 只预览不执行。',
      parameters: {
        type: 'object',
        additionalProperties: false,
        properties: {
          action: { type: 'string', enum: ACTIONS, description: '要做的动作' },
          ref: { type: 'number', description: 'cc_look 里的元素编号（最准）' },
          name: { type: 'string', description: '元素名称包含的文字，找不到/重名时用 ref 更稳' },
          x: { type: 'number', description: '目标 x（物理像素）' },
          y: { type: 'number', description: '目标 y（物理像素）' },
          x2: { type: 'number', description: '拖拽终点 x' },
          y2: { type: 'number', description: '拖拽终点 y' },
          duration: { type: 'number', description: '拖拽时长 ms，默认 350' },
          text: { type: 'string', description: 'type 的文本（支持中文）' },
          keys: { type: 'string', description: 'key 的组合键，如 ctrl+s、enter、ctrl+shift+t' },
          button: { type: 'string', enum: ['left', 'right', 'middle'], description: '鼠标键，默认 left' },
          delta: { type: 'number', description: '滚轮增量，负数是往下，默认 -120' },
          times: { type: 'number', description: '滚轮格数，默认 3' },
          expect: { type: 'string', enum: ['change', 'window', 'text', 'still', 'none'], description: '怎么算生效，默认 change（画面有变化）' },
          expect_value: { type: 'string', description: 'expect=window/text 时要出现的内容' },
          timeout_ms: { type: 'number', description: '等结果的时间，默认 1600ms' },
          preview: { type: 'boolean', description: 'true=只在截图上标出会点哪里，不真的执行' },
        },
        required: ['action'],
      },
      output: output(),
      timeoutMs: 120000,
      async execute(args) {
        return act(args || {})
      },
    },
    {
      name: 'cc_verify',
      description: '不动作，只验证：画面有没有变化 / 某个窗口有没有出现 / 窗口文字里有没有某段话 / 画面是否安静。返回当前截图。',
      parameters: {
        type: 'object',
        additionalProperties: false,
        properties: {
          kind: { type: 'string', enum: ['change', 'window', 'text', 'still'], description: '验证什么，默认 change' },
          value: { type: 'string', description: 'kind=window/text 时要匹配的内容' },
          timeout_ms: { type: 'number', description: '最多等多久，默认 3000ms' },
        },
        required: ['kind'],
      },
      output: output(),
      timeoutMs: 60000,
      async execute(args) {
        return verify(args || {})
      },
    },
    {
      name: 'cc_watch',
      description: '观看模式：start 开始按 2 帧/秒盯屏幕（只在画面变化时留图），peek 看最近的变化和刚发生的变化帧，snapshot 拍一张现在的，stop 停。peek/start 会自动切到观看状态。',
      parameters: {
        type: 'object',
        additionalProperties: false,
        properties: {
          action: { type: 'string', enum: ['start', 'stop', 'status', 'peek', 'snapshot'], description: '默认 status' },
          limit: { type: 'number', description: 'peek 返回几帧，默认 3' },
          fps: { type: 'number', description: '采集帧率，默认 2' },
        },
        required: ['action'],
      },
      output: output(),
      timeoutMs: 60000,
      async execute(args) {
        return watch(args || {})
      },
    },
    {
      name: 'cc_learn',
      description: '观看学习：start 开始记录你的一段操作（屏幕变化 + 点击 + 按键 + 窗口），stop 结束并生成时间线，list 看历史，read 读某次记录，save_skill 把流程沉淀成技能，read_skill 读技能。录制时不会动手。',
      parameters: {
        type: 'object',
        additionalProperties: false,
        properties: {
          action: { type: 'string', enum: ['start', 'stop', 'status', 'list', 'read', 'save_skill', 'read_skill'], description: '默认 status' },
          name: { type: 'string', description: 'start 时的任务名 / save_skill 的技能名' },
          id: { type: 'string', description: 'read 时的记录 id' },
          file: { type: 'string', enum: ['timeline.md', 'events.jsonl', 'meta.json'], description: 'read 时读哪个文件，默认 timeline.md' },
          content: { type: 'string', description: 'save_skill 的 markdown 正文' },
        },
        required: ['action'],
      },
      output: output(),
      timeoutMs: 120000,
      async execute(args) {
        return learn(args || {})
      },
    },
    {
      name: 'cc_log',
      description: '最近做过哪些动作、每步的坐标和判定结果（生效/没变化/出错）。',
      parameters: {
        type: 'object',
        additionalProperties: false,
        properties: {
          limit: { type: 'number', description: '返回几条，默认 12' },
        },
      },
      output: output(),
      timeoutMs: 20000,
      async execute(args) {
        return log(args || {})
      },
    },
    {
      name: 'cc_storage',
      description: '查看或清理 computer control 存下来的东西：动作截图（tmp，2 小时自动清）、观看变化帧（watch，每次开始观看清空）、学习记录（sessions，默认保留 30 天）、技能（skills，永不自动删）。clean 时技能不会被删。',
      parameters: {
        type: 'object',
        additionalProperties: false,
        properties: {
          action: { type: 'string', enum: ['status', 'clean'], description: 'status=看占用，clean=清理' },
          area: { type: 'string', enum: ['tmp', 'watch', 'sessions', 'all'], description: 'clean 时清哪块，默认 tmp' },
          days: { type: 'number', description: 'clean sessions 时只删多少天前的，默认 30；0 = 全部删掉' },
        },
        required: ['action'],
      },
      output: output(),
      timeoutMs: 60000,
      async execute(args) {
        return storage(args || {})
      },
    },
  ]
}

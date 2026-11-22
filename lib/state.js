import { existsSync, mkdirSync, readFileSync, writeFileSync, appendFileSync } from 'node:fs'
import { join } from 'node:path'

export const MODES = { idle: '待机', control: '控制电脑', watch: '观看学习' }

/**
 * 插件的状态机：idle / control / watch，持久化到 state.json，
 * 动作流水写 steps.jsonl，模型和人都能回看「第几步做了什么」。
 */
export class State {
  constructor(dataDir) {
    this.dir = dataDir
    this.file = join(dataDir, 'state.json')
    this.stepsFile = join(dataDir, 'steps.jsonl')
    mkdirSync(dataDir, { recursive: true })
    this.data = { mode: 'idle', since: Date.now(), note: '', step: 0, lastAction: '' }
    try {
      const saved = JSON.parse(readFileSync(this.file, 'utf8'))
      if (saved && typeof saved === 'object') Object.assign(this.data, saved)
    } catch {}
    if (!MODES[this.data.mode]) this.data.mode = 'idle'
    if (!Number.isFinite(this.data.step)) this.data.step = 0
  }

  get mode() {
    return this.data.mode
  }

  get modeLabel() {
    return MODES[this.data.mode] || this.data.mode
  }

  get step() {
    return this.data.step
  }

  save() {
    try {
      writeFileSync(this.file, JSON.stringify(this.data, null, 2), 'utf8')
    } catch {}
  }

  setMode(mode, note = '') {
    if (!MODES[mode]) throw new Error(`未知状态：${mode}`)
    const changed = this.data.mode !== mode
    this.data.mode = mode
    this.data.note = note
    this.data.since = Date.now()
    this.data.lastAction = ''
    this.save()
    if (typeof this.onModeChange === 'function') {
      try { this.onModeChange(mode) } catch {}
    }
    return { changed, mode, label: MODES[mode] }
  }

  nextStep() {
    this.data.step = (this.data.step || 0) + 1
    this.save()
    return this.data.step
  }

  /** 悬浮岛上显示的「最近一次操作」，比如 点击 (1200, 340)。 */
  setAction(text) {
    this.data.lastAction = String(text || '')
    this.save()
  }

  record(entry) {
    try {
      appendFileSync(this.stepsFile, JSON.stringify({ t: new Date().toISOString(), ...entry }) + '\n', 'utf8')
    } catch {}
  }

  recent(limit = 12) {
    try {
      if (!existsSync(this.stepsFile)) return []
      const lines = readFileSync(this.stepsFile, 'utf8').trim().split('\n')
      return lines
        .slice(-Math.max(1, limit))
        .map((line) => {
          try {
            return JSON.parse(line)
          } catch {
            return null
          }
        })
        .filter(Boolean)
    } catch {
      return []
    }
  }

  promptLine() {
    const tips = {
      control: '动手前先 cc_look 看清屏幕；点完会拿到判定和带十字的截图。',
      watch: '只看不动：cc_watch peek 看变化，cc_learn start/stop 学操作流程。',
      idle: '待机中：动手先 cc_mode set control，观察先 cc_mode set watch。',
    }
    const note = this.data.note ? `｜${this.data.note}` : ''
    return `【computer control】状态：${MODES[this.data.mode]}｜累计 ${this.data.step} 步${note}\n${tips[this.data.mode] || ''}`
  }
}

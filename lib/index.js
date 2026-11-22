import { homedir } from 'node:os'
import { join, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'
import { existsSync, readdirSync, statSync, unlinkSync, rmSync } from 'node:fs'
import { State } from './state.js'
import { Helper } from './helper.js'
import { createTools } from './tools.js'

export const name = 'computer-control'
export const inject = ['tools', 'attachments', 'systemPrompt']

/**
 * computer control —— 控制电脑 / 观看学习 两种状态，可主动切换。
 *
 * 清理策略：不跑固定周期定时器。每次工具响应结束后排一个 30 分钟后的一次性清理；
 * 重复排期只保留最晚的那个（后一次调用会把前一次取消），避免短时间多次调用重复触发。
 * 到点时如果正在观看/录制，就顺延 30 分钟再试。
 */
export function apply(ctx, config = {}) {
  const pluginRoot = dirname(dirname(fileURLToPath(import.meta.url)))
  const dataDir = config.dataDir || join(process.env.USERPROFILE || homedir(), '.dsh', 'computer-control')

  const log = (text) => {
    try {
      console.error(`[computer-control] ${text}`)
    } catch {}
  }

  const state = new State(dataDir)
  const helper = new Helper({ pluginRoot, dataDir, log })

  const retentionDays = Number.isFinite(Number(config.retentionDays)) ? Number(config.retentionDays) : 30
  const cleanupDelayMs = (
    Number.isFinite(Number(config.cleanupDelayMinutes)) && Number(config.cleanupDelayMinutes) > 0
      ? Number(config.cleanupDelayMinutes)
      : 30
  ) * 60 * 1000

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
        scheduleCleanup(cleanupDelayMs)
        return
      }

      const tmp = join(dataDir, 'tmp')
      if (existsSync(tmp)) {
        const cutoff = Date.now() - 2 * 60 * 60 * 1000
        try {
          for (const file of readdirSync(tmp)) {
            const path = join(tmp, file)
            try {
              if (statSync(path).isFile() && statSync(path).mtimeMs < cutoff) unlinkSync(path)
            } catch {}
          }
        } catch {}
      }

      if (retentionDays > 0) {
        const sessions = join(dataDir, 'sessions')
        if (existsSync(sessions)) {
          const cutoff = Date.now() - retentionDays * 24 * 60 * 60 * 1000
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

  // 重复排期只保留最晚的：新的一次调用直接取消旧的，重新从 30 分钟后算。
  const scheduleCleanup = (delayMs = cleanupDelayMs) => {
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

  const tools = createTools({
    helper,
    state,
    dataDir,
    attachments: ctx.attachments,
    cleanupDue: () => cleanupDueAt,
  })

  for (const tool of tools) {
    const run = tool.execute
    tool.execute = async (args, exec) => {
      try {
        const value = await run(args, exec)
        return JSON.parse(JSON.stringify(value ?? {}))
      } finally {
        // 响应结束后才排清理；失败也算一次活动。
        scheduleCleanup()
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

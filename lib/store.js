import { readdirSync, statSync, rmSync } from 'node:fs'
import { join } from 'node:path'

/** 目录里的文件数和总字节数（递归）。 */
export function dirUsage(dir) {
  let files = 0
  let bytes = 0
  const walk = (current) => {
    let entries = []
    try { entries = readdirSync(current, { withFileTypes: true }) } catch { return }
    for (const entry of entries) {
      const path = join(current, entry.name)
      if (entry.isDirectory()) walk(path)
      else {
        files += 1
        try { bytes += statSync(path).size } catch {}
      }
    }
  }
  walk(dir)
  return { files, bytes }
}

export function fmtBytes(bytes) {
  if (bytes >= 1024 * 1024 * 1024) return `${(bytes / 1024 / 1024 / 1024).toFixed(2)} GB`
  if (bytes >= 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(1)} MB`
  if (bytes >= 1024) return `${Math.round(bytes / 1024)} KB`
  return `${Math.max(0, Math.round(bytes))} B`
}

export function sessionDirs(sessionsDir) {
  const list = []
  try {
    for (const name of readdirSync(sessionsDir)) {
      const path = join(sessionsDir, name)
      try {
        const info = statSync(path)
        if (info.isDirectory()) list.push({ name, path, mtimeMs: info.mtimeMs })
      } catch {}
    }
  } catch {}
  return list
}

/**
 * 清理某一块数据。技能永远不会被删。
 * area: tmp | watch | sessions | all；days 只对 sessions 生效，0 = 全部删。
 */
export function cleanArea(dataDir, area, days = 30) {
  const targets = {
    tmp: join(dataDir, 'tmp'),
    watch: join(dataDir, 'watch'),
    sessions: join(dataDir, 'sessions'),
    skills: join(dataDir, 'skills'),
  }
  let freed = 0
  let removed = 0

  const removeContents = (dir) => {
    let entries = []
    try { entries = readdirSync(dir, { withFileTypes: true }) } catch { return }
    for (const entry of entries) {
      const path = join(dir, entry.name)
      try {
        const size = entry.isDirectory() ? dirUsage(path).bytes : statSync(path).size
        rmSync(path, { recursive: true, force: true })
        freed += size
        removed += 1
      } catch {}
    }
  }

  if (area === 'tmp' || area === 'all') removeContents(targets.tmp)
  if (area === 'watch' || area === 'all') removeContents(targets.watch)
  if (area === 'sessions' || area === 'all') {
    const cutoff = days <= 0 ? Number.MAX_SAFE_INTEGER : Date.now() - days * 24 * 60 * 60 * 1000
    for (const session of sessionDirs(targets.sessions)) {
      if (session.mtimeMs >= cutoff) continue
      try {
        freed += dirUsage(session.path).bytes
        rmSync(session.path, { recursive: true, force: true })
        removed += 1
      } catch {}
    }
  }
  return { removed, freed }
}

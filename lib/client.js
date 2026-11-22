// computer control 的设置页卡片（DSH 设置侧边栏）。
// 浏览器插件格式：window.__ModuleLoader__.load({ id, factory })，不需要构建步骤。
// 数据走同源接口 /api/computer-control（宿主侧 lib/index.js）。
window.__ModuleLoader__.load({
  id: 'dsh-computer-control',
  factory: (require) => {
    var module = { exports: {} }
    var exports = module.exports
    Object.defineProperty(exports, Symbol.toStringTag, { value: 'Module' })
    const React = require('react')
    const { useState, useEffect, useCallback } = React
    const h = React.createElement

    const CSS = `
.cc{--t1:var(--dsw-alias-label-primary,#1f2328);--t2:var(--dsw-alias-label-secondary,#59636e);--t3:var(--dsw-alias-label-tertiary,#8b949e);
--b1:var(--dsw-alias-bg-layer-1,#fff);--b2:var(--dsw-alias-bg-layer-2,#f6f8fa);--bd:var(--dsw-alias-border-l2,#d0d7de);
--brand:var(--dsw-alias-brand-primary,#4d6bfe);--ok:var(--dsw-alias-state-success-primary,#1a7f37);--bad:var(--dsw-alias-state-danger,#d1242f);
--r:var(--dsw-radius-lg,12px);--rs:var(--dsw-radius-md,8px);
display:flex;flex-direction:column;gap:14px;max-width:920px;padding-bottom:24px;color:var(--t1);font-size:14px;line-height:1.55}
.cc-head h2{margin:0;font-size:20px;font-weight:600}
.cc-head p{margin:4px 0 0;color:var(--t2);font-size:13px}
.cc-card{background:var(--b1);border:1px solid var(--bd);border-radius:var(--r);padding:16px;display:flex;flex-direction:column;gap:10px}
.cc-card-h{display:flex;align-items:baseline;justify-content:space-between;gap:8px;flex-wrap:wrap}
.cc-card-h h3{margin:0;font-size:15px;font-weight:600}
.cc-desc{margin:0;color:var(--t2);font-size:13px}
.cc-faint{color:var(--t3);font-size:12px}
.cc-row{display:flex;gap:8px;align-items:center;flex-wrap:wrap}
.cc-big{font-size:20px;font-weight:600}
.cc-badge{display:inline-flex;align-items:center;gap:4px;padding:1px 8px;border-radius:999px;font-size:12px;background:var(--b2);border:1px solid var(--bd);color:var(--t2)}
.cc-badge.brand{color:var(--brand);border-color:color-mix(in srgb,var(--brand) 35%,transparent);background:color-mix(in srgb,var(--brand) 8%,transparent)}
.cc-badge.ok{color:var(--ok);border-color:color-mix(in srgb,var(--ok) 35%,transparent)}
.cc-badge.bad{color:var(--bad);border-color:color-mix(in srgb,var(--bad) 35%,transparent)}
.cc-btn{appearance:none;font:inherit;font-size:13px;padding:5px 12px;border-radius:var(--rs);border:1px solid var(--bd);background:var(--b2);color:var(--t1);cursor:pointer;white-space:nowrap}
.cc-btn:hover{border-color:color-mix(in srgb,var(--t3) 60%,var(--bd))}
.cc-btn[disabled]{opacity:.5;cursor:default}
.cc-btn.primary{background:color-mix(in srgb,var(--brand) 12%,var(--b1));border-color:color-mix(in srgb,var(--brand) 45%,transparent);color:var(--brand)}
.cc-btn.danger{color:var(--bad);border-color:color-mix(in srgb,var(--bad) 35%,transparent)}
.cc-table{display:grid;grid-template-columns:1fr auto;gap:6px 12px;font-size:13px}
.cc-table b{font-variant-numeric:tabular-nums;font-weight:600;text-align:right}
.cc-input{font:inherit;font-size:13px;padding:5px 9px;border-radius:var(--rs);border:1px solid var(--bd);background:var(--b1);color:var(--t1);width:110px}
.cc-note{color:var(--t3);font-size:12px}
.cc-toast{position:sticky;bottom:8px;align-self:flex-start;padding:7px 12px;border-radius:var(--rs);background:var(--b2);border:1px solid var(--bd);font-size:13px}
.cc-toast.bad{color:var(--bad);border-color:color-mix(in srgb,var(--bad) 40%,transparent)}
`;

    if (typeof document !== 'undefined' && !document.getElementById('dsh-computer-control-style')) {
      const style = document.createElement('style')
      style.id = 'dsh-computer-control-style'
      style.textContent = CSS
      document.head.appendChild(style)
    }

    const ROUTE = '/api/computer-control'

    async function unwrap(response) {
      let data = null
      try {
        data = await response.json()
      } catch {
        throw new Error(`接口没有返回 JSON（HTTP ${response.status}），刷新页面或重启 DSH 试试`)
      }
      if (!data.ok) throw new Error(data.error || `HTTP ${response.status}`)
      return data
    }

    const api = {
      get: () => fetch(ROUTE, { credentials: 'same-origin' }).then(unwrap),
      post: (action, body = {}) => fetch(ROUTE, {
        method: 'POST',
        credentials: 'same-origin',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ action, ...body }),
      }).then(unwrap),
    }

    const fmtBytes = (bytes) => {
      const n = Number(bytes) || 0
      if (n >= 1024 ** 3) return `${(n / 1024 ** 3).toFixed(2)} GB`
      if (n >= 1024 ** 2) return `${(n / 1024 ** 2).toFixed(1)} MB`
      if (n >= 1024) return `${Math.round(n / 1024)} KB`
      return `${Math.round(n)} B`
    }

    const MODES = [
      ['idle', '待机'],
      ['control', '控制电脑'],
      ['watch', '观看学习'],
    ]
    const MODE_HINT = {
      idle: '不采集也不动作，随时可以切回来。',
      control: '能点鼠标、能打字；每次动作前截图、动作后判定并回传证据截图。',
      watch: '只看不动：盯画面变化、记录你的点击和按键，用来学习操作流程。',
    }

    function Btn({ children, onClick, kind, disabled }) {
      return h('button', {
        type: 'button',
        className: `cc-btn${kind ? ` ${kind}` : ''}`,
        disabled,
        onClick,
      }, children)
    }

    function Card() {
      const [data, setData] = useState(null)
      const [err, setErr] = useState(null)
      const [busy, setBusy] = useState(false)
      const [msg, setMsg] = useState(null)
      const [learnName, setLearnName] = useState('')
      const [draft, setDraft] = useState({ retentionDays: 30, cleanupDelayMinutes: 30 })

      const load = useCallback(async () => {
        try {
          const next = await api.get()
          setData(next)
          setErr(null)
        } catch (error) {
          setErr(error.message)
        }
      }, [])

      useEffect(() => {
        load()
        const timer = setInterval(load, 5000)
        return () => clearInterval(timer)
      }, [load])

      useEffect(() => {
        if (data?.settings) setDraft({ ...data.settings })
      }, [data?.settings?.retentionDays, data?.settings?.cleanupDelayMinutes])

      const toast = (text, bad = false) => {
        setMsg({ text, bad })
        setTimeout(() => setMsg(null), 4000)
      }

      const run = async (action, body, okText) => {
        setBusy(true)
        try {
          const next = await api.post(action, body)
          setData(next)
          if (okText) toast(okText)
        } catch (error) {
          toast(error.message, true)
        } finally {
          setBusy(false)
        }
      }

      if (!data) {
        return h('div', { className: 'cc' },
          h('div', { className: 'cc-head' }, h('h2', null, 'computer control'), h('p', null, '看得见、点得准、验得出来的电脑控制器。')),
          err ? h('div', { className: 'cc-card' }, h('p', { className: 'cc-desc' }, `读取失败：${err}`), h(Btn, { onClick: load }, '重试'))
            : h('div', { className: 'cc-card' }, h('p', { className: 'cc-desc' }, '正在读取状态…')))
      }

      const mode = data.mode
      const watch = data.watch || {}
      const learn = data.learn || {}
      const overlay = data.overlay || { enabled: true, position: 'top-center', locked: false }
      const storage = data.storage || { tmp: {}, watch: {}, sessions: {}, skills: {}, steps: 0, total: 0 }

      const clean = (area, days, label) => {
        if (area === 'all' && !window.confirm(`确定清理${label}？学习记录会按保留天数删除，技能不动。`)) return
        run('clean', { area, days }, `${label}已清理`)
      }

      return h('div', { className: 'cc' },
        h('div', { className: 'cc-head' },
          h('h2', null, 'computer control'),
          h('span', { className: 'cc-badge' }, `v${data.version || '0.1.0'}`),
          h('p', null, '控制电脑与观看学习两种状态，可随时切换。控制模式每步都带判定和截图，观看模式只在画面变化时留图。')),

        h('section', { className: 'cc-card' },
          h('div', { className: 'cc-card-h' }, h('h3', null, '当前状态'), h('span', { className: 'cc-badge' }, `累计 ${data.step} 步`)),
          h('div', { className: 'cc-row' }, h('span', { className: 'cc-big' }, data.modeLabel)),
          h('div', { className: 'cc-row' }, MODES.map(([id, label]) => h(Btn, {
            key: id,
            kind: mode === id ? 'primary' : undefined,
            disabled: busy,
            onClick: () => run('setMode', { mode: id }, `已切到「${label}」`),
          }, label))),
          h('p', { className: 'cc-desc' }, MODE_HINT[mode] || ''),
          data.helper
            ? h('p', { className: 'cc-faint' },
              `屏幕 ${data.helper.screen?.w ?? '?'}×${data.helper.screen?.h ?? '?'} · 光标 (${data.helper.cursor?.x ?? '?'}, ${data.helper.cursor?.y ?? '?'}) · 前台：${data.helper.foreground || '—'}`)
            : h('p', { className: 'cc-faint' }, 'helper 未启动；第一次用工具或开启观看时会自动启动。')),

        h('section', { className: 'cc-card' },
          h('div', { className: 'cc-card-h' }, h('h3', null, '观看与录制'), h('span', { className: 'cc-faint' }, '录制会同时开启观看采集')),
          h('div', { className: 'cc-row' },
            h('span', { className: watch.running ? 'cc-badge ok' : 'cc-badge' }, watch.running ? `观看中 · ${watch.frames} 帧` : '观看未开始'),
            h(Btn, { disabled: busy || watch.running, onClick: () => run('watchStart', {}, '已开始观看') }, '开始观看'),
            h(Btn, { disabled: busy || !watch.running, onClick: () => run('watchStop', {}, '已停止观看') }, '停止观看')),
          h('div', { className: 'cc-row' },
            h('span', { className: learn.recording ? 'cc-badge ok' : 'cc-badge' }, learn.recording ? `录制中 · ${learn.inputs} 个输入` : '未录制'),
            h('input', {
              className: 'cc-input',
              style: { width: 180 },
              placeholder: '这段操作叫什么',
              value: learnName,
              maxLength: 30,
              onChange: (event) => setLearnName(event.target.value),
            }),
            h(Btn, { disabled: busy || learn.recording, onClick: () => run('learnStart', { name: learnName || '未命名任务' }, '开始录制，你做你的操作就行') }, '开始录制'),
            h(Btn, { disabled: busy || !learn.recording, onClick: () => run('learnStop', {}, '录制结束，时间线已保存') }, '停止录制')),
          h('p', { className: 'cc-note' }, '录制结束后可以在对话里让我把这次操作整理成技能。')),

        h('section', { className: 'cc-card' },
          h('div', { className: 'cc-card-h' }, h('h3', null, '悬浮岛'), h('span', { className: 'cc-faint' }, '置顶显示在屏幕上，可拖动')),
          h('div', { className: 'cc-row' },
            h('label', { className: 'cc-row', style: { gap: 6 } },
              h('input', {
                type: 'checkbox',
                checked: overlay.enabled !== false,
                onChange: (event) => run('saveOverlay', { enabled: event.target.checked }, event.target.checked ? '悬浮岛已开启' : '悬浮岛已关闭'),
              }),
              h('span', null, '启用悬浮岛'))),
          h('div', { className: 'cc-row' },
            h('span', null, '位置'),
            h('select', {
              className: 'cc-input',
              style: { width: 130 },
              value: overlay.position || 'top-center',
              onChange: (event) => run('saveOverlay', { position: event.target.value }, '位置已保存'),
            },
            h('option', { value: 'top-center' }, '顶部居中'),
            h('option', { value: 'top-left' }, '左上角'),
            h('option', { value: 'top-right' }, '右上角'),
            h('option', { value: 'custom' }, '自定义（拖动后）')),
            h('label', { className: 'cc-row', style: { gap: 6 } },
              h('input', {
                type: 'checkbox',
                checked: Boolean(overlay.locked),
                onChange: (event) => run('saveOverlay', { locked: event.target.checked }, event.target.checked ? '位置已锁定' : '位置已解锁'),
              }),
              h('span', null, '锁定位置'))),
          h('div', { className: 'cc-row' },
            h(Btn, { disabled: busy, onClick: () => run('overlayPreview', { durationMs: 5000 }, '悬浮岛显示了 5 秒') }, '预览 5 秒')),
          h('p', { className: 'cc-note' }, '控制电脑或观看学习时弹出；点「停止」立刻结束当前行为；回到待机 2 秒后自动隐藏。拖动悬浮岛会自动记为自定义位置。预览时为了能截图会临时允许被截到。')),

        h('section', { className: 'cc-card' },
          h('div', { className: 'cc-card-h' }, h('h3', null, '存储'), h('span', { className: 'cc-faint' }, `合计 ${fmtBytes(storage.total)}`)),
          h('div', { className: 'cc-table' },
            h('span', null, `动作截图（2 小时自动清）`), h('b', null, `${storage.tmp?.files ?? 0} 个 · ${fmtBytes(storage.tmp?.bytes)}`),
            h('span', null, '观看变化帧'), h('b', null, `${storage.watch?.files ?? 0} 个 · ${fmtBytes(storage.watch?.bytes)}`),
            h('span', null, `学习记录（保留 ${data.settings?.retentionDays ?? 30} 天）`), h('b', null, `${storage.sessions?.count ?? 0} 条 · ${fmtBytes(storage.sessions?.bytes)}`),
            h('span', null, '技能（不自动删）'), h('b', null, `${storage.skills?.files ?? 0} 个 · ${fmtBytes(storage.skills?.bytes)}`)),
          h('div', { className: 'cc-row' },
            h(Btn, { disabled: busy, onClick: () => clean('tmp', 0, '动作截图') }, '清截图'),
            h(Btn, { disabled: busy, onClick: () => clean('watch', 0, '观看变化帧') }, '清变化帧'),
            h(Btn, { disabled: busy, onClick: () => clean('sessions', data.settings?.retentionDays ?? 30, '过期学习记录') }, '清过期记录'),
            h(Btn, { kind: 'danger', disabled: busy, onClick: () => clean('all', data.settings?.retentionDays ?? 30, '截图、变化帧和过期记录') }, '全部清理')),
          h('p', { className: 'cc-note' }, '自动清理：最近一次工具调用后按延迟时间排一次；观看或录制中会顺延。')),

        h('section', { className: 'cc-card' },
          h('div', { className: 'cc-card-h' }, h('h3', null, '设置')),
          h('div', { className: 'cc-row' },
            h('span', null, '学习记录保留'), h('input', {
              className: 'cc-input',
              type: 'number',
              min: 0,
              max: 3650,
              value: draft.retentionDays,
              onChange: (event) => setDraft({ ...draft, retentionDays: event.target.value }),
            }), h('span', { className: 'cc-faint' }, '天（0 = 不自动删）')),
          h('div', { className: 'cc-row' },
            h('span', null, '自动清理延迟'), h('input', {
              className: 'cc-input',
              type: 'number',
              min: 1,
              max: 1440,
              value: draft.cleanupDelayMinutes,
              onChange: (event) => setDraft({ ...draft, cleanupDelayMinutes: event.target.value }),
            }), h('span', { className: 'cc-faint' }, '分钟（每次工具调用后重新计时）')),
          h('div', { className: 'cc-row' },
            h(Btn, {
              kind: 'primary',
              disabled: busy,
              onClick: () => run('saveSettings', {
                retentionDays: Number(draft.retentionDays),
                cleanupDelayMinutes: Number(draft.cleanupDelayMinutes),
              }, '设置已保存'),
            }, '保存设置'),
            h(Btn, { disabled: busy, onClick: () => run('openDataDir', {}, '已打开数据目录') }, '打开数据目录'))),

        msg && h('div', { className: `cc-toast${msg.bad ? ' bad' : ''}`, role: 'status' }, msg.text))
    }

    const inject = ['slots']
    function apply(ctx) {
      ctx.slots.inject('settings.section', () => ctx.slots.register({
        name: 'settings.section',
        id: 'dsh-computer-control',
        order: 118,
        label: () => 'computer control',
        inject: () => ({}),
      }, Card))
    }

    exports.apply = apply
    exports.inject = inject
    exports.Card = Card
    return module.exports
  },
})

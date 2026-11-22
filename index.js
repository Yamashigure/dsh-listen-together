/**
 * 一起听歌 · Host 插件
 *
 * 原生辅助程序负责回环采集、悬浮窗和 WASAPI 控制；这一层只做三件事：
 * 1. 把采集到的状态整理成会话能读的一句话；
 * 2. 给 Agent 两个工具（听什么 / 控制播放与音量）；
 * 3. 在听了一阵子之后，挑一个还活着的会话递一句「说说这段音乐」。
 */
import { spawn, spawnSync } from 'node:child_process'
import { createHash, randomUUID } from 'node:crypto'
import {
  appendFileSync, existsSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync,
} from 'node:fs'
import { homedir } from 'node:os'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

export const name = 'listen-together'
export const inject = ['tools', 'systemPrompt', 'agents', 'commands']

const PACKAGE_DIR = dirname(fileURLToPath(import.meta.url))
const NATIVE_DIR = join(PACKAGE_DIR, 'native')
const SOURCES = ['Native.cs', 'Engine.cs', 'Ui.cs', 'Program.cs']
const LOCAL_APP_DATA = process.env.LOCALAPPDATA || join(homedir(), 'AppData', 'Local')
const CACHE_DIR = join(LOCAL_APP_DATA, 'dsh-listen-together')
const EXE_PATH = join(CACHE_DIR, 'ListenTogether.exe')
const HASH_PATH = join(CACHE_DIR, 'sources.hash')
const PID_PATH = join(CACHE_DIR, 'helper.pid')

const DEFAULT_CONFIG = {
  app: '',
  showWindow: true,
  comment: false,
  commentMinMinutes: 7,
  commentMaxMinutes: 16,
  commentPerTrack: 1,
  lyrics: true,
  sttClipSeconds: 10,
  sttEveryMinutes: 1,
  traceFile: '',
}

const BAND_NAMES = ['63', '125', '250', '500', '1k', '2k', '4k', '8k', '12k']

// ---------------------------------------------------------------- 小工具

function clamp(value, min, max) {
  return Math.min(max, Math.max(min, value))
}

function normalizeConfig(raw) {
  const config = { ...DEFAULT_CONFIG }
  if (raw && typeof raw === 'object') {
    if (typeof raw.app === 'string') config.app = raw.app.trim()
    if (typeof raw.showWindow === 'boolean') config.showWindow = raw.showWindow
    if (typeof raw.comment === 'boolean') config.comment = raw.comment
    if (Number.isFinite(raw.commentMinMinutes)) config.commentMinMinutes = clamp(Number(raw.commentMinMinutes), 1, 180)
    if (Number.isFinite(raw.commentMaxMinutes)) config.commentMaxMinutes = clamp(Number(raw.commentMaxMinutes), 2, 240)
    if (Number.isFinite(raw.commentPerTrack)) config.commentPerTrack = clamp(Math.round(Number(raw.commentPerTrack)), 0, 10)
    if (typeof raw.lyrics === 'boolean') config.lyrics = raw.lyrics
    if (Number.isFinite(raw.sttClipSeconds)) config.sttClipSeconds = clamp(Math.round(Number(raw.sttClipSeconds)), 4, 20)
    if (Number.isFinite(raw.sttEveryMinutes)) config.sttEveryMinutes = clamp(Number(raw.sttEveryMinutes), 0.5, 30)
    if (typeof raw.traceFile === 'string') config.traceFile = raw.traceFile.trim()
  }
  if (config.commentMaxMinutes < config.commentMinMinutes) {
    config.commentMaxMinutes = config.commentMinMinutes + 2
  }
  return config
}

function processNameToLabel(app) {
  if (!app) return '默认输出'
  const base = app.toLowerCase().replace(/\.exe$/i, '')
  const labels = {
    chrome: 'Chrome',
    msedge: 'Edge',
    firefox: 'Firefox',
    brave: 'Brave',
    opera: 'Opera',
    vivaldi: 'Vivaldi',
    qqbrowser: 'QQ 浏览器',
    '360se': '360 浏览器',
    '360chrome': '360 浏览器',
  }
  if (labels[base]) return labels[base]
  return base.charAt(0).toUpperCase() + base.slice(1)
}

function cleanTrackTitle(raw) {
  let title = String(raw || '').replace(/[\u200b-\u200f\u2060\ufeff]/g, '').trim()
  if (!title) return { title: '', source: '' }
  title = title
    .replace(/\s*[-–—]\s*Personal\s*[-–—]\s*Microsoft\s*Edge\s*$/i, '')
    .replace(/\s*[-–—]\s*Microsoft\s*Edge\s*$/i, '')
    .replace(/\s*[-–—]\s*Google\s*Chrome\s*$/i, '')
    .replace(/\s*[-–—]\s*Mozilla\s*Firefox\s*$/i, '')
    .replace(/\s*和另外\s*\d+\s*个页面.*$/u, '')
    .replace(/\s*[-–—]\s*\d+\s*个页面.*$/u, '')
    .trim()
  let source = ''
  if (/[-–—]?\s*哔哩哔哩[_\s-]*bilibili\s*$/u.test(title)) {
    title = title.replace(/[-–—]?\s*哔哩哔哩[_\s-]*bilibili\s*$/u, '')
    source = 'bilibili'
  } else if (/_哔哩哔哩_bilibili\s*$/u.test(title)) {
    title = title.replace(/_哔哩哔哩_bilibili\s*$/u, '')
    source = 'bilibili'
  } else if (/[-–—]\s*哔哩哔哩\s*$/u.test(title)) {
    title = title.replace(/[-–—]\s*哔哩哔哩\s*$/u, '')
    source = 'bilibili'
  } else if (/_bilibili\s*$/i.test(title)) {
    title = title.replace(/_bilibili\s*$/i, '')
    source = 'bilibili'
  } else if (/\s*[-–—]\s*YouTube\s*$/i.test(title)) {
    title = title.replace(/\s*[-–—]\s*YouTube\s*$/i, '')
    source = 'youtube'
  }
  title = title.replace(/\s+/g, ' ').trim()
  if (title === '无标题' || title === 'Untitled' || title === 'New Tab' || title === '新标签页') title = ''
  return { title, source }
}

function formatDuration(ms) {
  if (!Number.isFinite(ms) || ms <= 0) return '刚刚'
  const total = Math.floor(ms / 1000)
  const minutes = Math.floor(total / 60)
  const seconds = total % 60
  if (minutes <= 0) return `${seconds} 秒`
  if (minutes < 60) return `${minutes} 分 ${seconds} 秒`
  const hours = Math.floor(minutes / 60)
  return `${hours} 小时 ${minutes % 60} 分`
}

function loudnessLabel(db) {
  if (!Number.isFinite(db)) return '没有读数'
  if (db < -40) return '很轻'
  if (db < -32) return '偏轻'
  if (db < -24) return '中等'
  if (db < -16) return '偏响'
  return '很响'
}

function dynamicsLabel(avg, deviation) {
  if (!Number.isFinite(avg) || avg <= 0) return '听不出起伏'
  const ratio = deviation / avg
  if (ratio < 0.12) return '很稳，几乎一条线'
  if (ratio < 0.28) return '起伏不大'
  if (ratio < 0.5) return '有点起伏'
  return '起伏明显'
}

function spectrumLabel(bands) {
  if (!Array.isArray(bands) || bands.length < 9) return '频段还没读出来'
  const low = (bands[0] + bands[1] + bands[2]) / 3
  const mid = (bands[3] + bands[4] + bands[5]) / 3
  const high = (bands[6] + bands[7] + bands[8]) / 3
  const parts = []
  if (low > mid * 1.18) parts.push('低频偏多')
  else if (low < mid * 0.7) parts.push('低频很薄')
  if (high > mid * 1.2) parts.push('高频偏亮')
  else if (high < mid * 0.6) parts.push('高频偏少')
  if (parts.length === 0) parts.push('整体比较均衡')
  return parts.join('，')
}

function statsOf(samples, windowMs = 60000) {
  const cutoff = Date.now() - windowMs
  const recent = samples.filter((sample) => sample.t >= cutoff)
  if (recent.length === 0) return null
  let sum = 0
  let sumBands = new Array(9).fill(0)
  let minDb = Infinity
  let maxDb = -Infinity
  let silent = 0
  for (const sample of recent) {
    const rms = Number(sample.rms) || 0
    const db = 20 * Math.log10(rms + 1e-6)
    sum += rms
    minDb = Math.min(minDb, db)
    maxDb = Math.max(maxDb, db)
    if (rms < 0.004) silent++
    if (Array.isArray(sample.bands)) {
      for (let i = 0; i < 9; i++) sumBands[i] += Number(sample.bands[i]) || 0
    }
  }
  const avg = sum / recent.length
  const bands = sumBands.map((value) => value / recent.length)
  let deviation = 0
  for (const sample of recent) deviation += ((Number(sample.rms) || 0) - avg) ** 2
  deviation = Math.sqrt(deviation / recent.length)
  return {
    count: recent.length,
    avg,
    db: 20 * Math.log10(avg + 1e-6),
    minDb,
    maxDb,
    deviation,
    bands,
    silentRatio: silent / recent.length,
  }
}

// ---------------------------------------------------------------- 构建原生程序

function findCompiler() {
  const roots = [
    process.env.SystemRoot || 'C:\\Windows',
  ]
  for (const root of roots) {
    for (const framework of ['Framework64', 'Framework']) {
      const candidate = join(root, 'Microsoft.NET', framework, 'v4.0.30319', 'csc.exe')
      if (existsSync(candidate)) return candidate
    }
  }
  return ''
}

function sourceHash() {
  const hash = createHash('sha256')
  for (const file of SOURCES) {
    const path = join(NATIVE_DIR, file)
    hash.update(file)
    hash.update(existsSync(path) ? readFileSync(path) : Buffer.from(`missing:${file}`))
  }
  return hash.digest('hex')
}

function buildNative(log) {
  mkdirSync(CACHE_DIR, { recursive: true })
  const wanted = sourceHash()
  if (existsSync(EXE_PATH) && existsSync(HASH_PATH)) {
    try {
      if (readFileSync(HASH_PATH, 'utf8').trim() === wanted) return EXE_PATH
    } catch { /* 重新编译 */ }
  }
  const csc = findCompiler()
  if (!csc) throw new Error('没有找到 csc.exe（.NET Framework 4.x），原生辅助程序编译不了。')
  log(`build: ${csc}`)
  const args = [
    '/nologo',
    '/target:winexe',
    `/out:${EXE_PATH}`,
    '/r:System.Windows.Forms.dll',
    '/r:System.Drawing.dll',
    ...SOURCES.map((file) => join(NATIVE_DIR, file)),
  ]
  const result = spawnSync(csc, args, { encoding: 'utf8', windowsHide: true, timeout: 120000 })
  if (result.error) throw result.error
  if (result.status !== 0) {
    const detail = `${result.stdout || ''}\n${result.stderr || ''}`.trim().slice(0, 2000)
    throw new Error(`原生辅助程序编译失败（exit ${result.status}）：${detail}`)
  }
  if (!existsSync(EXE_PATH)) throw new Error('原生辅助程序编译后没有找到 exe。')
  writeFileSync(HASH_PATH, wanted, 'utf8')
  log(`build: ok ${EXE_PATH}`)
  return EXE_PATH
}

function killStaleHelper(log) {
  try {
    if (!existsSync(PID_PATH)) return
    const pid = Number(readFileSync(PID_PATH, 'utf8').trim())
    if (!Number.isInteger(pid) || pid <= 0) return
    try {
      process.kill(pid, 0)
      log(`kill stale helper ${pid}`)
      spawnSync('taskkill', ['/PID', String(pid), '/F'], { windowsHide: true })
    } catch { /* 已经不在了 */ }
  } catch { /* 忽略 */ }
}

// ---------------------------------------------------------------- 插件

export function apply(ctx, rawConfig) {
  const config = normalizeConfig(rawConfig)
  const logPrefix = '[listen-together]'
  const log = (line) => {
    const text = `${new Date().toISOString()} ${line}`
    try {
      if (config.traceFile) appendFileSync(config.traceFile, `${text}\n`, 'utf8')
    } catch { /* trace 只用于排查 */ }
    try { ctx.logger?.(name)?.info?.(line) } catch { /* 可选 */ }
  }

  const music = {
    playing: false,
    app: '',
    title: '',
    rawTitle: '',
    source: '',
    volume: 100,
    muted: false,
    rms: 0,
    peak: 0,
    bands: [],
    capture: false,
    error: '',
    updatedAt: 0,
    trackStartedAt: 0,
    listeningStartedAt: 0,
    pausedSince: 0,
    pendingTitle: '',
    pendingSource: '',
    pendingSince: 0,
    samples: [],
    lastTracks: [],
    commentsForTrack: 0,
    commentsTotal: 0,
    lastCommentAt: Date.now(),
    nextGapMs: 0,
    heard: [],
    lastRecordAt: 0,
  }

  const startedAt = Date.now()
  const lastActivity = new Map()
  let child = null
  let ready = false
  let disposing = false
  let restarts = 0
  let restartTimer = null
  let commentTimer = null
  let staleTimer = null
  let recordTimer = null
  let stdoutBuffer = ''
  const NONCE = randomUUID().replace(/-/g, '')
  let uiVisible = config.showWindow
  let settings = { width: 0, height: 0, opacity: 0.97, locked: false, background: '', waveColor: '#8fa8ff', waveOpacity: 78 }
  let sttPreparing = false

  // ------------------------------------------------------------ 原生进程

  function writeState(force) {
    if (!child || !child.stdin || child.stdin.destroyed) return
    try {
      child.stdin.write('state\n')
      if (force) log('helper: state requested')
    } catch { /* 管道断开会在 exit 里处理 */ }
  }

  function sendCommand(command) {
    if (!child || !child.stdin || child.stdin.destroyed) return false
    try {
      child.stdin.write(`${command}\n`)
      log(`helper <- ${command}`)
      return true
    } catch (error) {
      log(`helper write failed: ${error?.message || error}`)
      return false
    }
  }

  function startHelper() {
    if (disposing) return
    let exe
    try {
      exe = buildNative(log)
    } catch (error) {
      music.error = error instanceof Error ? error.message : String(error)
      log(`disabled: ${music.error}`)
      return
    }
    killStaleHelper(log)
    const args = []
    if (config.app) args.push('--pin', config.app)
    if (!config.showWindow) args.push('--hidden')
    args.push('--exclude-pid', String(process.pid))
    log(`spawn: ${exe} ${args.join(' ')}`)
    stdoutBuffer = ''
    ready = false
    try {
      child = spawn(exe, args, {
        cwd: CACHE_DIR,
        windowsHide: true,
        stdio: ['pipe', 'pipe', 'pipe'],
      })
    } catch (error) {
      music.error = error instanceof Error ? error.message : String(error)
      log(`spawn failed: ${music.error}`)
      return
    }
    child.stdout.setEncoding('utf8')
    child.stdout.on('data', handleStdout)
    child.stderr.setEncoding('utf8')
    child.stderr.on('data', (chunk) => {
      const text = String(chunk).trim()
      if (text) log(`helper stderr: ${text.slice(0, 500)}`)
    })
    child.on('error', (error) => {
      music.error = error instanceof Error ? error.message : String(error)
      log(`helper error: ${music.error}`)
    })
    child.on('exit', (code, signal) => {
      log(`helper exit code=${code} signal=${signal}`)
      child = null
      ready = false
      music.playing = false
      music.error = `辅助程序退出（code ${code ?? '?'}）`
      try { rmSync(PID_PATH, { force: true }) } catch { /* 忽略 */ }
      if (disposing) return
      restarts++
      if (restarts > 5) {
        log('helper: too many restarts, giving up')
        return
      }
      const delay = Math.min(30000, 2000 * restarts)
      restartTimer = setTimeout(() => {
        restartTimer = null
        startHelper()
      }, delay)
    })
  }

  function handleStdout(chunk) {
    stdoutBuffer += chunk
    if (stdoutBuffer.length > 128000) stdoutBuffer = stdoutBuffer.slice(-64000)
    let index
    while ((index = stdoutBuffer.indexOf('\n')) >= 0) {
      const line = stdoutBuffer.slice(0, index).trim()
      stdoutBuffer = stdoutBuffer.slice(index + 1)
      if (!line) continue
      let message
      try {
        message = JSON.parse(line)
      } catch {
        log(`helper: unparsed ${line.slice(0, 200)}`)
        continue
      }
      handleMessage(message)
      if (stdoutBuffer.length === 0) break
    }
  }

  function handleMessage(message) {
    if (!message || typeof message !== 'object') return
    switch (message.type) {
      case 'ready':
        ready = true
        restarts = 0
        music.error = ''
        try { writeFileSync(PID_PATH, String(message.pid ?? child?.pid ?? ''), 'utf8') } catch { /* 忽略 */ }
        log(`ready pid=${message.pid}`)
        if (!config.showWindow) sendCommand('hide')
        writeState(true)
        break
      case 'state':
        updateState(message)
        break
      case 'action':
        log(`helper -> action ${message.action ?? ''} ${message.app ?? ''} ${message.volume ?? ''}`)
        break
      case 'error':
        log(`helper -> error ${message.message ?? ''}`)
        break
      case 'ui':
        if (message.action === 'show') uiVisible = true
        else if (message.action === 'hide') uiVisible = false
        log(`helper -> ui ${message.action ?? ''}`)
        break
      case 'settings':
        settings = {
          width: Number.isFinite(message.width) ? Number(message.width) : settings.width,
          height: Number.isFinite(message.height) ? Number(message.height) : settings.height,
          opacity: Number.isFinite(message.opacity) ? Number(message.opacity) : settings.opacity,
          locked: Boolean(message.locked),
          background: typeof message.background === 'string' ? message.background : settings.background,
          waveColor: typeof message.waveColor === 'string' ? message.waveColor : settings.waveColor,
          waveOpacity: Number.isFinite(message.waveOpacity) ? Number(message.waveOpacity) : settings.waveOpacity,
        }
        break
      case 'wav':
        handleWav(message).catch((error) => log(`stt: ${error?.message || error}`))
        break
      default:
        break
    }
  }

  function updateState(message) {
    const now = Date.now()
    const wasPlaying = music.playing
    music.playing = Boolean(message.playing)
    if (music.playing && !wasPlaying) {
      if (!music.listeningStartedAt || (music.pausedSince && now - music.pausedSince > 120000)) {
        music.listeningStartedAt = now
      }
      music.pausedSince = 0
    } else if (!music.playing && wasPlaying) {
      music.pausedSince = now
    }
    if (!music.playing && music.pausedSince && now - music.pausedSince > 120000) {
      music.listeningStartedAt = 0
    }
    music.app = typeof message.app === 'string' ? message.app : ''
    music.rawTitle = typeof message.title === 'string' ? message.title : ''
    music.volume = Number.isFinite(message.volume) ? Number(message.volume) : music.volume
    music.muted = Boolean(message.muted)
    music.rms = Number.isFinite(message.rms) ? Number(message.rms) : 0
    music.peak = Number.isFinite(message.peak) ? Number(message.peak) : 0
    music.capture = Boolean(message.capture)
    music.error = typeof message.error === 'string' ? message.error : ''
    music.bands = Array.isArray(message.bands) ? message.bands.slice(0, 9).map(Number) : music.bands
    music.updatedAt = now

    // 浏览器切标签会让窗口标题一直变，等它稳住两秒再当成一首新曲子。
    const cleaned = cleanTrackTitle(music.rawTitle)
    if (cleaned.title !== music.title || cleaned.source !== music.source) {
      if (cleaned.title !== music.pendingTitle || cleaned.source !== music.pendingSource) {
        music.pendingTitle = cleaned.title
        music.pendingSource = cleaned.source
        music.pendingSince = now
      } else if (now - music.pendingSince >= 2000) {
        applyTrack(cleaned, now)
      }
    }

    music.samples.push({ t: now, rms: music.rms, bands: music.bands })
    if (music.samples.length > 900) music.samples.splice(0, music.samples.length - 900)
  }

  function applyTrack(cleaned, now) {
    const previous = music.title
    music.title = cleaned.title
    music.source = cleaned.source
    music.trackStartedAt = now
    music.commentsForTrack = 0
    music.nextGapMs = randomGap()
    music.pendingSince = 0
    if (cleaned.title && cleaned.title !== previous) {
      music.lastTracks.unshift({ title: cleaned.title, app: music.app, at: now })
      if (music.lastTracks.length > 20) music.lastTracks.length = 20
      log(`track: ${cleaned.title} (${cleaned.source || music.app})`)
    }
  }

  // ------------------------------------------------------------ 会话与评论

  function trackActivity(session) {
    const id = session?.id
    if (typeof id !== 'string' || !id) return
    lastActivity.set(id, Date.now())
    if (lastActivity.size > 500) {
      let oldestId = ''
      let oldest = Infinity
      for (const [key, value] of lastActivity) {
        if (value < oldest) {
          oldest = value
          oldestId = key
        }
      }
      if (oldestId) lastActivity.delete(oldestId)
    }
  }

  function rootAgents() {
    const list = ctx.agents.list().filter((agent) => {
      if (!agent || typeof agent.id !== 'string') return false
      if (typeof ctx.agents.roots === 'function') {
        try { return ctx.agents.roots().includes(agent) } catch { return true }
      }
      return true
    })
    return list
  }

  function pickAgent() {
    const cutoff = Date.now() - 30 * 60 * 1000
    const agents = rootAgents()
    let best = null
    let bestAt = 0
    for (const agent of agents) {
      const at = lastActivity.get(agent.id) || 0
      if (at < cutoff) continue
      if (at > bestAt) {
        bestAt = at
        best = agent
      }
    }
    if (!best && agents.length === 1) {
      const only = agents[0]
      const at = lastActivity.get(only.id) || 0
      if (at >= cutoff || lastActivity.size === 0) best = only
    }
    return best
  }

  function randomGap() {
    const min = config.commentMinMinutes * 60000
    const max = config.commentMaxMinutes * 60000
    return Math.round(min + Math.random() * Math.max(0, max - min))
  }

  function snapshot(windowMs = 60000) {
    const stats = statsOf(music.samples, windowMs)
    return {
      stats,
      db: stats ? stats.db : NaN,
      loudness: stats ? loudnessLabel(stats.db) : '',
      dynamics: stats ? dynamicsLabel(stats.avg, stats.deviation) : '',
      spectrum: spectrumLabel(music.bands),
      silentRatio: stats ? stats.silentRatio : 1,
    }
  }

  function nowText() {
    const label = processNameToLabel(music.app)
    if (!music.capture) {
      return `一起听歌 · 采集没有起来${music.error ? `（${music.error}）` : ''}`
    }
    if (!music.title && !music.playing) {
      return `一起听歌 · 还没有听到声音${music.app ? `（来源 ${label}）` : ''}`
    }
    const snap = snapshot()
    const lines = []
    lines.push(`一起听歌 · ${music.playing ? '正在播放' : '停着'}`)
    lines.push(`来源：${label}${music.app ? `（${music.app}）` : ''}  音量：${music.muted ? '静音' : music.volume}`)
    if (music.title) {
      lines.push(`曲目：${music.title}${music.source === 'bilibili' ? '（哔哩哔哩）' : music.source === 'youtube' ? '（YouTube）' : ''}`)
      const listened = music.listeningStartedAt ? Date.now() - music.listeningStartedAt : 0
      lines.push(`已听：${formatDuration(listened)}`)
    }
    const recent = music.samples.filter((sample) => sample.t >= Date.now() - 5000)
    if (recent.length) {
      const recentRms = recent.reduce((sum, sample) => sum + (Number(sample.rms) || 0), 0) / recent.length
      lines.push(`此刻：${recentRms > 0.02 ? '有声音' : recentRms > 0.004 ? '很轻' : '安静'}`)
    }
    if (snap.stats) {
      lines.push(`响度：${snap.loudness}（${snap.db.toFixed(0)} dBFS）`)
      lines.push(`走势：${snap.dynamics}`)
      lines.push(`频段：${snap.spectrum}`)
      if (snap.silentRatio > 0.5) lines.push('最近一半时间是安静的，可能暂停了。')
    } else {
      lines.push('还没有足够的声音读数。')
    }
    const previous = music.lastTracks.filter((entry) => entry.title !== music.title).slice(0, 3)
    if (previous.length) lines.push(`之前听过：${previous.map((entry) => entry.title).join(' / ')}`)
    if (music.heard.length) lines.push(`听写片段（机器识别，可能不准）：${music.heard.slice(-2).join(' / ')}`)
    return lines.join('\n')
  }

  function commentPrompt() {
    const snap = snapshot()
    const label = processNameToLabel(music.app)
    const parts = []
    parts.push(music.title ? `[一起听歌] 现在在放《${music.title}》` : '[一起听歌] 现在耳机里有一段音乐，暂时没读到名字')
    const listened = music.listeningStartedAt ? Date.now() - music.listeningStartedAt : 0
    parts.push(`已经听了 ${formatDuration(listened)}`)
    parts.push(`来源是 ${label}，音量 ${music.muted ? '静音' : music.volume}`)
    if (snap.stats) {
      parts.push(`响度${snap.loudness}，${snap.dynamics}，${snap.spectrum}`)
    }
    if (snap.silentRatio > 0.5) parts.push('不过有一半时间是安静的')
    if (music.heard.length) parts.push(`听写里隐约听到「${music.heard[music.heard.length - 1]}」`)
    return `${parts.join('；')}。说一句你对这段音乐的感觉。短一点，像随口接的一句话；不用报这些参数，也不用提插件，觉得一般就照实说。`
  }

  function maybeComment() {
    if (!config.comment || disposing) return
    if (!music.playing || !music.capture) return
    if (!music.listeningStartedAt || Date.now() - music.listeningStartedAt < 120000) return
    if (Date.now() - music.trackStartedAt < 30000) return
    if (music.commentsForTrack >= config.commentPerTrack) return
    if (music.nextGapMs === 0) music.nextGapMs = randomGap()
    if (Date.now() - music.lastCommentAt < music.nextGapMs) return
    const agent = pickAgent()
    if (!agent) return
    if (agent.status !== 'idle') return
    const text = commentPrompt()
    try {
      agent.followup({
        content: [{ type: 'text', text }],
        source: { kind: 'listen-together' },
      })
      music.lastCommentAt = Date.now()
      music.lastCommentAtTrack = music.trackStartedAt
      music.commentsForTrack++
      music.commentsTotal++
      music.nextGapMs = randomGap()
      log(`comment -> ${agent.id} (${music.title})`)
    } catch (error) {
      log(`comment failed: ${error?.message || error}`)
    }
  }

  // ------------------------------------------------------------ 工具与提示词

  function registerTool(def) {
    const output = {
      schema: { type: 'string' },
      render: (_args, value) => [{ type: 'text', text: typeof value === 'string' ? value : JSON.stringify(value) }],
    }
    const full = { ...def, output }
    if (typeof ctx.effect === 'function') ctx.effect(() => ctx.tools.register(full), `listen-together:${def.name}`)
    else ctx.tools.register(full)
  }

  registerTool({
    name: 'listen_now',
    description: '读取用户此刻正在听的音乐：来源应用、曲目、音量、响度与频段的走势。用户问「我在听什么」或者想聊正在放的歌时用它；不要凭聊天内容猜歌名。',
    parameters: {
      type: 'object',
      properties: {
        detail: {
          type: 'string',
          enum: ['brief', 'full'],
          description: 'brief 只给曲目和状态；full 带响度、频段和最近听过的曲目。',
        },
      },
      additionalProperties: false,
    },
    execute: async (args = {}) => {
      if (music.error && !music.capture) return nowText()
      if (args.detail === 'brief') {
        return music.title
          ? `${music.title}${music.source === 'bilibili' ? '（哔哩哔哩）' : ''} · ${music.playing ? '正在播放' : '停着'} · 来源 ${processNameToLabel(music.app)}`
          : '还没有听到声音。'
      }
      return nowText()
    },
  })

  registerTool({
    name: 'listen_feel',
    description: '当用户问起正在听的这首歌唱起来是什么感觉、想聊聊这段音乐时用它读取当前曲目、响度、走势、频段和听写片段。只在用户问了之后调用，不要主动调用；插件不会自己插话。',
    parameters: {
      type: 'object',
      properties: {},
      additionalProperties: false,
    },
    execute: async () => `${nowText()}\n\n用你自己的话回一句；不用报这些参数，也不用提插件或工具名。`,
  })

  registerTool({
    name: 'listen_control',
    description: '控制正在听的声音：暂停/继续、调来源应用的音量、显示或隐藏悬浮窗。这些操作直接作用在来源应用上，不是只改这一边的声音。',
    parameters: {
      type: 'object',
      properties: {
        action: {
          type: 'string',
          enum: ['pause', 'resume', 'toggle', 'volume', 'mute', 'unmute', 'show', 'hide', 'status'],
          description: '要做的动作。',
        },
        volume: {
          type: 'number',
          description: 'action 为 volume 时使用，0 到 100。',
        },
      },
      required: ['action'],
      additionalProperties: false,
    },
    execute: async (args = {}) => {
      const action = String(args.action || '')
      if (!ready) return `辅助程序还没起来${music.error ? `：${music.error}` : '。'}`
      if (action === 'status') return nowText()
      if (action === 'volume') {
        const volume = clamp(Math.round(Number(args.volume)), 0, 100)
        if (!Number.isFinite(volume)) return '音量要给一个 0 到 100 的数。'
        if (!sendCommand(`volume ${volume}`)) return '没有连上辅助程序。'
        await wait(300)
        return `音量调到 ${volume}${music.app ? `（${processNameToLabel(music.app)}）` : ''}。`
      }
      if (action === 'mute' || action === 'unmute') {
        const wantMuted = action === 'mute'
        if (music.muted !== wantMuted) sendCommand('mute')
        await wait(300)
        return wantMuted ? '静音了。' : '取消静音了。'
      }
      if (action === 'show' || action === 'hide') {
        uiVisible = action === 'show'
        sendCommand(action)
        await wait(200)
        return action === 'show' ? '悬浮窗出来了。' : '悬浮窗收起来了。'
      }
      if (action === 'pause') {
        if (!music.playing) return '本来就是停着的。'
        sendCommand('toggle')
        await wait(1200)
        if (music.playing) return '已经发过去了，不过来源那边好像还没停。可能得手动按一下。'
        return '暂停了。'
      }
      if (action === 'resume') {
        if (music.playing) return '已经在放了。'
        sendCommand('toggle')
        await wait(1200)
        return music.playing ? '继续了。' : '发出去了，好像还没响。'
      }
      if (action === 'toggle') {
        sendCommand('toggle')
        await wait(1200)
        return music.playing ? '现在是播放中。' : '现在停着。'
      }
      return `不认识的 action：${action}`
    },
  })

  function wait(ms) {
    return new Promise((resolve) => setTimeout(resolve, ms))
  }

  const sectionText = `## 一起听歌
用户装了「一起听歌」，可能正用浏览器（多半是 B 站）放歌。想知道现在放什么，用 listen_now；要暂停、继续、调来源应用的音量，用 listen_control。曲名常常是视频标题，别把它当成标准歌名再去搜。
用户问起正在听的歌给人的感觉、想聊聊这段音乐时，用 listen_feel 读一下当前音乐，再按你自己的话说；不用报参数，也不用提插件或工具名。插件不会主动递话。`

  if (typeof ctx.effect === 'function') {
    ctx.effect(() => ctx.systemPrompt.section({
      name: 'listen-together',
      order: 860,
      text: () => sectionText,
    }), 'listen-together:prompt')
  } else {
    ctx.systemPrompt.section({ name: 'listen-together', order: 860, text: () => sectionText })
  }

  ctx.on('session/event', (session) => trackActivity(session), { global: true })

  // ------------------------------------------------------------ 面板桥（Web UI 小按钮）
  function replyJson(res, status, payload) {
    try {
      res.writeHead(status, { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store' })
      res.end(JSON.stringify(payload))
    } catch { /* 客户端可能已经断开 */ }
  }

  function readBody(req) {
    return new Promise((resolve, reject) => {
      const chunks = []
      let size = 0
      req.on('data', (chunk) => {
        size += chunk.length
        if (size > 12 * 1024 * 1024) {
          reject(new Error('body too large'))
          req.destroy()
          return
        }
        chunks.push(chunk)
      })
      req.on('end', () => {
        try { resolve(Buffer.concat(chunks).toString('utf8')) } catch (error) { reject(error) }
      })
      req.on('error', reject)
    })
  }

  let serverOrigin = ''

  function requestAllowed(req) {
    if (req.headers['x-listen-together'] === NONCE) return true
    // 页面刚加载时可能还没拿到 nonce：同源请求（浏览器自己发的）也放行。
    const origin = req.headers.origin
    if (origin && serverOrigin && origin === serverOrigin) return true
    return req.headers['sec-fetch-site'] === 'same-origin'
  }

  function uiSnapshot() {
    return {
      ok: true,
      visible: uiVisible,
      settings: {
        width: settings.width || 356,
        height: settings.height || 116,
        opacity: settings.opacity,
        locked: settings.locked,
        background: settings.background,
        waveColor: settings.waveColor,
        waveOpacity: settings.waveOpacity,
      },
      music: {
        playing: music.playing,
        title: music.title,
        app: processNameToLabel(music.app),
        lyrics: music.heard.slice(-3),
      },
    }
  }

  function applyUiControl(body) {
    if (!body || typeof body !== 'object') throw new Error('bad body')
    const action = String(body.action || '')
    const numberOr = (value, fallback) => (Number.isFinite(value) ? Number(value) : fallback)
    switch (action) {
      case 'toggle':
        uiVisible = !uiVisible
        sendCommand(uiVisible ? 'show' : 'hide')
        break
      case 'show':
        uiVisible = true
        sendCommand('show')
        break
      case 'hide':
        uiVisible = false
        sendCommand('hide')
        break
      case 'lock':
        settings.locked = Boolean(body.value)
        sendCommand(`lock ${settings.locked ? 'on' : 'off'}`)
        break
      case 'size': {
        const width = clamp(Math.round(numberOr(body.width, 356)), 280, 1200)
        const height = clamp(Math.round(numberOr(body.height, 116)), 96, 600)
        settings.width = width
        settings.height = height
        sendCommand(`size ${width} ${height}`)
        break
      }
      case 'opacity': {
        const value = clamp(numberOr(body.value, 0.97), 0.25, 1)
        settings.opacity = Math.round(value * 100) / 100
        sendCommand(`opacity ${settings.opacity}`)
        break
      }
      case 'background-clear':
        settings.background = ''
        sendCommand('bg none')
        break
      case 'background-pick':
        sendCommand('pickbg')
        break
      case 'background-edit':
        sendCommand('bgedit')
        break
      case 'wave': {
        const color = typeof body.color === 'string' ? body.color.trim() : ''
        if (!/^#[0-9a-fA-F]{6}$/.test(color)) throw new Error('waveColor must be #rrggbb')
        const opacity = clamp(Math.round(numberOr(body.opacity, 78)), 0, 100)
        settings.waveColor = color
        settings.waveOpacity = opacity
        sendCommand(`wave ${color} ${opacity}`)
        break
      }
      default:
        throw new Error(`unknown action ${action}`)
    }
    return uiSnapshot()
  }

  // 面板按钮走 session command：dsh-app:// 页面里相对 fetch 到不了宿主，命令是现成的 RPC。
  ctx.effect(() => ctx.commands.register({
    name: 'listen-together',
    description: '一起听歌：开关悬浮窗、调尺寸或透明度',
    handler: (invocation) => {
      const parts = String(invocation.rawInput || '').trim().split(/\s+/).filter(Boolean)
      const action = parts[0] || 'toggle'
      try {
        if (action === 'size') {
          const width = Number(parts[1])
          const height = Number(parts[2])
          if (!Number.isFinite(width) || !Number.isFinite(height)) {
            return { kind: 'error', text: '尺寸要写两个数字，比如 /listen-together size 430 140。' }
          }
          applyUiControl({ action: 'size', width, height })
          return { kind: 'success', text: `悬浮窗改成 ${width}×${height}。` }
        }
        if (action === 'opacity') {
          const value = Number(parts[1])
          if (!Number.isFinite(value)) return { kind: 'error', text: '透明度要写 0.25 到 1 之间的小数。' }
          applyUiControl({ action: 'opacity', value })
          return { kind: 'success', text: '透明度改好了。' }
        }
        if (action === 'lock') {
          const on = parts[1] !== 'off' && parts[1] !== 'false'
          applyUiControl({ action: 'lock', value: on })
          return { kind: 'success', text: on ? '锁上了，除了小锁都点不到。' : '解锁了。' }
        }
        if (action === 'background-pick') {
          applyUiControl({ action: 'background-pick' })
          return { kind: 'success', text: '弹了一张选图窗口。' }
        }
        if (action === 'background-clear') {
          applyUiControl({ action: 'background-clear' })
          return { kind: 'success', text: '背景清掉了。' }
        }
        if (action === 'background-edit') {
          applyUiControl({ action: 'background-edit' })
          return { kind: 'success', text: '在悬浮窗里拖动背景，点“确定”收好。' }
        }
        if (action === 'wave') {
          const color = parts[1] || ''
          const opacity = Number(parts[2])
          if (!/^#[0-9a-fA-F]{6}$/.test(color) || !Number.isFinite(opacity)) {
            return { kind: 'error', text: '波形参数要写成 /listen-together wave #8fa8ff 78。' }
          }
          applyUiControl({ action: 'wave', color, opacity })
          return { kind: 'success', text: '波形颜色改好了。' }
        }
        if (action === 'toggle' || action === 'show' || action === 'hide') {
          const next = applyUiControl({ action })
          return { kind: 'success', text: next.visible ? '悬浮窗开着。' : '悬浮窗收起来了。' }
        }
        return { kind: 'error', text: `不认识子命令 ${action}。` }
      } catch (error) {
        return { kind: 'error', text: error instanceof Error ? error.message : String(error) }
      }
    },
  }), 'listen-together: command')

  if (typeof ctx.inject === 'function') {
    ctx.inject(['webServer'], (webCtx) => {
      const web = webCtx.webServer
      if (!web || typeof web.register !== 'function') return
      try { serverOrigin = web.getOrigin() } catch { serverOrigin = '' }
      const disposers = []
      disposers.push(web.register({
        kind: 'exact',
        path: '/listen-together/state',
        handler: async (req, res) => {
          if (req.method !== 'GET') return replyJson(res, 405, { ok: false, error: 'method' })
          if (!requestAllowed(req)) return replyJson(res, 403, { ok: false, error: 'stale' })
          replyJson(res, 200, uiSnapshot())
        },
      }))
      disposers.push(web.register({
        kind: 'exact',
        path: '/listen-together/control',
        handler: async (req, res) => {
          if (req.method !== 'POST') return replyJson(res, 405, { ok: false, error: 'method' })
          if (!requestAllowed(req)) return replyJson(res, 403, { ok: false, error: 'stale' })
          try {
            const body = JSON.parse(await readBody(req))
            replyJson(res, 200, applyUiControl(body))
          } catch (error) {
            replyJson(res, 400, { ok: false, error: error instanceof Error ? error.message : String(error) })
          }
        },
      }))
      disposers.push(web.register({
        kind: 'exact',
        path: '/listen-together/background',
        handler: async (req, res) => {
          if (req.method !== 'POST') return replyJson(res, 405, { ok: false, error: 'method' })
          if (!requestAllowed(req)) return replyJson(res, 403, { ok: false, error: 'stale' })
          try {
            const body = JSON.parse(await readBody(req))
            const dataUrl = typeof body?.dataUrl === 'string' ? body.dataUrl : ''
            const match = /^data:image\/(png|jpeg);base64,([A-Za-z0-9+/=]+)$/.exec(dataUrl)
            if (!match) return replyJson(res, 400, { ok: false, error: 'image must be png or jpeg' })
            const bytes = Buffer.from(match[2], 'base64')
            if (bytes.length > 8 * 1024 * 1024) return replyJson(res, 400, { ok: false, error: 'image too large' })
            mkdirSync(CACHE_DIR, { recursive: true })
            const file = join(CACHE_DIR, match[1] === 'png' ? 'background.png' : 'background.jpg')
            const other = join(CACHE_DIR, match[1] === 'png' ? 'background.jpg' : 'background.png')
            writeFileSync(file, bytes)
            try { rmSync(other, { force: true }) } catch { /* 忽略 */ }
            settings.background = file
            sendCommand(`bg ${file}`)
            log(`background: ${bytes.length} bytes -> ${file}`)
            replyJson(res, 200, uiSnapshot())
          } catch (error) {
            replyJson(res, 400, { ok: false, error: error instanceof Error ? error.message : String(error) })
          }
        },
      }))
      const tap = web.tapIndex((html) => {
        const script = `<script>window.__LISTEN_TOGETHER__=${JSON.stringify({ nonce: NONCE, base: '/listen-together' })};</script>`
        return html.includes('</head>') ? html.replace('</head>', `${script}</head>`) : `${html}${script}`
      })
      webCtx.effect(() => () => {
        for (const dispose of disposers) {
          try { dispose() } catch { /* 忽略 */ }
        }
        try { tap() } catch { /* 忽略 */ }
      }, 'listen-together:web')
    })
  }

  // ------------------------------------------------------------ 听写（语音识别）
  function sttProvider() {
    const stt = ctx.get('speechToText')
    if (!stt) return null
    try {
      const snap = stt.snapshot()
      const provider = snap.providers.find((entry) => entry.id === snap.selection?.providerId) || snap.providers[0]
      return provider ? { stt, provider, selection: snap.selection } : null
    } catch { return null }
  }

  function prepareStt() {
    const found = sttProvider()
    if (!found) return
    const phase = found.provider.preparation?.phase
    if (phase === 'ready' || phase === 'standby') { sttPreparing = false; return }
    if (phase === 'failed' && found.provider.preparation?.message && !sttPreparing) {
      log(`stt unavailable: ${found.provider.preparation.message}`)
      sttPreparing = true
      return
    }
    if (sttPreparing) return
    sttPreparing = true
    try {
      found.stt.prepare(found.provider.id)
      log(`stt prepare ${found.provider.id} (${phase || 'unprepared'})`)
    } catch (error) {
      log(`stt prepare failed: ${error?.message || error}`)
    }
    setTimeout(() => { sttPreparing = false }, 20000)
  }

  async function handleWav(message) {
    if (!config.lyrics) return
    const path = typeof message.path === 'string' ? message.path : ''
    if (!path) return
    const found = sttProvider()
    if (!found) return
    const phase = found.provider.preparation?.phase
    if (phase !== 'ready' && phase !== 'standby') { prepareStt(); return }
    let audio
    try { audio = readFileSync(path) } catch (error) { log(`stt read: ${error?.message || error}`); return }
    if (!audio || audio.length < 2000) return
    let spec
    try {
      spec = found.stt.resolve({ audio: new Uint8Array(audio) })
    } catch (error) {
      log(`stt resolve: ${error?.message || error}`)
      return
    }
    const controller = new AbortController()
    const timer = setTimeout(() => controller.abort(), 45000)
    try {
      const result = await found.stt.transcribe(spec, controller.signal)
      const text = String(result?.text || '').replace(/\s+/g, ' ').trim()
      if (text.length >= 2 && !music.heard.includes(text)) {
        music.heard.push(text)
        if (music.heard.length > 6) music.heard.shift()
        log(`heard: ${text}`)
      }
    } catch (error) {
      log(`stt transcribe: ${error?.message || error}`)
    } finally {
      clearTimeout(timer)
    }
  }


  // ------------------------------------------------------------ 生命周期

  startHelper()
  setTimeout(() => { try { prepareStt() } catch { /* 忽略 */ } }, 6000)

  staleTimer = setInterval(() => {
    if (!ready) return
    if (Date.now() - music.updatedAt > 8000) {
      log('helper state stale, asking again')
      writeState(true)
    }
  }, 4000)

  recordTimer = setInterval(() => {
    if (!config.lyrics || disposing || !ready || !music.playing) return
    const found = sttProvider()
    if (!found) return
    const phase = found.provider.preparation?.phase
    if (phase !== 'ready' && phase !== 'standby') { prepareStt(); return }
    if (Date.now() - music.lastRecordAt < config.sttEveryMinutes * 60000) return
    music.lastRecordAt = Date.now()
    const path = join(CACHE_DIR, 'stt.wav')
    if (sendCommand(`record ${config.sttClipSeconds} ${path}`)) log(`stt record ${config.sttClipSeconds}s`)
  }, 15000)

  ctx.effect(() => () => {
    disposing = true
    clearInterval(staleTimer)
    clearInterval(commentTimer)
    clearInterval(recordTimer)
    if (restartTimer) clearTimeout(restartTimer)
    if (child) {
      try { child.stdin.write('quit\n') } catch { /* 忽略 */ }
      const target = child
      child = null
      setTimeout(() => {
        try { target.kill() } catch { /* 忽略 */ }
      }, 1200)
    }
    try { rmSync(PID_PATH, { force: true }) } catch { /* 忽略 */ }
    log('disposed')
  }, 'listen-together:lifecycle')

  log(`activated showWindow=${config.showWindow} comment=${config.comment} app=${config.app || 'auto'} every=${config.commentMinMinutes}-${config.commentMaxMinutes}min`)
  void startedAt
}

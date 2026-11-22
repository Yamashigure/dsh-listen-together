# 一起听歌 · 实现说明

## 它是什么

一个给 DSH 桌面端用的听歌小窗：原生辅助程序负责采回环音频和画窗，Host 插件负责把状态和工具交给会话，客户端插件在面板上放一枚开关按钮。

## 工作方式

1. **采集**：WASAPI 回环，共享模式，默认输出设备。每 100ms 算一次响度（RMS、峰值）和九个频段（63 Hz 到 12 kHz 的 Goertzel），每 500ms 把状态以 JSON 行写到 stdout。
2. **来源应用**：枚举音频会话，按进程名（`QueryFullProcessImageName`）认应用；优先浏览器、优先正在出声的，安静下来后保持上一个。
3. **曲目**：先看音频会话的显示名，没有就看该应用的窗口标题；去掉浏览器后缀和 `_哔哩哔哩_bilibili` 这类尾巴。播放中约每 1.2 秒核对一次窗口标题，新标题连续两次一致才替换；没有媒体标记的标题不会顶掉已有曲名。
4. **悬浮窗**：WinForms 自绘，置顶、圆角、可拖。播放/暂停向来源窗口发 `WM_APPCOMMAND`（失败再退回系统媒体键）；音量通过 `ISimpleAudioVolume` 改该应用所有会话的音量。
5. **会话**：Host 注册 `listen_now`、`listen_feel`、`listen_control` 三个工具，并维护一小段音乐状态。只有用户在会话里问起时，助手才会调用 `listen_feel`；插件不会主动往会话里递消息。

## 接口

```
listen_now({ detail?: 'brief' | 'full' }) -> string
listen_feel() -> string
listen_control({ action, volume? }) -> string
```

`listen_control` 的 action：`pause` / `resume` / `toggle` / `volume` / `mute` / `unmute` / `show` / `hide` / `status`。

## 实现取舍

- **不做单窗口回环**：Windows 10 2004 有进程级 loopback，但要通过 `ActivateAudioInterfaceAsync` 走一套很长的参数结构；第一版用系统混音，稳定性优先。界面里会标出来源应用，混进去的声音也会一起被分析。
- **标题只到窗口这一层**：系统的「正在播放」元数据在 SMTC 里，读它需要 Windows SDK 的 WinRT 投影；这一版从窗口标题取曲目。为了不被浏览时切到的页面干扰，标题只在带媒体标记时接受，并且需要连续两次一致才替换。
- **不加歌词**：B 站没有稳定的歌词接口，抓网页又容易和登录态、反爬打架；先用响度和频段走势，够让一句感受有落点。
- **暂停用媒体命令**：直接操作播放器元素需要 CDP 或注入，代价大。Chromium 系对媒体键的响应足够日常使用；暂停时会短暂把来源窗口提到前面，再还回去。
- **原生窗而不是页面浮层**：用户切到别的窗口也要能看到、能点，所以只能做成独立的置顶窗口。
- **感受是手动触发的**：不保留定时 followup，不在用户没开口时往会话里塞消息；`listen_feel` 只读取当前状态，回复由助手自己组织。

## 面板上的按钮怎么连到宿主

桌面端的页面跑在 `dsh-app://app` 下，客户端里用相对路径 `fetch` 会发到自定义协议，到不了宿主的 HTTP 路由。所以按钮走宿主命令：

1. Host 注册一个全局命令 `listen-together`，子命令 `toggle/show/hide/lock/size/opacity/background-pick/background-clear`，内部复用同一个 `applyUiControl`。
2. 按钮通过 `ctx.remote.commands.execute(sessionId, '/listen-together toggle', [])` 执行命令，命令直接开/关原生窗，不经过模型，也不产生聊天消息。
3. 页面刷新前拿不到新的客户端模块；插件重载后刷新一次即可。设置面板的尺寸、透明度、穿透、背景也走同一条命令；背景图由原生 `OpenFileDialog` 选，复制到缓存目录，不再走浏览器上传。

（`/listen-together/state|control|background` 三条 HTTP 路由还留着，供普通浏览器页面调试；桌面端不用它们。）

beta 里这条桥会在会话日志留下 `command` 事件（不是聊天消息）；后续版本会换成不写会话的入口。

## 锁定与外观

- 锁定用 `WM_NCHITTEST`：整窗返回 `HTTRANSPARENT`，只有小锁那一小块返回 `HTCLIENT`。所以锁上后点击穿透，但还能点小锁解锁；`Ctrl+Alt+L` 也能切换。
- 拖动悬浮窗的四边或四角改窗口大小（最小是内容本身的设计尺寸，最大 1200×600）；内容不跟着缩放，多出来的宽度给波形和音量条，多出来的高度给波形，控制按钮留在底部。锁定后不响应拖拽。尺寸、透明度、背景、位置、锁定状态都写 `%LOCALAPPDATA%\dsh-listen-together\ui.ini`；下次启动读回来。
- 窗口是普通顶层窗口（没有 `WS_EX_TOOLWINDOW`），OBS 的“窗口采集”能选中它；任务栏按钮用 `ITaskbarList::DeleteTab` 去掉，不占任务栏。它的标题就是“一起听歌”。Alt+Tab 是否出现不重要。
- 背景图铺满窗口（cover），上面盖一层半透明深色，保证文字还看得清。面板的“调整位置”让窗口进入编辑模式：拖动画面改 `bgx/bgy`，右上角“确定”保存并退出；编辑时临时解锁，确定后恢复原来的锁定状态。偏移写进 `ui.ini`，窗口缩放时按 cover 的余量重新夹紧。
- 标题超出标题框时横向滚动，不换行也不截断：实测宽度超过可用宽度才滚，开头停 1.1 秒、按 30×scale 像素/秒走到结尾、停 1.1 秒回到开头；绘制时裁到标题框，不压右侧的锁和关闭。标题变了就重置计时。
- 波形颜色和透明度也是独立设置：`wavecolor` 存 `#rrggbb`，`waveopacity` 存 0–100；波形条用该颜色和透明度画，暂停时再乘 0.35 变淡。播放键、音量条这些强调色不跟着变，只有波形本身。

## 语音听写

- 原生端收到 `record <秒> <路径>` 后，从回环流里截一段，重采样到 16 kHz 单声道 16-bit PCM，写标准 WAV。
- Bundle 的 patch 额外插入 `speech-to-text` 和 `speech-to-text-sensevoice` 两行（只装 Host，不带麦克风界面），provider 是本地 SenseVoice；模型缓存在 `~/.dsh/speech-to-text/sensevoice`。
- Host 每分钟左右取一段，`speechToText.resolve({audio})` → `transcribe()`，文本去重后保留最近六条，进 `listen_now` 和 `listen_feel` 的状态。
- 音乐不是干净人声，识别会碎；这是参考片段，不是官方歌词。首次使用需要下载模型（约 239 MB）。

## 排查

- `traceFile` 指向一个日志文件，插件启动、编译、进程启停、曲目切换都会写一行；默认留空不写。
- 原生程序支持 `--probe`（列音频会话和标题）与 `--windows`（列可见窗口）两种诊断模式，直接跑 exe 就能看。
- 编译需要系统自带的 `csc.exe`（.NET Framework 4.x）。第一次激活会编译到 `%LOCALAPPDATA%\dsh-listen-together\`。

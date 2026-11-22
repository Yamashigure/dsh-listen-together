// 音频采集、会话枚举、来源应用与曲目识别。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ListenTogether
{
    class State
    {
        public readonly object Gate = new object();
        public float Rms;
        public float Peak;
        public float[] Bands = new float[9];
        public float[] Wave = new float[72];
        public int WaveHead;
        public bool Playing;
        public string App = "";
        public string Title = "";
        public int Volume = 100;
        public bool Muted;
        public uint Pid;
        public uint ExcludePid;
        public string PinApp = "";
        public bool CaptureOk;
        public string Error = "";
        public long LastSoundMs;
        public bool TitleProbe;
        public volatile string RecordPath = "";
        public volatile int RecordSeconds;
        public volatile bool RecordRequested;

        static readonly Stopwatch Clock = Stopwatch.StartNew();

        public static long Now()
        {
            return Clock.ElapsedMilliseconds;
        }
    }

    // ------------------------------------------------------------ 回环采集
    class AudioEngine
    {
        readonly State state;
        Thread thread;
        volatile bool stopped;
        IMMDevice device;
        IAudioClient client;
        IAudioCaptureClient capture;
        int sampleRate = 48000;
        int channels = 2;
        bool isFloat = true;
        float[] ring = new float[16384];
        int ringPos;
        int ringCount;
        long lastAnalyzeMs;
        readonly List<float> recordBuffer = new List<float>();
        bool recording;
        int recordTarget;
        string recordPath = "";

        public AudioEngine(State state)
        {
            this.state = state;
        }

        public void Start()
        {
            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Name = "audio-loopback";
            thread.Start();
        }

        public void Stop()
        {
            stopped = true;
            Close();
        }

        void Loop()
        {
            try { Native.CoInitializeEx(IntPtr.Zero, 0); } catch { }
            while (!stopped)
            {
                try
                {
                    if (client == null) Init();
                    Pump();
                }
                catch (Exception ex)
                {
                    lock (state.Gate)
                    {
                        state.CaptureOk = false;
                        state.Error = "capture: " + ex.Message;
                    }
                    Close();
                    Thread.Sleep(2000);
                }
            }
        }

        void Init()
        {
            var factory = new MMDeviceEnumeratorComObject();
            var enumerator = (IMMDeviceEnumerator)factory;
            IMMDevice dev;
            int hr = enumerator.GetDefaultAudioEndpoint(0, 0, out dev);
            if (hr != 0 || dev == null) throw new Exception("no-render-endpoint " + hr);
            device = dev;

            object obj;
            Guid iid = typeof(IAudioClient).GUID;
            hr = dev.Activate(ref iid, 23, IntPtr.Zero, out obj);
            if (hr != 0 || obj == null) throw new Exception("activate " + hr);
            var ac = (IAudioClient)obj;

            IntPtr fmt;
            hr = ac.GetMixFormat(out fmt);
            if (hr != 0 || fmt == IntPtr.Zero) throw new Exception("mix-format " + hr);
            WAVEFORMATEX wf = (WAVEFORMATEX)Marshal.PtrToStructure(fmt, typeof(WAVEFORMATEX));
            sampleRate = (int)wf.nSamplesPerSec;
            channels = wf.nChannels;
            ushort tag = wf.wFormatTag;
            if (tag == 0xFFFE && wf.cbSize >= 22)
                tag = (ushort)Marshal.ReadInt32(new IntPtr(fmt.ToInt64() + 24));
            isFloat = tag == 3 || (tag == 1 && wf.wBitsPerSample == 32);
            if (wf.wBitsPerSample == 16) isFloat = false;

            // 共享模式 + 回环，100ms 缓冲。
            hr = ac.Initialize(0, 0x00020000, 1000000, 0, fmt, IntPtr.Zero);
            Marshal.FreeCoTaskMem(fmt);
            if (hr != 0) throw new Exception("init " + hr);

            object capObj;
            Guid capIid = typeof(IAudioCaptureClient).GUID;
            hr = ac.GetService(ref capIid, out capObj);
            if (hr != 0 || capObj == null) throw new Exception("capture-service " + hr);
            capture = (IAudioCaptureClient)capObj;
            client = ac;

            hr = ac.Start();
            if (hr != 0) throw new Exception("start " + hr);

            lock (state.Gate)
            {
                state.CaptureOk = true;
                state.Error = "";
            }
        }

        void Pump()
        {
            uint packet;
            capture.GetNextPacketSize(out packet);
            while (packet > 0)
            {
                IntPtr data;
                uint frames;
                int flags;
                long devPos, qpcPos;
                int hr = capture.GetBuffer(out data, out frames, out flags, out devPos, out qpcPos);
                if (hr != 0) throw new Exception("get-buffer " + hr);
                if (frames > 0)
                {
                    bool silent = (flags & 0x2) != 0;
                    if (!silent) Append(data, (int)frames);
                    else PushSilence((int)frames);
                }
                capture.ReleaseBuffer(frames);
                capture.GetNextPacketSize(out packet);
            }

            long now = State.Now();
            if (now - lastAnalyzeMs >= 100)
            {
                lastAnalyzeMs = now;
                Analyze();
            }
            Thread.Sleep(15);
        }

        void Append(IntPtr data, int frames)
        {
            int total = frames * channels;
            if (!isFloat)
            {
                short[] tmp = new short[total];
                Marshal.Copy(data, tmp, 0, total);
                for (int i = 0; i < frames; i++)
                {
                    float sum = 0;
                    for (int c = 0; c < channels; c++) sum += tmp[i * channels + c] / 32768f;
                    float mono = sum / channels;
                    Push(mono);
                    CaptureRecord(mono);
                }
            }
            else
            {
                float[] tmp = new float[total];
                Marshal.Copy(data, tmp, 0, total);
                for (int i = 0; i < frames; i++)
                {
                    float sum = 0;
                    for (int c = 0; c < channels; c++) sum += tmp[i * channels + c];
                    float mono = sum / channels;
                    Push(mono);
                    CaptureRecord(mono);
                }
            }
        }

        void CaptureRecord(float value)
        {
            if (!recording && state.RecordRequested)
            {
                lock (state.Gate)
                {
                    recording = true;
                    recordTarget = Math.Max(8000, state.RecordSeconds * Math.Max(8000, sampleRate));
                    recordPath = state.RecordPath ?? "";
                    state.RecordRequested = false;
                }
                recordBuffer.Clear();
            }
            if (!recording) return;
            recordBuffer.Add(value);
            if (recordBuffer.Count >= recordTarget)
            {
                recording = false;
                string path = recordPath;
                float[] data = recordBuffer.ToArray();
                recordBuffer.Clear();
                try { WriteWave(path, data, sampleRate); }
                catch (Exception ex) { Program.Emit("{\"type\":\"error\",\"message\":" + J.S("wav: " + ex.Message) + "}"); }
            }
        }

        static void WriteWave(string path, float[] data, int rate)
        {
            if (string.IsNullOrEmpty(path) || data.Length == 0) return;
            const int targetRate = 16000;
            double ratio = rate / (double)targetRate;
            if (ratio < 1) ratio = 1;
            int outCount = (int)(data.Length / ratio);
            if (outCount < 1) outCount = 1;
            byte[] pcm = new byte[outCount * 2];
            for (int i = 0; i < outCount; i++)
            {
                int start = (int)(i * ratio);
                int end = Math.Min(data.Length, (int)((i + 1) * ratio));
                if (end <= start) end = Math.Min(data.Length, start + 1);
                float sum = 0;
                for (int j = start; j < end; j++) sum += data[j];
                float sample = sum / Math.Max(1, end - start);
                if (sample > 1f) sample = 1f;
                if (sample < -1f) sample = -1f;
                short value = (short)(sample * 32767f);
                pcm[i * 2] = (byte)(value & 0xff);
                pcm[i * 2 + 1] = (byte)((value >> 8) & 0xff);
            }
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(new char[] { 'R', 'I', 'F', 'F' });
                writer.Write(36 + pcm.Length);
                writer.Write(new char[] { 'W', 'A', 'V', 'E' });
                writer.Write(new char[] { 'f', 'm', 't', ' ' });
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)1);
                writer.Write(targetRate);
                writer.Write(targetRate * 2);
                writer.Write((short)2);
                writer.Write((short)16);
                writer.Write(new char[] { 'd', 'a', 't', 'a' });
                writer.Write(pcm.Length);
                writer.Write(pcm);
            }
            Program.Emit("{\"type\":\"wav\",\"path\":" + J.S(path) + ",\"seconds\":" + J.N(outCount / (double)targetRate, 1) + "}");
        }

        void PushSilence(int frames)
        {
            for (int i = 0; i < frames; i++) Push(0f);
        }

        void Push(float value)
        {
            ring[ringPos] = value;
            ringPos = (ringPos + 1) % ring.Length;
            if (ringCount < ring.Length) ringCount++;
        }

        void Analyze()
        {
            int n = Math.Min(ringCount, 4096);
            if (n < 256) return;
            float[] mono = new float[n];
            int start = (ringPos - n + ring.Length) % ring.Length;
            for (int i = 0; i < n; i++) mono[i] = ring[(start + i) % ring.Length];

            double sum = 0;
            float peak = 0;
            for (int i = 0; i < n; i++)
            {
                float v = mono[i];
                sum += v * v;
                float a = Math.Abs(v);
                if (a > peak) peak = a;
            }
            float rms = (float)Math.Sqrt(sum / n);

            int fftN = Math.Min(n, 2048);
            double[] centers = { 63, 125, 250, 500, 1000, 2000, 4000, 8000, 12000 };
            float[] bands = new float[centers.Length];
            for (int b = 0; b < centers.Length; b++)
            {
                double mag = Goertzel(mono, fftN, sampleRate, centers[b]);
                double db = 20 * Math.Log10(mag + 1e-7);
                double norm = (db + 62) / 62;
                if (norm < 0) norm = 0;
                if (norm > 1) norm = 1;
                bands[b] = (float)norm;
            }

            lock (state.Gate)
            {
                state.Rms = rms;
                state.Peak = peak;
                state.Bands = bands;
                state.Wave[state.WaveHead] = rms;
                state.WaveHead = (state.WaveHead + 1) % state.Wave.Length;
                long now = State.Now();
                if (rms > 0.004f)
                {
                    // 安静了一阵子之后第一次出声：这时候当前页面多半就是点播放的那一页，
                    // 在这个瞬间取一次标题，播放期间不再跟着标签页乱跳。
                    if (state.LastSoundMs == 0 || now - state.LastSoundMs > 2600) state.TitleProbe = true;
                    state.LastSoundMs = now;
                    state.Playing = true;
                }
                else if (now - state.LastSoundMs > 1800)
                {
                    state.Playing = false;
                }
            }
        }

        static double Goertzel(float[] x, int count, int rate, double freq)
        {
            double w = 2 * Math.PI * freq / rate;
            double coeff = 2 * Math.Cos(w);
            double s1 = 0, s2 = 0;
            int start = x.Length - count;
            for (int i = 0; i < count; i++)
            {
                double s0 = x[start + i] + coeff * s1 - s2;
                s2 = s1;
                s1 = s0;
            }
            double p = s1 * s1 + s2 * s2 - coeff * s1 * s2;
            return Math.Sqrt(Math.Max(0, p)) / count;
        }

        void Close()
        {
            try { if (client != null) client.Stop(); } catch { }
            try { if (capture != null) Marshal.ReleaseComObject(capture); } catch { }
            try { if (client != null) Marshal.ReleaseComObject(client); } catch { }
            try { if (device != null) Marshal.ReleaseComObject(device); } catch { }
            capture = null;
            client = null;
            device = null;
            lock (state.Gate) state.CaptureOk = false;
        }
    }

    // ------------------------------------------------------------ 音频会话
    class SessionInfo
    {
        public string ProcessName = "";
        public uint Pid;
        public float Volume = 1f;
        public bool Muted;
        public float Peak;
        public string DisplayName = "";
    }

    static class Sessions
    {
        static IAudioSessionManager2 OpenManager()
        {
            var factory = new MMDeviceEnumeratorComObject();
            var enumerator = (IMMDeviceEnumerator)factory;
            IMMDevice dev;
            if (enumerator.GetDefaultAudioEndpoint(0, 0, out dev) != 0 || dev == null) return null;
            object obj;
            Guid iid = typeof(IAudioSessionManager2).GUID;
            if (dev.Activate(ref iid, 23, IntPtr.Zero, out obj) != 0 || obj == null) return null;
            return (IAudioSessionManager2)obj;
        }

        public static List<SessionInfo> List()
        {
            var result = new List<SessionInfo>();
            IAudioSessionManager2 mgr = null;
            IAudioSessionEnumerator sessions = null;
            try
            {
                mgr = OpenManager();
                if (mgr == null) return result;
                if (mgr.GetSessionEnumerator(out sessions) != 0 || sessions == null) return result;
                int count;
                if (sessions.GetCount(out count) != 0) return result;
                for (int i = 0; i < count; i++)
                {
                    IAudioSessionControl ctl;
                    if (sessions.GetSession(i, out ctl) != 0 || ctl == null) continue;
                    try
                    {
                        var info = new SessionInfo();
                        var ctl2 = ctl as IAudioSessionControl2;
                        if (ctl2 != null)
                        {
                            uint pid;
                            if (ctl2.GetProcessId(out pid) == 0) info.Pid = pid;
                        }
                        if (info.Pid != 0) info.ProcessName = Native.ProcessNameOf(info.Pid);
                        try
                        {
                            string name;
                            if (ctl.GetDisplayName(out name) == 0 && !string.IsNullOrEmpty(name)) info.DisplayName = name;
                        }
                        catch { }
                        var vol = ctl as ISimpleAudioVolume;
                        if (vol != null)
                        {
                            float v;
                            bool m;
                            if (vol.GetMasterVolume(out v) == 0) info.Volume = v;
                            if (vol.GetMute(out m) == 0) info.Muted = m;
                        }
                        var meter = ctl as IAudioMeterInformation;
                        if (meter != null)
                        {
                            float p;
                            if (meter.GetPeakValue(out p) == 0) info.Peak = p;
                        }
                        result.Add(info);
                    }
                    finally
                    {
                        if (ctl != null) Marshal.ReleaseComObject(ctl);
                    }
                }
            }
            catch { }
            finally
            {
                if (sessions != null) Marshal.ReleaseComObject(sessions);
                if (mgr != null) Marshal.ReleaseComObject(mgr);
            }
            return result;
        }

        static void ForEach(string processName, Action<IAudioSessionControl> action)
        {
            IAudioSessionManager2 mgr = null;
            IAudioSessionEnumerator sessions = null;
            try
            {
                mgr = OpenManager();
                if (mgr == null) return;
                if (mgr.GetSessionEnumerator(out sessions) != 0 || sessions == null) return;
                int count;
                if (sessions.GetCount(out count) != 0) return;
                for (int i = 0; i < count; i++)
                {
                    IAudioSessionControl ctl;
                    if (sessions.GetSession(i, out ctl) != 0 || ctl == null) continue;
                    try
                    {
                        var ctl2 = ctl as IAudioSessionControl2;
                        if (ctl2 == null) continue;
                        uint pid;
                        if (ctl2.GetProcessId(out pid) != 0 || pid == 0) continue;
                        if (!string.Equals(Native.ProcessNameOf(pid), processName, StringComparison.OrdinalIgnoreCase)) continue;
                        action(ctl);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(ctl);
                    }
                }
            }
            catch { }
            finally
            {
                if (sessions != null) Marshal.ReleaseComObject(sessions);
                if (mgr != null) Marshal.ReleaseComObject(mgr);
            }
        }

        public static void SetVolume(string processName, int percent)
        {
            float level = Math.Max(0, Math.Min(100, percent)) / 100f;
            ForEach(processName, delegate (IAudioSessionControl ctl)
            {
                var vol = ctl as ISimpleAudioVolume;
                if (vol != null) vol.SetMasterVolume(level, IntPtr.Zero);
            });
        }

        public static void SetMute(string processName, bool mute)
        {
            ForEach(processName, delegate (IAudioSessionControl ctl)
            {
                var vol = ctl as ISimpleAudioVolume;
                if (vol != null) vol.SetMute(mute, IntPtr.Zero);
            });
        }
    }

    // ------------------------------------------------------------ 窗口标题
    static class WindowTitles
    {
        static bool IsGeneric(string title)
        {
            string t = title.Trim();
            return t == "Microsoft Edge" || t == "Google Chrome" || t == "新标签页" || t == "New Tab"
                || t == "Program Manager" || t == "设置" || t.Length < 2;
        }

        public static string BestForApp(string processName)
        {
            IntPtr bestWindow = IntPtr.Zero;
            string best = BestWindowForApp(processName, out bestWindow);
            if (bestWindow != IntPtr.Zero) lastWindow = bestWindow;
            return best;
        }

        static IntPtr lastWindow = IntPtr.Zero;

        /// <summary>最近一次命中的来源窗口，用来直接投递媒体命令。</summary>
        public static IntPtr LastWindow { get { return lastWindow; } }

        public static string BestWindowForApp(string processName, out IntPtr window)
        {
            window = IntPtr.Zero;
            if (string.IsNullOrEmpty(processName)) return "";
            string baseName = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? processName.Substring(0, processName.Length - 4) : processName;
            var pids = new HashSet<uint>();
            try
            {
                foreach (var process in Process.GetProcessesByName(baseName))
                {
                    try { pids.Add((uint)process.Id); } catch { }
                    try { process.Dispose(); } catch { }
                }
            }
            catch { }
            if (pids.Count == 0) return "";

            string best = "";
            int bestScore = -1;
            IntPtr found = IntPtr.Zero;
            Native.EnumWindows(delegate (IntPtr hWnd, IntPtr lParam)
            {
                if (!Native.IsWindowVisible(hWnd)) return true;
                uint pid;
                Native.GetWindowThreadProcessId(hWnd, out pid);
                if (pid == 0 || !pids.Contains(pid)) return true;
                int len = Native.GetWindowTextLengthW(hWnd);
                if (len <= 0) return true;
                var sb = new StringBuilder(len + 2);
                Native.GetWindowTextW(hWnd, sb, sb.Capacity);
                string title = sb.ToString().Trim();
                if (IsGeneric(title)) return true;
                int score = title.Length;
                if (title.IndexOf("哔哩哔哩", StringComparison.OrdinalIgnoreCase) >= 0) score += 200;
                if (title.IndexOf("YouTube", StringComparison.OrdinalIgnoreCase) >= 0) score += 120;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = title;
                    found = hWnd;
                }
                return true;
            }, IntPtr.Zero);
            window = found;
            return best;
        }
    }

    // ------------------------------------------------------ 来源应用跟踪
    static class AppStateLoop
    {
        static readonly string[] BrowserNames = { "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe", "vivaldi.exe", "qqbrowser.exe", "360se.exe", "360chrome.exe" };
        static string self;

        public static void Start(State state)
        {
            self = Process.GetCurrentProcess().ProcessName.ToLowerInvariant() + ".exe";
            Thread t = new Thread(delegate () { Run(state); });
            t.IsBackground = true;
            t.Name = "app-state";
            t.Start();
        }

        static bool IsBrowser(string name)
        {
            foreach (string b in BrowserNames) if (string.Equals(b, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>窗口标题像不像一页媒体内容；用来挡住浏览时别的页面把曲名顶掉。</summary>
        static bool LooksLikeMedia(string title)
        {
            string t = title.ToLowerInvariant();
            return t.Contains("哔哩哔哩") || t.Contains("bilibili") || t.Contains("youtube")
                || t.Contains("网易云") || t.Contains("qq音乐") || t.Contains("酷狗") || t.Contains("酷我")
                || t.Contains("spotify") || t.Contains("soundcloud") || t.Contains("music") || t.Contains("音乐")
                || t.Contains("《") || t.Contains("【");
        }

        static void Run(State state)
        {
            string candidateTitle = "";
            int candidateCount = 0;
            long lastTitlePoll = 0;
            while (true)
            {
                try
                {
                    List<SessionInfo> all = Sessions.List();
                    string pin;
                    uint excludePid;
                    lock (state.Gate)
                    {
                        pin = state.PinApp;
                        excludePid = state.ExcludePid;
                    }
                    all.RemoveAll(delegate (SessionInfo s) { return s.Pid != 0 && s.Pid == excludePid; });

                    SessionInfo chosen = null;
                    string current;
                    lock (state.Gate) current = state.App;
                    SessionInfo currentSession = null;
                    foreach (var s in all)
                        if (!string.IsNullOrEmpty(current) && string.Equals(s.ProcessName, current, StringComparison.OrdinalIgnoreCase))
                        {
                            currentSession = s;
                            break;
                        }

                    // 1) 指定的来源应用
                    if (!string.IsNullOrEmpty(pin))
                    {
                        float bestPeak = -1;
                        foreach (var s in all)
                            if (string.Equals(s.ProcessName, pin, StringComparison.OrdinalIgnoreCase) && s.Peak >= bestPeak)
                            {
                                bestPeak = s.Peak;
                                chosen = s;
                            }
                    }

                    // 2) 正在出声的浏览器
                    if (chosen == null)
                    {
                        float bestPeak = 0.01f;
                        foreach (var s in all)
                        {
                            if (string.IsNullOrEmpty(s.ProcessName)) continue;
                            if (string.Equals(s.ProcessName, self, StringComparison.OrdinalIgnoreCase)) continue;
                            if (s.Peak > bestPeak && IsBrowser(s.ProcessName))
                            {
                                bestPeak = s.Peak;
                                chosen = s;
                            }
                        }
                    }

                    // 3) 浏览器停下来了，也还是它；别被游戏里很小的底噪抢走
                    if (chosen == null && currentSession != null && IsBrowser(current)) chosen = currentSession;

                    // 4) 别的应用真的在出声（门槛调高，避免底噪）
                    if (chosen == null)
                    {
                        float bestPeak = 0.03f;
                        foreach (var s in all)
                        {
                            if (string.IsNullOrEmpty(s.ProcessName)) continue;
                            if (string.Equals(s.ProcessName, self, StringComparison.OrdinalIgnoreCase)) continue;
                            if (s.Peak > bestPeak)
                            {
                                bestPeak = s.Peak;
                                chosen = s;
                            }
                        }
                    }

                    // 5) 只有浏览器或确实还在出声的应用才保持；安静下来的普通应用让位
                    if (chosen == null && currentSession != null && IsBrowser(current)) chosen = currentSession;
                    if (chosen == null && currentSession != null && currentSession.Peak > 0.01f) chosen = currentSession;
                    if (chosen == null && currentSession != null && !IsBrowser(current) && currentSession.Peak <= 0.01f)
                    {
                        lock (state.Gate)
                        {
                            state.App = "";
                            state.Pid = 0;
                            state.Title = "";
                            currentSession = null;
                        }
                    }

                    if (chosen != null)
                    {
                        bool probe;
                        lock (state.Gate)
                        {
                            probe = state.TitleProbe;
                            state.TitleProbe = false;
                        }
                        bool appChanged = !string.Equals(chosen.ProcessName, current, StringComparison.OrdinalIgnoreCase);
                        bool browser = IsBrowser(chosen.ProcessName);
                        string existing;
                        bool playingNow;
                        lock (state.Gate)
                        {
                            existing = state.Title;
                            playingNow = state.Playing;
                        }
                        long nowMs = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
                        bool pollDue = browser && playingNow && nowMs - lastTitlePoll >= 1200;
                        bool mustRefresh = probe || appChanged || string.IsNullOrEmpty(existing);

                        if (mustRefresh || pollDue)
                        {
                            if (pollDue) lastTitlePoll = nowMs;
                            string title = "";
                            if (!browser && !string.IsNullOrEmpty(chosen.DisplayName)
                                && chosen.DisplayName.IndexOf(".exe", StringComparison.OrdinalIgnoreCase) < 0
                                && chosen.DisplayName.IndexOf("\\", StringComparison.Ordinal) < 0)
                                title = chosen.DisplayName;
                            if (title == "" || browser) title = WindowTitles.BestForApp(chosen.ProcessName);
                            bool acceptable = title != "" && (!browser || LooksLikeMedia(title));
                            if (acceptable && title != existing)
                            {
                                if (mustRefresh)
                                {
                                    lock (state.Gate) state.Title = title;
                                    candidateTitle = "";
                                    candidateCount = 0;
                                }
                                else
                                {
                                    if (string.Equals(title, candidateTitle, StringComparison.Ordinal)) candidateCount++;
                                    else
                                    {
                                        candidateTitle = title;
                                        candidateCount = 1;
                                    }
                                    if (candidateCount >= 2)
                                    {
                                        lock (state.Gate) state.Title = title;
                                        candidateTitle = "";
                                        candidateCount = 0;
                                    }
                                }
                            }
                            else if (acceptable && title == existing)
                            {
                                candidateTitle = "";
                                candidateCount = 0;
                            }
                        }
                        lock (state.Gate)
                        {
                            state.App = chosen.ProcessName;
                            state.Pid = chosen.Pid;
                            state.Volume = (int)Math.Round(chosen.Volume * 100);
                            state.Muted = chosen.Muted;
                        }
                    }
                    else
                    {
                        lock (state.Gate)
                        {
                            state.Pid = 0;
                            state.App = "";
                            state.Title = "";
                        }
                    }
                }
                catch { }
                Thread.Sleep(900);
            }
        }
    }
}

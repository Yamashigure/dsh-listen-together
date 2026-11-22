// 入口：命令行模式、标准输入输出协议、状态广播。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ListenTogether
{
    static class Program
    {
        public static readonly State Shared = new State();
        static AudioEngine audio;
        static FloatingForm form;
        static volatile bool quitting;
        static long lastEmitMs;
        static bool lastPlaying;
        static string lastTitle = "";
        static string lastApp = "";
        static int lastVolume = -1;
        static bool lastMuted;
        static readonly object OutGate = new object();

        [STAThread]
        static void Main(string[] args)
        {
            try { Native.SetProcessDPIAware(); } catch { }
            try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }
            try
            {
                var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
                stdout.AutoFlush = true;
                Console.SetOut(stdout);
            }
            catch { }
            try { Native.SetProcessDPIAware(); } catch { }
            try { Native.CoInitializeEx(IntPtr.Zero, 0); } catch { }
            Application.ThreadException += delegate (object sender, System.Threading.ThreadExceptionEventArgs e)
            {
                Emit("{\"type\":\"error\",\"message\":" + J.S("ui: " + e.Exception.Message) + "}");
            };
            AppDomain.CurrentDomain.UnhandledException += delegate (object sender, UnhandledExceptionEventArgs e)
            {
                Emit("{\"type\":\"error\",\"message\":" + J.S("fatal: " + e.ExceptionObject) + "}");
            };

            string mode = args.Length > 0 ? args[0] : "";
            if (mode == "--probe") { RunProbe(); return; }
            if (mode == "--windows") { RunWindows(); return; }

            bool headless = false;
            bool hidden = false;
            string pin = "";
            uint excludePid = 0;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--headless") headless = true;
                if (args[i] == "--hidden") hidden = true;
                if (args[i] == "--pin" && i + 1 < args.Length) pin = args[i + 1];
                if (args[i] == "--exclude-pid" && i + 1 < args.Length)
                {
                    uint pid;
                    if (uint.TryParse(args[i + 1], out pid)) excludePid = pid;
                }
            }
            lock (Shared.Gate)
            {
                Shared.PinApp = pin;
                Shared.ExcludePid = excludePid;
            }

            Emit("{\"type\":\"ready\",\"pid\":" + Process.GetCurrentProcess().Id
                + ",\"headless\":" + (headless ? "true" : "false") + "}");

            StartStdin();
            audio = new AudioEngine(Shared);
            audio.Start();
            AppStateLoop.Start(Shared);
            StartEmitter();

            if (headless)
            {
                while (!quitting) Thread.Sleep(200);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            form = new FloatingForm(Shared, Emit, hidden);
            form.EmitSettings();
            Application.Run(form);
            quitting = true;
            audio.Stop();
        }

        // ------------------------------------------------------------ 诊断模式
        static void RunWindows()
        {
            int count = 0;
            Native.EnumWindows(delegate (IntPtr hWnd, IntPtr lParam)
            {
                if (!Native.IsWindowVisible(hWnd)) return true;
                int len = Native.GetWindowTextLengthW(hWnd);
                if (len <= 0) return true;
                var sb = new StringBuilder(len + 2);
                Native.GetWindowTextW(hWnd, sb, sb.Capacity);
                uint pid;
                Native.GetWindowThreadProcessId(hWnd, out pid);
                Console.WriteLine("{\"app\":" + J.S(Native.ProcessNameOf(pid)) + ",\"pid\":" + pid
                    + ",\"title\":" + J.S(sb.ToString()) + "}");
                count++;
                return true;
            }, IntPtr.Zero);
            Console.WriteLine("{\"type\":\"windows\",\"count\":" + count + "}");
        }

        static void RunProbe()
        {
            List<SessionInfo> all = Sessions.List();
            Console.WriteLine("{\"type\":\"probe\",\"sessions\":[");
            for (int i = 0; i < all.Count; i++)
            {
                SessionInfo s = all[i];
                if (i > 0) Console.Write(",");
                Console.Write("{\"app\":" + J.S(s.ProcessName) + ",\"pid\":" + s.Pid
                    + ",\"volume\":" + Math.Round(s.Volume * 100) + ",\"muted\":" + (s.Muted ? "true" : "false")
                    + ",\"peak\":" + J.N(s.Peak, 4) + ",\"name\":" + J.S(s.DisplayName) + "}");
            }
            Console.WriteLine("],\"titles\":[");
            List<string> apps = new List<string>();
            foreach (SessionInfo s in all)
                if (!string.IsNullOrEmpty(s.ProcessName) && !apps.Contains(s.ProcessName)) apps.Add(s.ProcessName);
            for (int i = 0; i < apps.Count; i++)
            {
                if (i > 0) Console.Write(",");
                Console.Write("{\"app\":" + J.S(apps[i]) + ",\"title\":" + J.S(WindowTitles.BestForApp(apps[i])) + "}");
            }
            Console.WriteLine("]}");
        }

        // ------------------------------------------------------------ 状态广播
        static void StartEmitter()
        {
            Thread t = new Thread(delegate ()
            {
                while (!quitting)
                {
                    Thread.Sleep(500);
                    EmitState(false);
                }
            });
            t.IsBackground = true;
            t.Name = "state-emitter";
            t.Start();
        }

        public static void EmitState(bool force)
        {
            string title, app, error;
            int volume;
            bool muted, playing, captureOk;
            float rms, peak;
            float[] bands;
            lock (Shared.Gate)
            {
                title = Shared.Title;
                app = Shared.App;
                volume = Shared.Volume;
                muted = Shared.Muted;
                playing = Shared.Playing;
                captureOk = Shared.CaptureOk;
                error = Shared.Error;
                rms = Shared.Rms;
                peak = Shared.Peak;
                bands = (float[])Shared.Bands.Clone();
            }
            long now = State.Now();
            bool changed = force || playing != lastPlaying || title != lastTitle || app != lastApp
                || volume != lastVolume || muted != lastMuted;
            if (!changed && now - lastEmitMs < 1500) return;
            lastPlaying = playing;
            lastTitle = title;
            lastApp = app;
            lastVolume = volume;
            lastMuted = muted;
            lastEmitMs = now;

            var sb = new StringBuilder(256);
            sb.Append("{\"type\":\"state\",\"playing\":").Append(playing ? "true" : "false");
            sb.Append(",\"app\":").Append(J.S(app));
            sb.Append(",\"title\":").Append(J.S(title));
            sb.Append(",\"volume\":").Append(volume);
            sb.Append(",\"muted\":").Append(muted ? "true" : "false");
            sb.Append(",\"rms\":").Append(J.N(rms, 4));
            sb.Append(",\"peak\":").Append(J.N(peak, 4));
            sb.Append(",\"capture\":").Append(captureOk ? "true" : "false");
            if (!string.IsNullOrEmpty(error)) sb.Append(",\"error\":").Append(J.S(error));
            sb.Append(",\"bands\":[");
            for (int i = 0; i < bands.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(J.N(bands[i], 3));
            }
            sb.Append("]}");
            Emit(sb.ToString());
        }

        public static void Emit(string line)
        {
            lock (OutGate)
            {
                try
                {
                    Console.Out.WriteLine(line);
                    Console.Out.Flush();
                }
                catch { }
            }
        }

        // ------------------------------------------------------------ 控制
        static string TargetApp()
        {
            lock (Shared.Gate)
                return !string.IsNullOrEmpty(Shared.PinApp) ? Shared.PinApp : Shared.App;
        }

        public static void TogglePlayback()
        {
            string app = TargetApp();
            IntPtr window = WindowTitles.LastWindow;
            if (window != IntPtr.Zero)
            {
                // 先声明一下：把来源窗口临时提到前面，按键才会送到它那里。
                // 这一下很快，画面上顶多闪一下，不会把用户的窗口挪走。
                IntPtr previous = Native.GetForegroundWindow();
                if (Native.IsIconic(window)) Native.ShowWindow(window, Native.SW_RESTORE);
                Native.SetForegroundWindow(window);
                Thread.Sleep(90);
                Native.MediaKey(Native.VK_MEDIA_PLAY_PAUSE);
                Thread.Sleep(90);
                if (previous != IntPtr.Zero && previous != window) Native.SetForegroundWindow(previous);
            }
            else
            {
                Native.MediaKey(Native.VK_MEDIA_PLAY_PAUSE);
            }
            lock (Shared.Gate) Shared.Playing = !Shared.Playing;
            Emit("{\"type\":\"action\",\"action\":\"toggle\"}");
            EmitState(true);
        }

        public static void SetVolumePercent(int percent)
        {
            percent = Math.Max(0, Math.Min(100, percent));
            string app = TargetApp();
            if (string.IsNullOrEmpty(app)) return;
            Sessions.SetVolume(app, percent);
            lock (Shared.Gate)
            {
                Shared.Volume = percent;
                Shared.Muted = percent == 0;
            }
            Emit("{\"type\":\"action\",\"action\":\"volume\",\"app\":" + J.S(app) + ",\"volume\":" + percent + "}");
            EmitState(true);
        }

        public static void NudgeVolume(int delta)
        {
            int volume;
            lock (Shared.Gate) volume = Shared.Volume;
            SetVolumePercent(volume + delta);
        }

        public static void ToggleMute()
        {
            string app = TargetApp();
            if (string.IsNullOrEmpty(app)) return;
            bool muted;
            lock (Shared.Gate) muted = Shared.Muted;
            Sessions.SetMute(app, !muted);
            lock (Shared.Gate) Shared.Muted = !muted;
            Emit("{\"type\":\"action\",\"action\":\"mute\",\"app\":" + J.S(app) + ",\"muted\":" + (!muted ? "true" : "false") + "}");
            EmitState(true);
        }

        static void StartStdin()
        {
            Thread t = new Thread(delegate ()
            {
                try { Native.CoInitializeEx(IntPtr.Zero, 0); } catch { }
                string line;
                while ((line = Console.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.Length == 0) continue;
                    int space = line.IndexOf(' ');
                    string cmd = (space < 0 ? line : line.Substring(0, space)).ToLowerInvariant();
                    string arg = space < 0 ? "" : line.Substring(space + 1).Trim();
                    try
                    {
                        switch (cmd)
                        {
                            case "quit":
                            case "exit":
                                quitting = true;
                                try { if (form != null) form.BeginInvoke(new Action(delegate () { form.Close(); })); } catch { }
                                return;
                            case "toggle":
                            case "pause":
                            case "play":
                                TogglePlayback();
                                break;
                            case "volume":
                                int v;
                                if (int.TryParse(arg, out v)) SetVolumePercent(v);
                                break;
                            case "mute":
                                ToggleMute();
                                break;
                            case "show":
                                if (form != null) form.BeginInvoke(new Action(delegate () { form.SetVisible(true); }));
                                break;
                            case "hide":
                                if (form != null) form.BeginInvoke(new Action(delegate () { form.SetVisible(false); }));
                                break;
                            case "lock":
                                if (form != null)
                                {
                                    bool lockOn = string.Equals(arg, "on", StringComparison.OrdinalIgnoreCase)
                                        || arg == "1" || string.Equals(arg, "true", StringComparison.OrdinalIgnoreCase);
                                    bool lockOff = string.Equals(arg, "off", StringComparison.OrdinalIgnoreCase)
                                        || arg == "0" || string.Equals(arg, "false", StringComparison.OrdinalIgnoreCase);
                                    if (lockOn) form.BeginInvoke(new Action(delegate () { form.SetLocked(true); }));
                                    else if (lockOff) form.BeginInvoke(new Action(delegate () { form.SetLocked(false); }));
                                    else form.BeginInvoke(new Action(delegate () { form.ToggleLock(); }));
                                }
                                break;
                            case "opacity":
                                float opacity;
                                if (float.TryParse(arg, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out opacity) && form != null)
                                    form.BeginInvoke(new Action(delegate () { form.SetOpacity(opacity); }));
                                break;
                            case "size":
                                if (form != null)
                                {
                                    string[] pair = arg.Split(new char[] { ' ', 'x', ',' }, StringSplitOptions.RemoveEmptyEntries);
                                    int w, h;
                                    if (pair.Length >= 2 && int.TryParse(pair[0], out w) && int.TryParse(pair[1], out h))
                                        form.BeginInvoke(new Action(delegate () { form.SetSize(w, h); }));
                                }
                                break;
                            case "bg":
                                if (form != null)
                                {
                                    string path = arg;
                                    if (path.Length >= 2 && path[0] == '"' && path[path.Length - 1] == '"') path = path.Substring(1, path.Length - 2);
                                    if (path == "none") path = "";
                                    form.BeginInvoke(new Action(delegate () { form.SetBackground(path); }));
                                }
                                break;
                            case "pickbg":
                                if (form != null) form.BeginInvoke(new Action(delegate () { form.PickBackground(); }));
                                break;
                            case "bgedit":
                                if (form != null) form.BeginInvoke(new Action(delegate () { form.EnterBackgroundEdit(); }));
                                break;
                            case "wave":
                                if (form != null)
                                {
                                    string[] wp = arg.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                                    int wa;
                                    if (wp.Length >= 2 && int.TryParse(wp[1], out wa))
                                        form.BeginInvoke(new Action(delegate () { form.SetWave(wp[0], wa); }));
                                }
                                break;
                            case "settings":
                                if (form != null) form.BeginInvoke(new Action(delegate () { form.EmitSettings(); }));
                                break;
                            case "record":
                                {
                                    int recordSpace = arg.IndexOf(' ');
                                    if (recordSpace > 0)
                                    {
                                        int seconds;
                                        if (int.TryParse(arg.Substring(0, recordSpace), out seconds))
                                        {
                                            string path = arg.Substring(recordSpace + 1).Trim();
                                            lock (Shared.Gate)
                                            {
                                                Shared.RecordSeconds = Math.Max(3, Math.Min(30, seconds));
                                                Shared.RecordPath = path;
                                                Shared.RecordRequested = true;
                                            }
                                            Emit("{\"type\":\"record\",\"seconds\":" + Shared.RecordSeconds + "}");
                                        }
                                    }
                                    else Emit("{\"type\":\"error\",\"message\":\"record needs seconds and path\"}");
                                }
                                break;
                            case "pin":
                                lock (Shared.Gate) Shared.PinApp = arg;
                                Emit("{\"type\":\"action\",\"action\":\"pin\",\"app\":" + J.S(arg) + "}");
                                break;
                            case "state":
                                EmitState(true);
                                break;
                            default:
                                Emit("{\"type\":\"error\",\"message\":\"unknown command\"}");
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Emit("{\"type\":\"error\",\"message\":" + J.S(ex.Message) + "}");
                    }
                }
            });
            t.IsBackground = true;
            t.Name = "stdin";
            t.Start();
        }
    }
}

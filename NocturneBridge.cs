// NOCTURNE DECK Bridge
// A small tray app that lets the NOCTURNE DECK iCUE widget:
//   - pick which player it shows and controls (Spotify, browser/YouTube, AIMP 2/3/4/5, VLC, ...)
//   - read real play state, position and length
//   - set the volume (Windows master volume, or AIMP's own)
//
// Why a separate app: iCUE widgets run in a sandbox and only get "song + artist of whatever
// Windows plays now". Anything more needs a program on the PC.
//
// What it does NOT do: no internet access, no files written except one optional autostart
// entry (HKCU ...\Run, only when you tick "Start automatically"), no admin rights.
// It listens on http://localhost:8977 only, so nothing outside this PC can reach it.
// The only connections it opens itself go to 127.0.0.1: VLC's web interface and foobar2000's
// Beefweb component, and only when you have switched those on in the player.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("NOCTURNE DECK Bridge")]
[assembly: AssemblyDescription("Media source and volume bridge for the NOCTURNE DECK iCUE widget (localhost only)")]
[assembly: AssemblyProduct("NOCTURNE DECK")]
[assembly: AssemblyCompany("GM Edge Labs")]
[assembly: AssemblyCopyright("GM Edge Labs 2026")]
[assembly: AssemblyVersion("1.4.0.0")]
[assembly: AssemblyFileVersion("1.4.0.0")]

namespace NocturneDeck
{
    static class Program
    {
        public const int Port = 8977;
        public const string Version = "1.4.0";

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var mutex = new Mutex(false, "NocturneDeckBridge_SingleInstance");
            if (!Take(mutex, 0))
            {
                // Another bridge is running. If it is this version or newer, leave it alone.
                string other = PeerVersion();
                if (other != null && Compare(other, Version) >= 0)
                {
                    MessageBox.Show("NOCTURNE DECK Bridge " + other + " is already running.\n\nYou find it in the tray next to the clock.",
                        "NOCTURNE DECK Bridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                // Older or broken bridge: close it and take over.
                CloseOthers();
                if (!Take(mutex, 5000))
                {
                    MessageBox.Show("An older NOCTURNE DECK Bridge is still running and could not be closed.\n\n" +
                        "Right-click its tray icon and choose Exit (or restart the PC), then start this one again.",
                        "NOCTURNE DECK Bridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
            try { Application.Run(new TrayApp()); }
            finally { try { mutex.ReleaseMutex(); } catch { } }
        }

        static bool Take(Mutex m, int ms)
        {
            try { return m.WaitOne(ms); }
            catch (AbandonedMutexException) { return true; } // previous owner was closed
        }

        // version of the bridge that answers on our port, or null if none / too old to say
        static string PeerVersion()
        {
            try
            {
                var rq = (HttpWebRequest)WebRequest.Create("http://localhost:" + Port + "/version");
                rq.Timeout = 1500; rq.Proxy = null;
                using (var rs = (HttpWebResponse)rq.GetResponse())
                using (var sr = new StreamReader(rs.GetResponseStream()))
                {
                    string body = sr.ReadToEnd();
                    int i = body.IndexOf("\"version\"");
                    if (i < 0) return null;
                    int a = body.IndexOf('"', body.IndexOf(':', i) + 1), b = body.IndexOf('"', a + 1);
                    return a > 0 && b > a ? body.Substring(a + 1, b - a - 1) : null;
                }
            }
            catch { return null; }
        }

        public static int Compare(string a, string b)
        {
            System.Version x, y;
            if (!System.Version.TryParse(a, out x)) return -1;
            if (!System.Version.TryParse(b, out y)) return 1;
            return x.CompareTo(y);
        }

        // Close every other copy of the bridge: same program name, same product, or whoever holds our port.
        static void CloseOthers()
        {
            var me = System.Diagnostics.Process.GetCurrentProcess();
            var portPids = PortOwners();
            foreach (var p in System.Diagnostics.Process.GetProcesses())
            {
                try
                {
                    if (p.Id == me.Id) continue;
                    bool hit = portPids.Contains(p.Id)
                        || string.Equals(p.ProcessName, me.ProcessName, StringComparison.OrdinalIgnoreCase)
                        || p.ProcessName.IndexOf("NocturneBridge", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!hit)
                    {
                        try
                        {
                            var fv = p.MainModule.FileVersionInfo;
                            hit = (fv.ProductName ?? "").StartsWith("NOCTURNE DECK", StringComparison.OrdinalIgnoreCase)
                               || (fv.FileDescription ?? "").StartsWith("NOCTURNE DECK", StringComparison.OrdinalIgnoreCase);
                        }
                        catch { }
                    }
                    if (hit) { p.Kill(); p.WaitForExit(3000); }
                }
                catch { }
            }
        }

        // PIDs that registered http://localhost:<Port>/ with Windows (works in any Windows language)
        static HashSet<int> PortOwners()
        {
            var found = new HashSet<int>();
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("netsh", "http show servicestate view=requestq")
                { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
                string text;
                using (var pr = System.Diagnostics.Process.Start(psi))
                {
                    text = pr.StandardOutput.ReadToEnd();
                    pr.WaitForExit(4000);
                }
                var pids = new List<int>();
                bool ours = false;
                string mark = ":" + Port + "/";
                foreach (var raw in (text + "\nEND").Split('\n'))
                {
                    string line = raw.TrimEnd(new[] { '\r' });
                    if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
                    {
                        if (ours) foreach (int id in pids) found.Add(id);
                        pids.Clear(); ours = false;
                        continue;
                    }
                    string t = line.Trim();
                    int n;
                    if (t.Length > 0 && t.All(char.IsDigit) && int.TryParse(t, out n)) pids.Add(n);
                    if (t.IndexOf(mark, StringComparison.OrdinalIgnoreCase) >= 0) ours = true;
                }
            }
            catch { }
            found.Remove(0); found.Remove(4);
            return found;
        }
    }

    // ------------------------------------------------------------------ tray
    class TrayApp : ApplicationContext
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "NOCTURNE DECK Bridge";
        readonly NotifyIcon tray;
        readonly ToolStripMenuItem statusItem, autoItem;
        readonly Server server;

        public TrayApp()
        {

            statusItem = new ToolStripMenuItem("Starting...") { Enabled = false };
            autoItem = new ToolStripMenuItem("Start automatically", null, (s, e) => ToggleAutostart());
            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("NOCTURNE DECK Bridge") { Enabled = false, Font = new Font(SystemFonts.MenuFont, FontStyle.Bold) });
            menu.Items.Add(statusItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(autoItem);
            menu.Items.Add("Show status page", null, (s, e) => OpenUrl("http://localhost:" + Program.Port + "/debug"));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, e) => ExitThread());
            menu.Opening += (s, e) => { autoItem.Checked = AutostartOn(); statusItem.Text = Status(); };

            tray = new NotifyIcon { Icon = MakeIcon(), Text = "NOCTURNE DECK Bridge", ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += (s, e) => OpenUrl("http://localhost:" + Program.Port + "/debug");

            AimpArt.CreateReceiver();   // hidden window that receives album art from AIMP 3.60+
            server = new Server();
            string err = server.Start();
            if (err != null)
            {
                tray.ShowBalloonTip(8000, "NOCTURNE DECK Bridge", err, ToolTipIcon.Warning);
            }
            else if (AutostartOn())
            {
                // keep autostart pointing at this copy, so an older copy never starts again
                SetAutostart(true);
            }
            else if (FirstRun())
            {
                // first start: turn autostart on so it just keeps working (can be switched off in the menu)
                SetAutostart(true);
                tray.ShowBalloonTip(6000, "NOCTURNE DECK Bridge is running",
                    "It sits here in the tray and starts automatically. Right-click to change that or exit.", ToolTipIcon.Info);
            }
        }

        string Status()
        {
            if (!server.Running) return "Not running (port " + Program.Port + " busy)";
            return "v" + Program.Version + " running on localhost:" + Program.Port + " - AIMP: " + Aimp.Api() + (Vlc.Live() ? " - VLC: connected" : "");
        }

        static bool FirstRun()
        {
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\NocturneDeckBridge"))
            {
                if (k.GetValue("Seen") != null) return false;
                k.SetValue("Seen", 1);
                return true;
            }
        }

        static bool AutostartOn()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue(RunName) != null;
        }

        static void SetAutostart(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue(RunName, false);
            }
        }

        void ToggleAutostart() { SetAutostart(!AutostartOn()); autoItem.Checked = AutostartOn(); }

        static void OpenUrl(string url)
        {
            try { System.Diagnostics.Process.Start(url); } catch { }
        }

        static Icon MakeIcon()
        {
            try
            {
                var ico = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (ico != null) return new Icon(ico, 16, 16);
            }
            catch { }
            var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(28, 28, 30));
                using (var b = new SolidBrush(Color.FromArgb(143, 220, 255)))
                {
                    int[] h = { 6, 10, 13, 8, 11 };
                    for (int i = 0; i < h.Length; i++) g.FillRectangle(b, 1 + i * 3, 15 - h[i], 2, h[i]);
                }
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        protected override void ExitThreadCore()
        {
            tray.Visible = false;
            server.Stop();
            base.ExitThreadCore();
        }
    }

    // ------------------------------------------------------------------ HTTP
    class Server
    {
        HttpListener listener;
        Thread thread;
        public bool Running { get { return listener != null && listener.IsListening; } }

        public string Start()
        {
            try
            {
                listener = new HttpListener();
                listener.Prefixes.Add("http://localhost:" + Program.Port + "/");
                listener.Start();
            }
            catch (Exception e)
            {
                listener = null;
                return "Port " + Program.Port + " is in use (" + e.Message + "). Another bridge is still running: exit it or restart the PC once.";
            }
            thread = new Thread(Loop) { IsBackground = true, Name = "http" };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            return null;
        }

        public void Stop() { try { listener.Stop(); } catch { } }

        void Loop()
        {
            while (Running)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); } catch { return; }
                if (ctx.Request.Url.AbsolutePath.TrimEnd(new[] { '/' }).ToLowerInvariant() == "/levels/stream")
                {
                    if (ctx.Request.QueryString["app"] != null) Spectrum.WantApp = ctx.Request.QueryString["app"];
                    var c = ctx;
                    new Thread(() => Meter.Stream(c)) { IsBackground = true, Name = "sse" }.Start();
                    continue;
                }
                try { Handle(ctx); }
                catch (Exception e) { try { Send(ctx, 500, Json.Obj("ok", false, "error", e.Message)); } catch { } }
            }
        }

        static void Handle(HttpListenerContext ctx)
        {
            var q = ctx.Request.QueryString;
            if (ctx.Request.HttpMethod == "OPTIONS") { Send(ctx, 204, ""); return; }
            string path = ctx.Request.Url.AbsolutePath.TrimEnd('/').ToLowerInvariant();
            string app = q["app"] ?? "";

            switch (path)
            {
                case "":
                    Send(ctx, 200, Json.Obj("ok", true, "name", "NOCTURNE DECK Bridge", "version", Program.Version));
                    return;

                case "/version":
                    Send(ctx, 200, Json.Obj("ok", true, "version", Program.Version));
                    return;

                case "/sessions":
                {
                    var list = new List<object>();
                    string current = "";
                    Dictionary<string, object> aimp = Aimp.Info();
                    Dictionary<string, object> vlc = null;
                    try { Remote.Touch(); vlc = Vlc.Info(); } catch { vlc = null; }
                    foreach (var s in Media.Sessions(out current))
                    {
                        if (aimp != null && (string)s["app"] == "AIMP") { aimp["current"] = s["current"]; continue; }
                        if (vlc != null && Vlc.Same((string)s["id"])) { vlc["current"] = s["current"]; continue; }
                        if ((string)s["app"] == "foobar2000") { try { Foobar.Decorate(s); } catch { } }
                        list.Add(s);
                    }
                    if (aimp != null) list.Add(aimp);
                    if (vlc != null) list.Add(vlc);
                    Send(ctx, 200, Json.Obj("ok", true, "version", Program.Version, "current", current, "sessions", list, "volume", Volume.Info("")));
                    return;
                }

                case "/control":
                {
                    string cmd = (q["cmd"] ?? "").ToLowerInvariant();
                    bool ok = app == Aimp.Id ? Aimp.Command(cmd) : app == Vlc.Id ? Vlc.Command(cmd) : Media.Control(app, cmd);
                    Send(ctx, 200, Json.Obj("ok", ok));
                    return;
                }

                case "/volume":
                {
                    string set = q["set"], mute = (q["mute"] ?? "").ToLowerInvariant();
                    bool useAimp = app == Aimp.Id && Aimp.Running();
                    int n;
                    if (!string.IsNullOrEmpty(set) && int.TryParse(set, out n))
                    {
                        if (useAimp) Aimp.SetVolume(n); else Volume.Set(n);
                    }
                    if (mute != "")
                    {
                        bool cur = (bool)Volume.Info(app)["muted"];
                        bool want = mute == "on" ? true : mute == "off" ? false : !cur;
                        if (useAimp) Aimp.SetMute(want); else Volume.Mute(want);
                    }
                    var v = Volume.Info(app);
                    v["ok"] = true;
                    Send(ctx, 200, Json.Write(v));
                    return;
                }

                case "/art":
                {
                    // /art?app=<id>  -> the current album art of that player (image), 404 if none
                    Media.Art a = null;
                    lock (Media.ArtCache) Media.ArtCache.TryGetValue(app, out a);
                    if (a == null || a.Bytes == null) { Send(ctx, 404, Json.Obj("ok", false)); return; }
                    SendBytes(ctx, a.Bytes, a.Type ?? "image/jpeg");
                    return;
                }

                case "/levels":
                    // real output levels: {"l","r" 0..1, "b": 48 bands 0-100, "age": ms}. ?app=<id> = listen to that player only
                    if (q["app"] != null) Spectrum.WantApp = app;
                    Send(ctx, 200, Json.Write(Meter.Read()));
                    return;

                case "/seek":
                {
                    // /seek?app=<id>&pos=<seconds>
                    double sec;
                    bool ok = double.TryParse(q["pos"] ?? "", NumberStyles.Float, CultureInfo.InvariantCulture, out sec) && sec >= 0
                              && (app == Aimp.Id ? Aimp.Seek(sec) : app == Vlc.Id ? Vlc.Seek(sec) : Media.Seek(app, sec));
                    Send(ctx, 200, Json.Obj("ok", ok));
                    return;
                }

                case "/toggle":
                {
                    // /toggle?app=<id>&what=repeat|shuffle[&set=on|off]  (AIMP, VLC, foobar2000 with Beefweb). Without set it flips the state.
                    // The answer also carries the state after the change: "on".
                    string what = (q["what"] ?? "").ToLowerInvariant(), set = (q["set"] ?? "").ToLowerInvariant();
                    if (app == Vlc.Id) { Send(ctx, 200, Json.Write(Vlc.Flag(what, set))); return; }
                    if (Foobar.Is(app)) { Send(ctx, 200, Json.Write(Foobar.Flag(what, set))); return; }
                    bool done = app == Aimp.Id && (set == "on" ? Aimp.SetFlag(what, true) : set == "off" ? Aimp.SetFlag(what, false) : Aimp.Toggle(what));
                    if (!done) { Send(ctx, 200, Json.Obj("ok", false)); return; }
                    Thread.Sleep(60);
                    Send(ctx, 200, Json.Obj("ok", true, "on", what == "repeat" ? Aimp.Repeat() : Aimp.Shuffle()));
                    return;
                }

                case "/playlist":
                {
                    // /playlist?app=<id>[&since=<stamp>]  -> the playlist that player plays from (read-only):
                    // AIMP 4/5, VLC (web interface) and foobar2000 (Beefweb). Sources that can do it carry "playlist": true in /sessions.
                    // Every other player answers ok=false. With since=<stamp of the last answer> the track list is left out while unchanged.
                    if (app == Vlc.Id) { Send(ctx, 200, Json.Write(Vlc.Playlist(q["since"] ?? ""))); return; }
                    if (Foobar.Is(app)) { Send(ctx, 200, Json.Write(Foobar.Playlist(q["since"] ?? ""))); return; }
                    if (app != Aimp.Id || !Aimp.Running()) { Send(ctx, 200, Json.Obj("ok", false, "error", "no playlist for this player")); return; }
                    Send(ctx, 200, Json.Write(AimpPlaylist.Read(Aimp.CurrentFile(), q["since"] ?? "")));
                    return;
                }

                case "/mixer":
                    // /mixer -> every program that plays sound on the default output (volume 0-100, muted, live peak 0..1),
                    //           plus the output devices and which one is the default
                    Send(ctx, 200, Json.Write(Mixer.List()));
                    return;

                case "/mixer/set":
                {
                    // /mixer/set?app=<name from /mixer>[&volume=0..100][&mute=on|off|toggle]
                    int vol = -1, parsed;
                    if (int.TryParse(q["volume"] ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) vol = Math.Max(0, Math.Min(100, parsed));
                    Send(ctx, 200, Json.Write(Mixer.Set(app, vol, (q["mute"] ?? "").ToLowerInvariant())));
                    return;
                }

                case "/output":
                    // /output?set=<id from /mixer outputs>  -> make that device the default output
                    Send(ctx, 200, Json.Write(Mixer.SetOutput(q["set"] ?? "")));
                    return;

                case "/jump":
                {
                    // /jump?app=<id>&index=<n>  -> play track n (0-based) of the playlist that /playlist reports.
                    int idx;
                    if ((app == Vlc.Id || Foobar.Is(app)) && int.TryParse(q["index"] ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture, out idx))
                    { Send(ctx, 200, Json.Write(app == Vlc.Id ? Vlc.Jump(idx) : Foobar.Jump(idx))); return; }
                    if (app != Aimp.Id || !Aimp.Running() || !int.TryParse(q["index"] ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture, out idx))
                    { Send(ctx, 200, Json.Obj("ok", false, "error", "not available")); return; }
                    Send(ctx, 200, Json.Write(AimpPlaylist.Jump(idx)));
                    return;
                }

                case "/debug":
                {
                    string cur;
                    var d = new Dictionary<string, object>();
                    d["ok"] = true;
                    d["bridge"] = "NOCTURNE DECK Bridge " + Program.Version + " (" + (Environment.Is64BitProcess ? "64" : "32") + "-bit)";
                    d["system"] = Environment.OSVersion.Version.ToString();
                    d["aimpRunning"] = Aimp.Running();
                    d["aimpApi"] = Aimp.Api();
                    d["aimp"] = Aimp.Info();
                    if (Aimp.Running() && Aimp.Api() == "new")      // what AIMP answers for mute / repeat / shuffle, unprocessed
                        d["aimpRaw"] = new Dictionary<string, object> { { "mute", Aimp.Raw(0x60) }, { "repeat", Aimp.Raw(0x70) }, { "shuffle", Aimp.Raw(0x80) } };
                    d["mediaError"] = Media.LastError;
                    d["mediaSessions"] = Media.Sessions(out cur);
                    d["volume"] = Volume.Info("");
                    d["spectrum"] = Spectrum.Status;
                    try { d["vlc"] = Vlc.Status(); d["foobar2000"] = Foobar.Status(); } catch { }
                    Send(ctx, 200, Json.Write(d, true), "application/json");
                    return;
                }
            }
            Send(ctx, 404, Json.Obj("ok", false, "error", "not found"));
        }

        static void SendBytes(HttpListenerContext ctx, byte[] b, string type)
        {
            var r = ctx.Response;
            r.StatusCode = 200;
            r.AddHeader("Access-Control-Allow-Origin", "*");
            r.AddHeader("Access-Control-Allow-Private-Network", "true");
            r.AddHeader("Cache-Control", "max-age=3600");
            r.ContentType = type;
            r.ContentLength64 = b.Length;
            r.OutputStream.Write(b, 0, b.Length);
            r.Close();
        }

        static void Send(HttpListenerContext ctx, int code, string body, string type = "application/json")
        {
            var r = ctx.Response;
            r.StatusCode = code;
            r.AddHeader("Access-Control-Allow-Origin", "*");
            r.AddHeader("Access-Control-Allow-Private-Network", "true");
            r.AddHeader("Access-Control-Allow-Headers", "*");
            r.AddHeader("Cache-Control", "no-store");
            r.ContentType = type + "; charset=utf-8";
            byte[] b = Encoding.UTF8.GetBytes(body);
            r.ContentLength64 = b.Length;
            r.OutputStream.Write(b, 0, b.Length);
            r.Close();
        }
    }

    // ------------------------------------------------------------------ Windows media sessions (SMTC)
    // Reached through reflection on the WinRT projection, so no Windows SDK is needed to build.
    static class Media
    {
        public static string LastError = "";
        static object manager;
        static MethodInfo asTaskOp;
        static Type tManager, tProps, tStream;
        static MethodInfo asStreamForRead;

        // album art per player: key = "title|artist", bytes, content type
        public class Art { public string Key; public byte[] Bytes; public string Type; public int Id; public long Hash; public int Since; }
        public static readonly Dictionary<string, Art> ArtCache = new Dictionary<string, Art>();
        static int artSeq;

        static bool Init()
        {
            if (manager != null) return true;
            try
            {
                tManager = Type.GetType("Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager, Windows.Media.Control, ContentType=WindowsRuntime", true);
                tProps = Type.GetType("Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties, Windows.Media.Control, ContentType=WindowsRuntime", true);
                var rt = Assembly.Load("System.Runtime.WindowsRuntime, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");
                try
                {
                    tStream = Type.GetType("Windows.Storage.Streams.IRandomAccessStreamWithContentType, Windows.Storage.Streams, ContentType=WindowsRuntime", true);
                    asStreamForRead = rt.GetType("System.IO.WindowsRuntimeStreamExtensions").GetMethods()
                        .First(m => m.Name == "AsStreamForRead" && m.GetParameters().Length == 1);
                }
                catch { tStream = null; }
                asTaskOp = rt.GetType("System.WindowsRuntimeSystemExtensions").GetMethods()
                    .First(m => m.Name == "AsTask" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.Name == "IAsyncOperation`1");
                manager = Await(tManager.GetMethod("RequestAsync").Invoke(null, null), tManager, 5000);
                LastError = "";
                return true;
            }
            catch (Exception e)
            {
                LastError = Inner(e).Message;
                manager = null;
                return false;
            }
        }

        static Exception Inner(Exception e) { while (e.InnerException != null) e = e.InnerException; return e; }

        static object Await(object op, Type result, int ms)
        {
            var task = (Task)asTaskOp.MakeGenericMethod(result).Invoke(null, new[] { op });
            if (!task.Wait(ms)) throw new TimeoutException("WinRT call timed out");
            return task.GetType().GetProperty("Result").GetValue(task, null);
        }

        static object Call(object o, string name) { return o.GetType().GetMethod(name, Type.EmptyTypes).Invoke(o, null); }
        static object Get(object o, string name) { return o.GetType().GetProperty(name).GetValue(o, null); }

        static IEnumerable<object> All()
        {
            var list = Call(manager, "GetSessions") as IEnumerable;
            if (list == null) yield break;
            foreach (var s in list) yield return s;
        }

        public static List<Dictionary<string, object>> Sessions(out string current)
        {
            current = "";
            var outList = new List<Dictionary<string, object>>();
            if (!Init()) return outList;
            try
            {
                var cur = Call(manager, "GetCurrentSession");
                if (cur != null) current = (string)Get(cur, "SourceAppUserModelId");
                foreach (var s in All()) outList.Add(Describe(s, current));
            }
            catch (Exception e) { LastError = Inner(e).Message; manager = null; }
            return outList;
        }

        static Dictionary<string, object> Describe(object s, string current)
        {
            string id = (string)Get(s, "SourceAppUserModelId");
            var d = new Dictionary<string, object>();
            d["id"] = id; d["app"] = Names.Friendly(id); d["current"] = id == current;
            d["title"] = ""; d["artist"] = ""; d["album"] = ""; d["status"] = "unknown";
            d["position"] = 0.0; d["duration"] = 0.0;
            try
            {
                var p = Await(Call(s, "TryGetMediaPropertiesAsync"), tProps, 1500);
                d["title"] = Get(p, "Title") ?? ""; d["artist"] = Get(p, "Artist") ?? ""; d["album"] = Get(p, "AlbumTitle") ?? "";
                d["art"] = ArtFor(id, d["title"] + "|" + d["artist"], p);
            }
            catch { }
            try
            {
                var pb = Call(s, "GetPlaybackInfo");
                d["status"] = Get(pb, "PlaybackStatus").ToString().ToLowerInvariant();
            }
            catch { }
            try
            {
                var tl = Call(s, "GetTimelineProperties");
                var pos = (TimeSpan)Get(tl, "Position");
                var end = (TimeSpan)Get(tl, "EndTime") - (TimeSpan)Get(tl, "StartTime");
                var upd = (DateTimeOffset)Get(tl, "LastUpdatedTime");
                double p = pos.TotalSeconds;
                if ((string)d["status"] == "playing" && upd.Year > 2000) p += (DateTimeOffset.Now - upd).TotalSeconds;
                if (end.TotalSeconds > 0) p = Math.Min(p, end.TotalSeconds);
                d["position"] = Math.Round(Math.Max(0, p), 1);
                d["duration"] = Math.Round(Math.Max(0, end.TotalSeconds), 1);
            }
            catch { }
            return d;
        }

        // Reads the thumbnail once per track; returns a short key the widget uses to reload the image ("" = none).
        // Reads the thumbnail per track. Players often publish the new title before the new
        // picture, so for 10 s after a track change the picture is re-read and replaced if it changes.
        static string ArtFor(string app, string key, object props)
        {
            Art a;
            lock (ArtCache)
                if (ArtCache.TryGetValue(app, out a) && a.Key == key && Environment.TickCount - a.Since > 10000)
                    return a.Bytes != null ? a.Id.ToString() : "";
            byte[] bytes = null; string type = null;
            try
            {
                var thumb = Get(props, "Thumbnail");
                if (thumb != null && tStream != null)
                {
                    var open = thumb.GetType().GetMethod("OpenReadAsync");
                    var stream = Await(open.Invoke(thumb, null), tStream, 1500);
                    using (var net = (Stream)asStreamForRead.Invoke(null, new[] { stream }))
                    using (var ms = new MemoryStream())
                    {
                        net.CopyTo(ms);
                        if (ms.Length > 0 && ms.Length < 8 * 1024 * 1024) bytes = ms.ToArray();
                    }
                    try { type = (string)Get(stream, "ContentType"); } catch { }
                    try { ((IDisposable)stream).Dispose(); } catch { }
                }
            }
            catch { bytes = null; }
            long hash = Hash(bytes);
            lock (ArtCache)
            {
                if (ArtCache.TryGetValue(app, out a) && a.Key == key)
                {
                    if (a.Hash == hash) return a.Bytes != null ? a.Id.ToString() : "";   // unchanged
                }
                else a = null;
                var n = new Art { Key = key, Id = Interlocked.Increment(ref artSeq), Hash = hash, Since = a != null ? a.Since : Environment.TickCount };
                n.Bytes = bytes != null ? ArtTools.Trim(bytes) : null;
                n.Type = n.Bytes != null ? (n.Bytes == bytes && !string.IsNullOrEmpty(type) ? type : Sniff(n.Bytes)) : null;
                ArtCache[app] = n;
                return n.Bytes != null ? n.Id.ToString() : "";
            }
        }

        static long Hash(byte[] b)
        {
            if (b == null) return 0;
            long h = 1469598103934665603L;
            for (int i = 0; i < b.Length; i += Math.Max(1, b.Length / 4096)) h = (h ^ b[i]) * 1099511628211L;
            return h ^ b.Length;
        }

        public static string Sniff(byte[] b)
        {
            if (b.Length > 3 && b[0] == 0x89 && b[1] == 0x50) return "image/png";
            if (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8) return "image/jpeg";
            if (b.Length > 12 && b[8] == 0x57 && b[9] == 0x45) return "image/webp";
            return "image/jpeg";
        }

        public static bool Seek(string app, double sec)
        {
            if (!Init()) return false;
            object s = null;
            foreach (var x in All()) if ((string)Get(x, "SourceAppUserModelId") == app) { s = x; break; }
            if (s == null && app == "") s = Call(manager, "GetCurrentSession");
            if (s == null) return false;
            var m = s.GetType().GetMethod("TryChangePlaybackPositionAsync");
            if (m == null) return false;
            return (bool)Await(m.Invoke(s, new object[] { TimeSpan.FromSeconds(sec).Ticks }), typeof(bool), 3000);
        }

        public static bool Control(string app, string cmd)
        {
            if (!Init()) return false;
            object s = null;
            foreach (var x in All()) if ((string)Get(x, "SourceAppUserModelId") == app) { s = x; break; }
            if (s == null && app == "") s = Call(manager, "GetCurrentSession");
            if (s == null) return false;
            string m = cmd == "toggle" ? "TryTogglePlayPauseAsync" : cmd == "play" ? "TryPlayAsync" : cmd == "pause" ? "TryPauseAsync"
                     : cmd == "stop" ? "TryStopAsync" : cmd == "next" ? "TrySkipNextAsync" : cmd == "prev" ? "TrySkipPreviousAsync" : null;
            if (m == null) return false;
            bool ok = (bool)Await(Call(s, m), typeof(bool), 3000);
            if (!ok && cmd == "stop") ok = (bool)Await(Call(s, "TryPauseAsync"), typeof(bool), 3000); // some players ignore Stop
            return ok;
        }
    }

    static class Names
    {
        static readonly string[][] Known = {
            new[]{"spotify","Spotify"}, new[]{"chrome","Chrome"}, new[]{"msedge","Edge"}, new[]{"308046b0af4a39cb","Firefox"},
            new[]{"firefox","Firefox"}, new[]{"opera","Opera"}, new[]{"brave","Brave"}, new[]{"vivaldi","Vivaldi"}, new[]{"aimp","AIMP"},
            new[]{"vlc","VLC"}, new[]{"foobar2000","foobar2000"}, new[]{"applemusic","Apple Music"}, new[]{"itunes","iTunes"},
            new[]{"zunemusic","Media Player"}, new[]{"zunevideo","Movies & TV"}, new[]{"tidal","TIDAL"}, new[]{"deezer","Deezer"},
            new[]{"discord","Discord"}, new[]{"musicbee","MusicBee"}, new[]{"winamp","Winamp"}, new[]{"potplayer","PotPlayer"},
            new[]{"mpc-hc","MPC-HC"}, new[]{"amazonmusic","Amazon Music"}, new[]{"youtubemusic","YouTube Music"},
        };

        public static string Friendly(string id)
        {
            string low = (id ?? "").ToLowerInvariant();
            foreach (var k in Known) if (low.Contains(k[0])) return k[1];
            string n = id ?? "";
            if (n.Contains("!")) n = n.Split('!').Last();
            if (n.Contains("_")) n = n.Split('_')[0];
            if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 4);
            if (n.Contains(".")) n = n.Split('.').Last();
            return n;
        }
    }

    // ------------------------------------------------------------------ AIMP remote API
    // AIMP 2/3/4/5 keep a hidden "AIMP2_RemoteInfo" window and a shared memory block with the track.
    //  "new"    AIMP 3.60+: WM_AIMP_PROPERTY + command ids 13-18
    //  "legacy" AIMP 2 and AIMP 3.00-3.55: WM_AIMP_COMMAND with STATUS_GET(1) / STATUS_SET(2) / CALLFUNC(3)
    static class Aimp
    {
        public const string Id = "aimp-remote";
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
        const uint WM_USER = 0x400, WM_AIMP_COMMAND = WM_USER + 0x75, WM_AIMP_PROPERTY = WM_USER + 0x77;
        const string NAME = "AIMP2_RemoteInfo";

        static IntPtr Wnd() { return FindWindow(NAME, null); }
        public static bool Running() { try { return Wnd() != IntPtr.Zero; } catch { return false; } }

        static long Send(uint msg, long w, long l)
        {
            IntPtr h = Wnd(), r;
            if (h == IntPtr.Zero) return -1;
            if (SendMessageTimeout(h, msg, (IntPtr)w, (IntPtr)l, 2 /* abort if hung */, 700, out r) == IntPtr.Zero) return -1;
            return (long)r;
        }

        // Like Send, but tells "no answer" apart from the value: AIMP answers "on" for mute / repeat / shuffle with a
        // non-zero value that is not always 1 (seen with AIMP 5.40), and -1 is also what Send returns on failure.
        static bool TrySend(uint msg, long w, long l, out long value)
        {
            value = 0;
            IntPtr h = Wnd(), r;
            if (h == IntPtr.Zero) return false;
            if (SendMessageTimeout(h, msg, (IntPtr)w, (IntPtr)l, 2 /* abort if hung */, 700, out r) == IntPtr.Zero) return false;
            value = (long)r;
            return true;
        }
        static bool On(int prop) { long v; return TrySend(WM_AIMP_PROPERTY, prop, 0, out v) && v != 0; }
        public static long Raw(int prop) { long v; return TrySend(WM_AIMP_PROPERTY, prop, 0, out v) ? v : long.MinValue; }

        public static string Api()
        {
            if (!Running()) return "not running";
            return Send(WM_AIMP_PROPERTY, 0x10, 0) > 0 ? "new" : "legacy";
        }
        static bool IsNew() { return Api() == "new"; }

        static long Status(int sts) { return Send(WM_AIMP_COMMAND, 1, sts); }
        static bool SetStatus(int sts, int v) { return Send(WM_AIMP_COMMAND, 2, ((long)sts << 16) | (uint)(v & 0xFFFF)) >= 0; }
        static bool CallFn(int fn) { return Send(WM_AIMP_COMMAND, 3, fn) >= 0; }

        // 0 stopped, 1 paused, 2 playing
        public static int State()
        {
            if (IsNew()) return (int)Send(WM_AIMP_PROPERTY, 0x40, 0);
            long v = Status(4);               // legacy: 0 stop, 1 play, 2 pause
            return v == 1 ? 2 : v == 2 ? 1 : 0;
        }
        static long PositionMs() { return IsNew() ? Send(WM_AIMP_PROPERTY, 0x20, 0) : Status(31) * 1000; }
        static long DurationMs() { return IsNew() ? Send(WM_AIMP_PROPERTY, 0x30, 0) : Status(32) * 1000; }
        public static int Volume() { return (int)(IsNew() ? Send(WM_AIMP_PROPERTY, 0x50, 0) : Status(1)); }
        public static bool Muted() { return IsNew() ? On(0x60) : Status(5) == 1; }

        // repeat: new 0x70 / legacy 29, shuffle: new 0x80 / legacy 41
        static bool Flag(int prop, int sts) { return IsNew() ? On(prop) : Status(sts) == 1; }
        public static bool Shuffle() { return Flag(0x80, 41); }
        public static bool Repeat() { return Flag(0x70, 29); }
        public static bool Toggle(string what)
        {
            if (!Running()) return false;
            int prop = what == "repeat" ? 0x70 : what == "shuffle" ? 0x80 : 0, sts = what == "repeat" ? 29 : what == "shuffle" ? 41 : 0;
            if (prop == 0) return false;
            return SetFlag(what, !Flag(prop, sts));
        }
        // Sets repeat / shuffle to a given state (no reading involved).
        public static bool SetFlag(string what, bool on)
        {
            if (!Running()) return false;
            int prop = what == "repeat" ? 0x70 : what == "shuffle" ? 0x80 : 0, sts = what == "repeat" ? 29 : what == "shuffle" ? 41 : 0;
            if (prop == 0) return false;
            if (IsNew()) Send(WM_AIMP_PROPERTY, prop | 1, on ? 1 : 0); else SetStatus(sts, on ? 1 : 0);
            return true;
        }

        // AIMP 3.60+: asks AIMP to send the current cover to our window via WM_COPYDATA.
        public static bool RequestAlbumArt(IntPtr replyTo)
        {
            if (!Running() || !IsNew()) return false;
            return Send(WM_AIMP_COMMAND, 29, replyTo.ToInt64()) >= 0;
        }

        public static bool Seek(double sec)
        {
            if (!Running()) return false;
            if (IsNew()) return Send(WM_AIMP_PROPERTY, 0x20 | 1, (long)(sec * 1000)) >= 0;
            return SetStatus(31, (int)Math.Min(65535, sec));
        }

        public static void SetVolume(int v)
        {
            v = Math.Max(0, Math.Min(100, v));
            if (IsNew()) Send(WM_AIMP_PROPERTY, 0x50 | 1, v); else SetStatus(1, v);
        }
        public static void SetMute(bool m) { if (IsNew()) Send(WM_AIMP_PROPERTY, 0x60 | 1, m ? 1 : 0); else SetStatus(5, m ? 1 : 0); }

        public static bool Command(string c)
        {
            if (!Running()) return false;
            if (IsNew())
            {
                int id = c == "play" ? 13 : c == "toggle" ? 14 : c == "pause" ? 15 : c == "stop" ? 16 : c == "next" ? 17 : c == "prev" ? 18 : 0;
                return id != 0 && Send(WM_AIMP_COMMAND, id, 0) >= 0;
            }
            int st = State();
            if (c == "toggle") c = st == 2 ? "pause" : "play";
            if (c == "pause" && st != 2) return true;      // legacy pause toggles: only send while playing
            int fn = c == "play" ? (st == 1 ? 16 : 15) : c == "pause" ? 16 : c == "stop" ? 17 : c == "next" ? 18 : c == "prev" ? 19 : 0;
            return fn != 0 && CallFn(fn);
        }

        // Full path (or URL) of the track AIMP has loaded, "" if none. Reads the same shared memory block as Info().
        public static string CurrentFile()
        {
            try
            {
                using (var mmf = MemoryMappedFile.OpenExisting(NAME, MemoryMappedFileRights.Read))
                using (var v = mmf.CreateViewAccessor(0, 2048, MemoryMappedFileAccess.Read))
                {
                    int header = v.ReadInt32(0);
                    if (header < 88 || header > 512) header = 88;
                    if (v.ReadInt32(4) == 0) return "";
                    long pos = header;
                    for (int i = 0; i < 6; i++)
                    {
                        int len = Math.Max(0, v.ReadInt32(40 + i * 4));
                        if (i == 3)                                   // memory order: album, artist, date, fileName, genre, title
                        {
                            int n = len;
                            if (pos + n * 2 > 2048) n = (int)Math.Max(0, (2048 - pos) / 2);
                            var b = new byte[n * 2];
                            v.ReadArray(pos, b, 0, b.Length);
                            return Encoding.Unicode.GetString(b).TrimEnd('\0');
                        }
                        pos += len * 2;
                    }
                }
            }
            catch { }
            return "";
        }

        public static Dictionary<string, object> Info()
        {
            if (!Running()) return null;
            var d = new Dictionary<string, object>();
            string title = "", artist = "", album = "", file = "";
            int kbps = 0, hz = 0, ch = 0; long fsize = 0;
            try
            {
                using (var mmf = MemoryMappedFile.OpenExisting(NAME, MemoryMappedFileRights.Read))
                using (var v = mmf.CreateViewAccessor(0, 2048, MemoryMappedFileAccess.Read))
                {
                    int header = v.ReadInt32(0);
                    if (header < 88 || header > 512) header = 88;
                    bool active = v.ReadInt32(4) != 0;
                    kbps = v.ReadInt32(8); ch = v.ReadInt32(12); hz = v.ReadInt32(32); fsize = v.ReadInt64(20);
                    var len = new int[6];
                    for (int i = 0; i < 6; i++) len[i] = Math.Max(0, v.ReadInt32(40 + i * 4));
                    var txt = new string[6];
                    long pos = header;
                    for (int i = 0; i < 6; i++)
                    {
                        int n = len[i];
                        if (pos + n * 2 > 2048) n = (int)Math.Max(0, (2048 - pos) / 2);
                        var b = new byte[n * 2];
                        v.ReadArray(pos, b, 0, b.Length);
                        txt[i] = Encoding.Unicode.GetString(b).TrimEnd('\0');
                        pos += len[i] * 2;
                    }
                    // memory order: album, artist, date, fileName, genre, title
                    if (active) { album = txt[0]; artist = txt[1]; file = txt[3]; title = txt[5]; }
                }
            }
            catch { }
            if (title == "" && file != "") title = Path.GetFileNameWithoutExtension(file);
            if (kbps <= 0 && !IsNew()) kbps = (int)Math.Max(0, Status(35));

            int state = State();
            d["id"] = Id; d["app"] = "AIMP"; d["current"] = false;
            d["title"] = title; d["artist"] = artist; d["album"] = album;
            d["status"] = state == 2 ? "playing" : state == 1 ? "paused" : "stopped";
            d["position"] = Math.Round(Math.Max(0, PositionMs()) / 1000.0, 1);
            d["duration"] = Math.Round(Math.Max(0, DurationMs()) / 1000.0, 1);
            d["bitrate"] = Math.Max(0, kbps); d["sampleRate"] = Math.Max(0, hz); d["channels"] = Math.Max(0, ch);
            d["volume"] = Volume(); d["muted"] = Muted(); d["api"] = Api();
            d["art"] = AimpArt.For(file, title + "|" + artist);
            string ext = file != "" ? Path.GetExtension(file).TrimStart(new[] { '.' }).ToUpperInvariant() : "";
            d["format"] = ext.Length > 0 && ext.Length <= 5 ? ext : (file.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? "RADIO" : "");
            d["fileSizeMB"] = Math.Round(Math.Max(0, fsize) / 1048576.0, 2);
            d["repeat"] = Flag(0x70, 29); d["shuffle"] = Flag(0x80, 41);
            return d;
        }
    }

    // ------------------------------------------------------------------ Windows master volume (Core Audio)
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr p);
        int UnregisterControlChangeNotify(IntPtr p);
        int GetChannelCount(out int c);
        int SetMasterVolumeLevel(float l, ref Guid ctx);
        int SetMasterVolumeLevelScalar(float l, ref Guid ctx);
        int GetMasterVolumeLevel(out float l);
        int GetMasterVolumeLevelScalar(out float l);
        int SetChannelVolumeLevel(uint ch, float l, ref Guid ctx);
        int SetChannelVolumeLevelScalar(uint ch, float l, ref Guid ctx);
        int GetChannelVolumeLevel(uint ch, out float l);
        int GetChannelVolumeLevelScalar(uint ch, out float l);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool m, ref Guid ctx);
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool m);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o);
        int OpenPropertyStore(int access, out IntPtr store);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }
    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        int GetBufferSize(out uint frames);
        int GetStreamLatency(out long latency);
        int GetCurrentPadding(out uint padding);
        int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        int GetMixFormat(out IntPtr format);
        int GetDevicePeriod(out long def, out long min);
        int Start();
        int Stop();
        int Reset();
        int SetEventHandle(IntPtr h);
        int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object o);
    }
    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devPos, out ulong qpcPos);
        int ReleaseBuffer(uint frames);
        int GetNextPacketSize(out uint frames);
    }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int flow, int state, out IntPtr devices);
        int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice ep);
    }
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCom { }

    // ------------------------------------------------------------------ AIMP album art
    // 1. AIMP 3.60+: GET_ALBUMART, the picture arrives as WM_COPYDATA on a hidden window.
    // 2. Any AIMP version: the picture inside the file (MP3 ID3v2 APIC, FLAC PICTURE),
    // 3. or cover.jpg / folder.jpg / front.jpg next to the file.
    static class AimpArt
    {
        const int WM_COPYDATA = 0x004A;
        const long ALBUMART_ID = 0x41495043;
        [StructLayout(LayoutKind.Sequential)] struct COPYDATASTRUCT { public IntPtr dwData; public int cbData; public IntPtr lpData; }

        class Receiver : NativeWindow
        {
            public Receiver() { CreateHandle(new CreateParams { Caption = "NocturneArtReceiver" }); }
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_COPYDATA)
                {
                    try
                    {
                        var cds = (COPYDATASTRUCT)Marshal.PtrToStructure(m.LParam, typeof(COPYDATASTRUCT));
                        if (cds.dwData.ToInt64() == ALBUMART_ID && cds.cbData > 0 && cds.cbData < 16 * 1024 * 1024)
                        {
                            var b = new byte[cds.cbData];
                            Marshal.Copy(cds.lpData, b, 0, b.Length);
                            received = b;
                        }
                    }
                    catch { }
                    m.Result = (IntPtr)1;
                    return;
                }
                base.WndProc(ref m);
            }
        }

        static Receiver receiver;
        static volatile byte[] received;
        static string lastKey;
        static int seq;

        public static void CreateReceiver() { try { receiver = new Receiver(); } catch { receiver = null; } }

        // Returns the art key for the widget ("" = none) and fills Media.ArtCache["aimp-remote"].
        public static string For(string file, string trackKey)
        {
            string key = file + "|" + trackKey;
            Media.Art a;
            lock (Media.ArtCache)
                if (lastKey == key && Media.ArtCache.TryGetValue(Aimp.Id, out a)) return a.Bytes != null ? a.Id.ToString() : "";
            lastKey = key;
            byte[] img = null;
            try
            {
                if (receiver != null && receiver.Handle != IntPtr.Zero)
                {
                    received = null;
                    if (Aimp.RequestAlbumArt(receiver.Handle))
                    {
                        for (int i = 0; i < 20 && received == null; i++) Thread.Sleep(15);
                        img = received;
                    }
                }
            }
            catch { }
            if (img == null && !string.IsNullOrEmpty(file) && !file.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                try { img = Embedded(file); } catch { }
                if (img == null) try { img = Folder(file); } catch { }
            }
            a = new Media.Art { Key = key, Id = Interlocked.Increment(ref seq) + 100000, Bytes = img, Type = img != null ? Media.Sniff(img) : null };
            lock (Media.ArtCache) Media.ArtCache[Aimp.Id] = a;
            return img != null ? a.Id.ToString() : "";
        }

        static readonly string[] Names = { "cover", "folder", "front", "album", "albumart", "albumartsmall" };
        static byte[] Folder(string file)
        {
            string dir = Path.GetDirectoryName(file);
            if (dir == null || !Directory.Exists(dir)) return null;
            string best = null;
            foreach (var f in Directory.GetFiles(dir))
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext != ".jpg" && ext != ".jpeg" && ext != ".png") continue;
                string n = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                if (Names.Any(x => n == x || n.StartsWith(x + "_") || n.StartsWith("albumart_"))) { best = f; if (n == "cover" || n == "folder") break; }
            }
            if (best == null) return null;
            var fi = new FileInfo(best);
            return fi.Length > 0 && fi.Length < 16 * 1024 * 1024 ? File.ReadAllBytes(best) : null;
        }

        static byte[] Embedded(string file)
        {
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var head = new byte[10];
                if (fs.Read(head, 0, 10) < 10) return null;
                if (head[0] == 'I' && head[1] == 'D' && head[2] == '3') return Id3(fs, head);
                if (head[0] == 'f' && head[1] == 'L' && head[2] == 'a' && head[3] == 'C') { fs.Position = 4; return Flac(fs); }
            }
            return null;
        }

        static int Syncsafe(byte[] b, int o) { return (b[o] & 0x7F) << 21 | (b[o + 1] & 0x7F) << 14 | (b[o + 2] & 0x7F) << 7 | (b[o + 3] & 0x7F); }
        static int BE(byte[] b, int o) { return b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]; }

        static byte[] Id3(FileStream fs, byte[] h)
        {
            int ver = h[3], size = Syncsafe(h, 6);
            if (size <= 0 || size > 32 * 1024 * 1024) return null;
            var tag = new byte[size];
            int got = 0; while (got < size) { int n = fs.Read(tag, got, size - got); if (n <= 0) break; got += n; }
            int pos = 0;
            if ((h[5] & 0x40) != 0 && ver >= 3) pos += ver == 4 ? Syncsafe(tag, 0) : BE(tag, 0) + 4;   // extended header
            byte[] best = null; bool bestFront = false;
            while (pos + (ver == 2 ? 6 : 10) <= got)
            {
                string id; int fsz, hdr;
                if (ver == 2) { id = Encoding.ASCII.GetString(tag, pos, 3); fsz = tag[pos + 3] << 16 | tag[pos + 4] << 8 | tag[pos + 5]; hdr = 6; }
                else { id = Encoding.ASCII.GetString(tag, pos, 4); fsz = ver == 4 ? Syncsafe(tag, pos + 4) : BE(tag, pos + 4); hdr = 10; }
                if (fsz <= 0 || id[0] == 0 || pos + hdr + fsz > got) break;
                if (id == "APIC" || id == "PIC")
                {
                    int p = pos + hdr, end = p + fsz;
                    int enc = tag[p++];
                    if (id == "PIC") p += 3; else { while (p < end && tag[p] != 0) p++; p++; }   // image format / MIME
                    int picType = p < end ? tag[p++] : 0;
                    if (enc == 1 || enc == 2) { while (p + 1 < end && !(tag[p] == 0 && tag[p + 1] == 0)) p += 2; p += 2; }
                    else { while (p < end && tag[p] != 0) p++; p++; }
                    if (p < end)
                    {
                        var img = new byte[end - p];
                        Buffer.BlockCopy(tag, p, img, 0, img.Length);
                        if (best == null || (picType == 3 && !bestFront)) { best = img; bestFront = picType == 3; }
                    }
                }
                pos += hdr + fsz;
            }
            return best;
        }

        static byte[] Flac(FileStream fs)
        {
            var h = new byte[4];
            for (int guard = 0; guard < 128; guard++)
            {
                if (fs.Read(h, 0, 4) < 4) return null;
                bool last = (h[0] & 0x80) != 0;
                int type = h[0] & 0x7F, len = h[1] << 16 | h[2] << 8 | h[3];
                if (type == 6 && len > 32 && len < 32 * 1024 * 1024)
                {
                    var b = new byte[len];
                    int got = 0; while (got < len) { int n = fs.Read(b, got, len - got); if (n <= 0) break; got += n; }
                    int p = 4;
                    int ml = BE(b, p); p += 4 + ml;
                    int dl = BE(b, p); p += 4 + dl;
                    p += 16;
                    int size = BE(b, p); p += 4;
                    if (size > 0 && p + size <= got) { var img = new byte[size]; Buffer.BlockCopy(b, p, img, 0, size); return img; }
                    return null;
                }
                fs.Position += len;
                if (last) return null;
            }
            return null;
        }
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioMeterInformation
    {
        int GetPeakValue(out float peak);
        int GetMeteringChannelCount(out int count);
        int GetChannelsPeakValues(int count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0), Out] float[] peaks);
        int QueryHardwareSupport(out int mask);
    }

    // Real output levels for the VU / PPM meters. Reads the peak meter of the default
    // playback device (what you hear), ~40 times a second, only while the widget asks.
    static class Meter
    {
        static readonly object gate = new object();
        static Thread worker;
        static long lastAsk;                // ms tick of the last request
        static float l, r;                  // 0..1, already on a 50 dB scale
        static long stamp;

        public static bool Wanted() { return Environment.TickCount - Interlocked.Read(ref lastAsk) < 5000; }

        public static Dictionary<string, object> Read()
        {
            Touch();
            var d = new Dictionary<string, object>();
            float cl, cr;
            if (Spectrum.PeaksLive(out cl, out cr)) { d["l"] = Math.Round(Scale(cl), 3); d["r"] = Math.Round(Scale(cr), 3); d["age"] = 0; }
            else lock (gate) { d["l"] = Math.Round(l, 3); d["r"] = Math.Round(r, 3); d["age"] = Environment.TickCount - stamp; }
            int bage; var b = Spectrum.Read(out bage);
            if (bage < 500) d["b"] = b;
            return d;
        }

        public static void Touch()
        {
            Interlocked.Exchange(ref lastAsk, Environment.TickCount);
            lock (gate)
            {
                if (worker != null && worker.IsAlive) return;
                worker = new Thread(Run) { IsBackground = true, Name = "meter" };
                worker.SetApartmentState(ApartmentState.MTA);
                worker.Start();
            }
            Spectrum.Ensure();
        }

        static IAudioMeterInformation Open()
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            IMMDevice dev;
            Marshal.ThrowExceptionForHR(en.GetDefaultAudioEndpoint(0, 1, out dev));
            Guid iid = typeof(IAudioMeterInformation).GUID;
            object o;
            Marshal.ThrowExceptionForHR(dev.Activate(ref iid, 23, IntPtr.Zero, out o));
            return (IAudioMeterInformation)o;
        }

        static float Scale(float p) { double db = 20 * Math.Log10(Math.Max(p, 1e-5)); return (float)Math.Max(0, Math.Min(1, (db + 50) / 50)); }

        static void Run()
        {
            IAudioMeterInformation m = null;
            int opened = 0;
            var peaks = new float[8];
            while (Wanted())   // stop when nobody asks
            {
                try
                {
                    if (m == null || Environment.TickCount - opened > 3000) { m = Open(); opened = Environment.TickCount; } // follow device changes
                    int n; m.GetMeteringChannelCount(out n);
                    n = Math.Max(1, Math.Min(8, n));
                    m.GetChannelsPeakValues(n, peaks);
                    float a = Scale(peaks[0]), b = Scale(n > 1 ? peaks[1] : peaks[0]);
                    lock (gate) { l = a; r = b; stamp = Environment.TickCount; }
                }
                catch { m = null; lock (gate) { l = 0; r = 0; } Thread.Sleep(500); }
                Thread.Sleep(25);
            }
        }

        // Server-Sent Events: one "data: {l,r}" line ~30 times a second until the widget disconnects.
        public static void Stream(HttpListenerContext ctx)
        {
            var res = ctx.Response;
            try
            {
                res.AddHeader("Access-Control-Allow-Origin", "*");
                res.AddHeader("Access-Control-Allow-Private-Network", "true");
                res.AddHeader("Cache-Control", "no-store");
                res.ContentType = "text/event-stream; charset=utf-8";
                res.SendChunked = true;
                var o = res.OutputStream;
                var hello = Encoding.UTF8.GetBytes("retry: 2000\n\n");
                o.Write(hello, 0, hello.Length); o.Flush();
                while (true)
                {
                    var b = Encoding.UTF8.GetBytes("data: " + Json.Write(Read()) + "\n\n");
                    o.Write(b, 0, b.Length); o.Flush();
                    Thread.Sleep(33);
                }
            }
            catch { }
            finally { try { res.Close(); } catch { } }
        }
    }

    // Real spectrum: listens to what the speakers play (WASAPI loopback, nothing is recorded
    // or stored), runs a 2048-point FFT ~30 times a second and folds it into 48 bands.
    static class Spectrum
    {
        public const int Bands = 48;
        const int N = 2048;
        static readonly object gate = new object();
        static Thread worker;
        static readonly float[] ring = new float[N];
        static int ringPos;
        static readonly int[] bands = new int[Bands];
        static long stamp;
        public static string Status = "idle";

        public static int[] Read(out int age)
        {
            lock (gate) { age = (int)(Environment.TickCount - stamp); return (int[])bands.Clone(); }
        }

        public static void Ensure()
        {
            lock (gate)
            {
                if (worker != null && worker.IsAlive) return;
                worker = new Thread(Run) { IsBackground = true, Name = "spectrum" };
                worker.SetApartmentState(ApartmentState.MTA);
                worker.Start();
            }
        }

        static void Run()
        {
            while (Meter.Wanted())
            {
                try { Capture(); }
                catch (Exception e)
                {
                    Status = "error: " + e.Message;
                    if (attemptPid > 0) failedPid = attemptPid;     // per-app capture failed: next time use the whole PC
                    lock (gate) Array.Clear(bands, 0, Bands);
                    Thread.Sleep(1000);
                }
            }
            Status = "idle";
        }

        // Which player the visualizer should listen to ("" = the whole PC). Set by /levels?app=...
        public static volatile string WantApp = "";
        static int attemptPid, failedPid = -1;
        static float peakL, peakR;          // per-interval sample peaks -> VU
        public static int PeakStamp;
        public static float L, R;

        static void Capture()
        {
            string app = WantApp;
            int pid = string.IsNullOrEmpty(app) ? 0 : Procs.RootPid(app);
            if (pid == failedPid) pid = 0;
            attemptPid = pid;
            IAudioClient client = null; IntPtr fmt = IntPtr.Zero; bool ownFmt = false; string mode;
            IMMDeviceEnumerator en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            string devId = null;
            if (pid > 0)
            {
                try { client = ProcessLoopback.Activate(pid); fmt = ProcessLoopback.FloatFormat(); ownFmt = true; }
                catch (Exception e) { client = null; Status = "per-app capture unavailable (" + e.Message + "), using whole PC"; }
            }
            if (client == null)
            {
                IMMDevice dev;
                Marshal.ThrowExceptionForHR(en.GetDefaultAudioEndpoint(0, 1, out dev));
                dev.GetId(out devId);
                Guid iid = typeof(IAudioClient).GUID;
                object o;
                Marshal.ThrowExceptionForHR(dev.Activate(ref iid, 23, IntPtr.Zero, out o));
                client = (IAudioClient)o;
                Marshal.ThrowExceptionForHR(client.GetMixFormat(out fmt));
                mode = "whole PC";
            }
            else mode = "only " + app + " (pid " + pid + ")";
            var evt = new AutoResetEvent(false);
            try
            {
                int tag = Marshal.ReadInt16(fmt, 0), ch = Marshal.ReadInt16(fmt, 2), rate = Marshal.ReadInt32(fmt, 4);
                int block = Marshal.ReadInt16(fmt, 12), bits = Marshal.ReadInt16(fmt, 14);
                bool isFloat = tag == 3;
                if (tag == unchecked((short)0xFFFE)) isFloat = Marshal.ReadInt32(fmt, 24) == 3;   // extensible: SubFormat.Data1
                if (ownFmt)
                {
                    // process loopback: event driven, Windows converts to our float format
                    Marshal.ThrowExceptionForHR(client.Initialize(0, 0x00020000 | 0x00040000 | unchecked((int)0x80000000) | 0x08000000, 200000, 0, fmt, IntPtr.Zero));
                    Marshal.ThrowExceptionForHR(client.SetEventHandle(evt.SafeWaitHandle.DangerousGetHandle()));
                }
                else Marshal.ThrowExceptionForHR(client.Initialize(0, 0x00020000 /* LOOPBACK */, 2000000, 0, fmt, IntPtr.Zero));
                Guid cid = typeof(IAudioCaptureClient).GUID;
                object co;
                Marshal.ThrowExceptionForHR(client.GetService(ref cid, out co));
                var cap = (IAudioCaptureClient)co;
                Marshal.ThrowExceptionForHR(client.Start());
                Status = "capturing " + mode + ", " + rate + " Hz, " + ch + " ch, " + (isFloat ? "float" : bits + "-bit");
                var re = new double[N]; var im = new double[N]; var win = new double[N];
                var fbuf = new float[8192];
                for (int i = 0; i < N; i++) win[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (N - 1));
                int lastFft = Environment.TickCount, lastCheck = Environment.TickCount, lastData = Environment.TickCount;
                try
                {
                    while (Meter.Wanted())
                    {
                        uint packet;
                        Marshal.ThrowExceptionForHR(cap.GetNextPacketSize(out packet));
                        while (packet > 0)
                        {
                            IntPtr data; uint frames, flags; ulong dp, qp;
                            Marshal.ThrowExceptionForHR(cap.GetBuffer(out data, out frames, out flags, out dp, out qp));
                            bool silent = (flags & 2) != 0;
                            int stride = Math.Max(1, block / 4);
                            if (isFloat && !silent)
                            {
                                int count = (int)frames * stride;
                                if (fbuf.Length < count) fbuf = new float[count];
                                Marshal.Copy(data, fbuf, 0, count);
                            }
                            for (int f = 0; f < frames; f++)
                            {
                                double sum = 0;
                                if (!silent)
                                    for (int c = 0; c < Math.Min(ch, 2); c++)
                                    {
                                        double v = 0;
                                        int off = f * block + c * (bits / 8);
                                        if (isFloat) v = fbuf[f * stride + c];
                                        else if (bits == 16) v = Marshal.ReadInt16(data, off) / 32768.0;
                                        else if (bits == 32) v = Marshal.ReadInt32(data, off) / 2147483648.0;
                                        else if (bits == 24) v = ((Marshal.ReadByte(data, off) | Marshal.ReadByte(data, off + 1) << 8 | (sbyte)Marshal.ReadByte(data, off + 2) << 16)) / 8388608.0;
                                        sum += v;
                                        float av = (float)Math.Abs(v);
                                        if (c == 0) { if (av > peakL) peakL = av; if (ch == 1 && av > peakR) peakR = av; }
                                        else if (av > peakR) peakR = av;
                                    }
                                ring[ringPos] = (float)(sum / Math.Min(ch, 2));
                                ringPos = (ringPos + 1) % N;
                            }
                            cap.ReleaseBuffer(frames);
                            lastData = Environment.TickCount;
                            Marshal.ThrowExceptionForHR(cap.GetNextPacketSize(out packet));
                        }
                        int now = Environment.TickCount;
                        if (now - lastData > 200) { Array.Clear(ring, 0, N); }            // nothing playing: loopback sends no packets
                        if (now - lastFft >= 33)
                        {
                            lastFft = now;
                            Analyse(re, im, win, rate);
                            lock (gate) { L = peakL; R = peakR; PeakStamp = now; }
                            peakL = 0; peakR = 0;
                        }
                        if (WantApp != app) break;                                       // source changed: reopen
                        if (now - lastCheck > 2000)
                        {
                            lastCheck = now;
                            if (pid > 0 && ownFmt && !Procs.Alive(pid)) break;          // player closed
                            if (!ownFmt)
                            {
                                IMMDevice d2; string id2 = null;
                                if (en.GetDefaultAudioEndpoint(0, 1, out d2) == 0) d2.GetId(out id2);
                                if (id2 != null && id2 != devId) break;                  // follow a change of speakers / headset
                                if (!string.IsNullOrEmpty(WantApp) && Procs.RootPid(WantApp) > 0 && pid == 0) break; // player started
                            }
                        }
                        if (ownFmt) evt.WaitOne(20); else Thread.Sleep(8);
                    }
                }
                finally { try { client.Stop(); } catch { } }
            }
            finally { if (ownFmt) Marshal.FreeHGlobal(fmt); else Marshal.FreeCoTaskMem(fmt); evt.Close(); }
        }

        public static bool PeaksLive(out float l, out float r)
        {
            lock (gate) { l = L; r = R; return Environment.TickCount - PeakStamp < 300; }
        }

        static void Analyse(double[] re, double[] im, double[] win, int rate)
        {
            int start = ringPos;
            for (int i = 0; i < N; i++) { re[i] = ring[(start + i) % N] * win[i]; im[i] = 0; }
            Fft(re, im);
            var outp = new int[Bands];
            double fMin = 40, fMax = Math.Min(16000, rate / 2.0);
            for (int b = 0; b < Bands; b++)
            {
                double f0 = fMin * Math.Pow(fMax / fMin, (double)b / Bands), f1 = fMin * Math.Pow(fMax / fMin, (double)(b + 1) / Bands);
                int k0 = Math.Max(1, (int)(f0 * N / rate)), k1 = Math.Max(k0 + 1, (int)(f1 * N / rate));
                double mx = 0;
                for (int k = k0; k < k1 && k < N / 2; k++) mx = Math.Max(mx, Math.Sqrt(re[k] * re[k] + im[k] * im[k]));
                double db = 20 * Math.Log10(mx / (N / 4.0) + 1e-9);                   // ~0 dB for a full-scale sine
                db += 3.0 * Math.Log(f0 / fMin, 2) / 2.0;                               // gentle tilt: music has less energy up high
                outp[b] = (int)Math.Round(Math.Max(0, Math.Min(1, (db + 66) / 66)) * 100);
            }
            lock (gate) { Array.Copy(outp, bands, Bands); stamp = Environment.TickCount; }
        }

        static void Fft(double[] re, double[] im)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) { double t = re[i]; re[i] = re[j]; re[j] = t; t = im[i]; im[i] = im[j]; im[j] = t; }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = -2 * Math.PI / len, wr = Math.Cos(ang), wi = Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    double cr = 1, ci = 0;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int a = i + k, b = a + len / 2;
                        double xr = re[b] * cr - im[b] * ci, xi = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - xr; im[b] = im[a] - xi; re[a] += xr; im[a] += xi;
                        double nr = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = nr;
                    }
                }
            }
        }
    }

    // Cleans up player thumbnails: some (Spotify) put the cover on a transparent or plain
    // 300x300 canvas with their logo underneath. Keep only the cover square.
    static class ArtTools
    {
        public static byte[] Trim(byte[] src)
        {
            try
            {
                using (var ms = new MemoryStream(src))
                using (var bmp = new Bitmap(ms))
                {
                    int w = bmp.Width, h = bmp.Height;
                    if (w < 64 || h < 64 || w > 4000 || h > 4000) return src;
                    Color bg = bmp.GetPixel(0, h - 1);
                    Func<Color, bool> content = c => c.A > 24 && (bg.A < 24 || Math.Abs(c.R - bg.R) + Math.Abs(c.G - bg.G) + Math.Abs(c.B - bg.B) > 36);
                    // rows that contain content (sampled every 2 px)
                    int top = -1, bottom = -1;
                    for (int y = 0; y < h; y++)
                    {
                        bool any = false;
                        for (int x = 0; x < w && !any; x += 2) any = content(bmp.GetPixel(x, y));
                        if (any && top < 0) top = y;
                        if (!any && top >= 0) { bottom = y - 1; break; }
                    }
                    if (top < 0) return src;
                    if (bottom < 0) bottom = h - 1;
                    int left = w, right = -1;
                    for (int y = top; y <= bottom; y += 2)
                        for (int x = 0; x < w; x++)
                            if (content(bmp.GetPixel(x, y))) { if (x < left) left = x; if (x > right) right = x; }
                    int bw = right - left + 1, bh = bottom - top + 1;
                    if (right < 0 || bw < w * 0.5 || bh < h * 0.5) return src;                 // not a cover block
                    if (bw >= w - 2 && bh >= h - 2) return src;                               // nothing to trim
                    if (left < w * 0.04 || (w - 1 - right) < w * 0.04) return src;            // only framed thumbnails (side margins)
                    int side = Math.Min(bw, bh);
                    var rect = new Rectangle(left + (bw - side) / 2, top + (bh - side) / 2, side, side);
                    using (var crop = bmp.Clone(rect, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                    using (var outp = new MemoryStream())
                    {
                        crop.Save(outp, System.Drawing.Imaging.ImageFormat.Png);
                        return outp.ToArray();
                    }
                }
            }
            catch { return src; }
        }
    }

    // ------------------------------------------------------------------ per-program volume and output switch (Core Audio sessions)
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }
    // Same COM interface as IMMDeviceEnumerator above; this declaration returns the device list as an object.
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumeratorList
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice ep);
    }
    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }
    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }
    // IAudioSessionControl (first nine methods) followed by IAudioSessionControl2. Unused slots keep their place only.
    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);                    // 0 inactive, 1 active, 2 expired
        [PreserveSig] int Slot1();
        [PreserveSig] int Slot2();
        [PreserveSig] int Slot3();
        [PreserveSig] int Slot4();
        [PreserveSig] int Slot5();
        [PreserveSig] int Slot6();
        [PreserveSig] int Slot7();
        [PreserveSig] int Slot8();
        [PreserveSig] int Slot9();
        [PreserveSig] int Slot10();
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();                    // 0 = yes, 1 = no
    }
    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
    // Not a documented Windows interface: it is what the Sound control panel itself uses to change the default device.
    // Only SetDefaultEndpoint (11th method) is called; the ten before it keep their place only.
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int Slot0();
        [PreserveSig] int Slot1();
        [PreserveSig] int Slot2();
        [PreserveSig] int Slot3();
        [PreserveSig] int Slot4();
        [PreserveSig] int Slot5();
        [PreserveSig] int Slot6();
        [PreserveSig] int Slot7();
        [PreserveSig] int Slot8();
        [PreserveSig] int Slot9();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
    }
    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")] class PolicyConfigClient { }

    static class Mixer
    {
        const int MaxApps = 8;

        static void Free(object o) { try { if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); } catch { } }

        // Calls visit(processName, state, session) for every live session of the default output device.
        static void Each(Action<string, int, object> visit)
        {
            var en = (IMMDeviceEnumeratorList)new MMDeviceEnumeratorCom();
            IMMDevice dev = null; object mo = null; IAudioSessionEnumerator se = null;
            try
            {
                Marshal.ThrowExceptionForHR(en.GetDefaultAudioEndpoint(0, 1, out dev));
                Guid iid = typeof(IAudioSessionManager2).GUID;
                Marshal.ThrowExceptionForHR(dev.Activate(ref iid, 23, IntPtr.Zero, out mo));
                Marshal.ThrowExceptionForHR(((IAudioSessionManager2)mo).GetSessionEnumerator(out se));
                int n;
                Marshal.ThrowExceptionForHR(se.GetCount(out n));
                uint self;
                using (var me = System.Diagnostics.Process.GetCurrentProcess()) self = (uint)me.Id;
                for (int i = 0; i < n; i++)
                {
                    object so = null;
                    try
                    {
                        if (se.GetSession(i, out so) != 0 || so == null) continue;
                        var c = (IAudioSessionControl2)so;
                        int state; uint pid;
                        if (c.GetState(out state) != 0 || state == 2) continue;          // expired
                        if (c.IsSystemSoundsSession() == 0) continue;                    // Windows notification sounds
                        if (c.GetProcessId(out pid) != 0 || pid == 0 || pid == self) continue;   // not the bridge's own listening stream
                        string exe;
                        try { using (var p = System.Diagnostics.Process.GetProcessById((int)pid)) exe = p.ProcessName; } catch { continue; }
                        visit(exe, state, so);
                    }
                    catch { }
                    finally { Free(so); }
                }
            }
            finally { Free(se); Free(mo); Free(dev); Free(en); }
        }

        public static Dictionary<string, object> List()
        {
            var d = new Dictionary<string, object>();
            d["ok"] = false;
            try
            {
                var apps = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
                var order = new List<string>();
                Each(delegate(string exe, int state, object so)
                {
                    var v = (ISimpleAudioVolume)so;
                    float level; bool mute; float peak = 0;
                    if (v.GetMasterVolume(out level) != 0) return;
                    v.GetMute(out mute);
                    try { ((IAudioMeterInformation)so).GetPeakValue(out peak); } catch { peak = 0; }
                    Dictionary<string, object> a;
                    if (!apps.TryGetValue(exe, out a))
                    {
                        a = new Dictionary<string, object>();
                        a["app"] = exe; a["name"] = Names.Friendly(exe); a["volume"] = (int)Math.Round(level * 100);
                        a["muted"] = mute; a["peak"] = Math.Round(peak, 3); a["active"] = state == 1;
                        apps[exe] = a; order.Add(exe);
                    }
                    else                                                              // several streams of one program: one row
                    {
                        a["muted"] = (bool)a["muted"] && mute;
                        a["peak"] = Math.Max((double)a["peak"], Math.Round(peak, 3));
                        a["active"] = (bool)a["active"] || state == 1;
                    }
                });
                var list = new List<object>();
                foreach (var k in order) if ((bool)apps[k]["active"] && list.Count < MaxApps) list.Add(apps[k]);
                foreach (var k in order) if (!(bool)apps[k]["active"] && list.Count < MaxApps) list.Add(apps[k]);
                d["sessions"] = list;
                d["outputs"] = Outputs();
                d["ok"] = true;
            }
            catch (Exception e) { d["ok"] = false; d["error"] = e.Message; }
            return d;
        }

        public static Dictionary<string, object> Set(string app, int volume, string mute)
        {
            var d = new Dictionary<string, object>();
            d["ok"] = false;
            try
            {
                int hits = 0;
                Each(delegate(string exe, int state, object so)
                {
                    if (!string.Equals(exe, app, StringComparison.OrdinalIgnoreCase)) return;
                    var v = (ISimpleAudioVolume)so;
                    Guid ctx = Guid.Empty;
                    if (volume >= 0) v.SetMasterVolume(volume / 100f, ref ctx);
                    if (mute == "on" || mute == "off" || mute == "toggle")
                    {
                        bool cur; v.GetMute(out cur);
                        v.SetMute(mute == "on" ? true : mute == "off" ? false : !cur, ref ctx);
                    }
                    hits++;
                });
                d["ok"] = hits > 0;
                if (hits == 0) d["error"] = "program not found";
            }
            catch (Exception e) { d["ok"] = false; d["error"] = e.Message; }
            return d;
        }

        // Active output devices. The names come from the registry, where Windows keeps them as plain text.
        static List<object> Outputs()
        {
            var list = new List<object>();
            var en = (IMMDeviceEnumeratorList)new MMDeviceEnumeratorCom();
            IMMDevice def = null; IMMDeviceCollection col = null;
            try
            {
                string current = "";
                if (en.GetDefaultAudioEndpoint(0, 1, out def) == 0 && def != null) def.GetId(out current);
                if (en.EnumAudioEndpoints(0, 1, out col) != 0 || col == null) return list;
                uint n;
                col.GetCount(out n);
                for (uint i = 0; i < n && i < 16; i++)
                {
                    IMMDevice dev = null;
                    try
                    {
                        if (col.Item(i, out dev) != 0 || dev == null) continue;
                        string id; dev.GetId(out id);
                        var o = new Dictionary<string, object>();
                        o["id"] = id; o["name"] = DeviceName(id, (int)i + 1); o["current"] = id == current;
                        list.Add(o);
                    }
                    catch { }
                    finally { Free(dev); }
                }
            }
            finally { Free(col); Free(def); Free(en); }
            return list;
        }

        static string DeviceName(string id, int number)
        {
            try
            {
                int at = id.LastIndexOf('{');
                if (at >= 0)
                    using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                    using (var k = hklm.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\MMDevices\\Audio\\Render\\" + id.Substring(at) + "\\Properties"))
                    {
                        if (k != null)
                        {
                            string desc = k.GetValue("{a45c254e-df1c-4efd-8020-67d146a850e0},2") as string;
                            string card = k.GetValue("{b3f8fa53-0004-438e-9003-51a46e139bfc},6") as string;
                            if (!string.IsNullOrEmpty(desc)) return string.IsNullOrEmpty(card) ? desc : desc + " (" + card + ")";
                        }
                    }
            }
            catch { }
            return "Output " + number.ToString(CultureInfo.InvariantCulture);
        }

        public static Dictionary<string, object> SetOutput(string id)
        {
            var d = new Dictionary<string, object>();
            d["ok"] = false;
            try
            {
                bool known = false;
                foreach (Dictionary<string, object> o in Outputs()) if ((string)o["id"] == id) known = true;
                if (!known) { d["error"] = "unknown output"; return d; }
                var pc = (IPolicyConfig)new PolicyConfigClient();
                try
                {
                    Marshal.ThrowExceptionForHR(pc.SetDefaultEndpoint(id, 0));     // console
                    Marshal.ThrowExceptionForHR(pc.SetDefaultEndpoint(id, 1));     // multimedia
                }
                finally { Free(pc); }
                d["ok"] = true; d["outputs"] = Outputs();
            }
            catch (Exception e) { d["ok"] = false; d["error"] = e.Message; }
            return d;
        }
    }

    // ------------------------------------------------------------------ AIMP playlist (read-only)
    // AIMP 4/5 keep every playlist as <profile>\PLS\<name>.aimppl4: UTF-16 text, one track per line under
    // "#-----CONTENT-----#", fields separated by '|' (0 path, 1 title, 2 artist, ... 14 length in ms); lines
    // starting with '-' are group headers. AIMP.ini [Playlist.Manager] PlayingStorage names the playlist that
    // is playing. The files are only read, never written. AIMP 2/3 use another format: they get ok=false.
    static class AimpPlaylist
    {
        const int MaxTracks = 3000;
        static readonly object gate = new object();
        static string cachePath = "", cacheStamp = "", cacheName = "";
        static List<string> cacheFiles = new List<string>();
        static List<object> cacheTracks = new List<object>();
        static int lastScan;
        static bool scanned;

        public static Dictionary<string, object> Read(string currentFile, string since)
        {
            var d = new Dictionary<string, object>();
            d["ok"] = false;
            try
            {
                lock (gate)
                {
                    bool have = cachePath != "" && File.Exists(cachePath);
                    bool fresh = have && Stamp(cachePath, cacheTracks.Count) == cacheStamp && (currentFile == "" || IndexOf(currentFile) >= 0);
                    if (!fresh && (!scanned || unchecked(Environment.TickCount - lastScan) >= 4000))
                    {
                        scanned = true; lastScan = Environment.TickCount;
                        string path = Pick(currentFile);
                        if (path != null) Load(path); else if (!have) cachePath = "";
                    }
                    if (cachePath == "") { d["error"] = "no AIMP playlist found"; return d; }
                    d["ok"] = true; d["app"] = Aimp.Id; d["name"] = cacheName; d["stamp"] = cacheStamp;
                    d["count"] = cacheTracks.Count; d["index"] = currentFile == "" ? -1 : IndexOf(currentFile);
                    if (since != "" && since == cacheStamp) d["unchanged"] = true; else d["tracks"] = cacheTracks;
                }
            }
            catch (Exception e) { d["ok"] = false; d["error"] = e.Message; }
            return d;
        }

        // AIMP's remote interface has no "play track n". The bridge walks there with Next / Previous (muted when
        // it is more than one step, shuffle off for the walk) and checks after every step that AIMP really is on
        // the expected file. It stops at the first surprise and always restores mute and shuffle.
        const int MaxSteps = 80;
        public static Dictionary<string, object> Jump(int target)
        {
            var d = new Dictionary<string, object>();
            d["ok"] = false;
            try
            {
                string now = Aimp.CurrentFile();
                if (now == "")                                  // stopped: start playback so AIMP has a current track
                {
                    Aimp.Command("play");
                    for (int w = 0; w < 100 && now == ""; w++) { Thread.Sleep(20); now = Aimp.CurrentFile(); }
                }
                string[] files;
                lock (gate)
                {
                    Read(now, "");                              // makes sure the cached playlist is the one that is playing
                    files = cacheFiles.ToArray();
                }
                if (target < 0 || target >= files.Length) { d["error"] = "no such track"; return d; }
                int cur = Find(files, now);
                if (cur < 0) { d["error"] = "playing track is not in the playlist"; return d; }
                int steps = Math.Abs(target - cur);
                if (steps > MaxSteps) { d["error"] = "too far"; return d; }
                bool wasPlaying = Aimp.State() == 2;
                bool failed = false;
                if (steps > 0)
                {
                    bool wasMuted = Aimp.Muted(), wasShuffle = Aimp.Shuffle(), quiet = steps > 1 && !wasMuted;
                    try
                    {
                        if (quiet) Aimp.SetMute(true);
                        if (wasShuffle) { Aimp.SetFlag("shuffle", false); Thread.Sleep(60); }
                        int dir = target > cur ? 1 : -1;
                        string cmd = dir > 0 ? "next" : "prev";
                        for (int i = cur; i != target && !failed; i += dir)
                        {
                            string want = files[i + dir];
                            bool same = string.Equals(want, files[i], StringComparison.OrdinalIgnoreCase);
                            Aimp.Command(cmd);
                            if (same) { Thread.Sleep(250); continue; }       // the same file twice in a row: nothing to compare
                            if (WaitFor(want, 2000)) continue;
                            if (dir < 0 && string.Equals(Aimp.CurrentFile(), files[i], StringComparison.OrdinalIgnoreCase))
                            {
                                Aimp.Command(cmd);                           // first "previous" only restarted the track
                                if (WaitFor(want, 2000)) continue;
                            }
                            failed = true;
                        }
                    }
                    finally
                    {
                        if (wasShuffle) Aimp.SetFlag("shuffle", true);
                        if (quiet) Aimp.SetMute(false);
                    }
                }
                d["index"] = Find(files, Aimp.CurrentFile());
                if (failed) { d["error"] = "could not reach the track"; return d; }
                if (!wasPlaying || Aimp.State() != 2) Aimp.Command("play");
                d["ok"] = true;
            }
            catch (Exception e) { d["ok"] = false; d["error"] = e.Message; }
            return d;
        }

        static int Find(string[] files, string file)
        {
            for (int i = 0; i < files.Length; i++)
                if (string.Equals(files[i], file, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        static bool WaitFor(string file, int ms)
        {
            int start = Environment.TickCount;
            while (unchecked(Environment.TickCount - start) < ms)
            {
                if (string.Equals(Aimp.CurrentFile(), file, StringComparison.OrdinalIgnoreCase)) return true;
                Thread.Sleep(15);
            }
            return false;
        }

        static string Stamp(string path, int count)
        {
            return File.GetLastWriteTimeUtc(path).Ticks.ToString(CultureInfo.InvariantCulture) + "-" + count.ToString(CultureInfo.InvariantCulture);
        }

        static int IndexOf(string file)
        {
            for (int i = 0; i < cacheFiles.Count; i++)
                if (string.Equals(cacheFiles[i], file, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        // Profile folders to look in: next to a running portable AIMP first, then the normal one in AppData.
        static List<string> Profiles()
        {
            var list = new List<string>();
            try
            {
                foreach (var p in System.Diagnostics.Process.GetProcessesByName("AIMP"))
                {
                    try { list.Add(Path.Combine(Path.GetDirectoryName(p.MainModule.FileName), "Profile")); } catch { }
                }
            }
            catch { }
            list.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIMP"));
            return list;
        }

        // AIMP may be writing the file: open it shared and read it whole (BOM decides UTF-16 / UTF-8).
        static string ReadText(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, Encoding.Unicode, true))
                return sr.ReadToEnd();
        }

        static string IniValue(string text, string section, string key)
        {
            bool inside = false;
            foreach (var raw in text.Split(new[] { '\n' }))
            {
                string line = raw.Trim();
                if (line.StartsWith("[", StringComparison.Ordinal)) { inside = string.Equals(line, "[" + section + "]", StringComparison.OrdinalIgnoreCase); continue; }
                if (inside && line.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) return line.Substring(key.Length + 1).Trim();
            }
            return "";
        }

        // The playlist that holds the playing file wins, then the one AIMP.ini calls "playing", then the newest.
        static string Pick(string currentFile)
        {
            string best = null; int bestScore = -1; DateTime bestTime = DateTime.MinValue;
            foreach (var profile in Profiles())
            {
                string pls = Path.Combine(profile, "PLS");
                if (!Directory.Exists(pls)) continue;
                string playing = "";
                try { playing = IniValue(ReadText(Path.Combine(profile, "AIMP.ini")), "Playlist.Manager", "PlayingStorage"); } catch { }
                foreach (var f in Directory.GetFiles(pls, "*.aimppl4"))
                {
                    try
                    {
                        string text = ReadText(f);
                        int score = 0;
                        if (currentFile != "" && text.IndexOf("\n" + currentFile + "|", StringComparison.OrdinalIgnoreCase) >= 0) score += 2;
                        if (playing != "" && text.IndexOf("ID=" + playing, StringComparison.OrdinalIgnoreCase) >= 0) score += 1;
                        DateTime t = File.GetLastWriteTimeUtc(f);
                        if (score > bestScore || (score == bestScore && t > bestTime)) { best = f; bestScore = score; bestTime = t; }
                    }
                    catch { }
                }
                if (best != null) break;
            }
            return best;
        }

        static void Load(string path)
        {
            var files = new List<string>();
            var tracks = new List<object>();
            string name = "";
            bool content = false;
            foreach (var raw in ReadText(path).Split(new[] { '\n' }))
            {
                string line = raw.TrimEnd(new[] { '\r' });
                if (line.Length == 0) continue;
                if (line[0] == '#') { content = line.StartsWith("#-----CONTENT", StringComparison.Ordinal); continue; }
                if (!content) { if (line.StartsWith("Name=", StringComparison.Ordinal)) name = line.Substring(5); continue; }
                if (line[0] == '-') continue;                          // group header
                if (tracks.Count >= MaxTracks) break;
                string[] f = line.Split(new[] { '|' });
                string file = f[0], title = f.Length > 1 ? f[1] : "", artist = f.Length > 2 ? f[2] : "";
                double ms = 0;
                if (f.Length > 14) double.TryParse(f[14], NumberStyles.Float, CultureInfo.InvariantCulture, out ms);
                if (title == "")
                {
                    try { title = Path.GetFileNameWithoutExtension(file); } catch { title = ""; }
                    if (string.IsNullOrEmpty(title)) title = file;
                }
                var t = new Dictionary<string, object>();
                t["title"] = title; t["artist"] = artist; t["duration"] = Math.Round(Math.Max(0, ms) / 1000.0, 1);
                files.Add(file); tracks.Add(t);
            }
            cachePath = path; cacheName = name; cacheFiles = files; cacheTracks = tracks;
            cacheStamp = Stamp(path, tracks.Count);
        }
    }

    // Finds the main process of a player from its media id / name.
    static class Procs
    {
        static readonly string[][] Map = {
            new[]{"spotify","Spotify"}, new[]{"chrome","chrome"}, new[]{"msedge","msedge"}, new[]{"308046b0af4a39cb","firefox"},
            new[]{"firefox","firefox"}, new[]{"opera","opera"}, new[]{"brave","brave"}, new[]{"vivaldi","vivaldi"},
            new[]{"aimp","AIMP","AIMP3","AIMP2"}, new[]{"vlc","vlc"}, new[]{"foobar2000","foobar2000"}, new[]{"applemusic","AppleMusic"},
            new[]{"itunes","iTunes"}, new[]{"zunemusic","Microsoft.Media.Player","Music.UI"}, new[]{"tidal","TIDAL"}, new[]{"deezer","Deezer"},
            new[]{"discord","Discord"}, new[]{"musicbee","MusicBee"}, new[]{"winamp","winamp"}, new[]{"potplayer","PotPlayerMini64","PotPlayerMini"},
            new[]{"mpc-hc","mpc-hc64","mpc-hc"}, new[]{"amazonmusic","Amazon Music"},
        };

        public static int RootPid(string app)
        {
            string low = app.ToLowerInvariant();
            var names = new List<string>();
            foreach (var m in Map) if (low.Contains(m[0])) names.AddRange(m.Skip(1));
            if (names.Count == 0)
            {
                string n = app;
                if (n.Contains("!")) n = n.Split(new[] { '!' }).Last();
                if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 4);
                names.Add(n);
            }
            System.Diagnostics.Process best = null;
            foreach (var name in names)
                foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
                {
                    try { if (best == null || p.StartTime < best.StartTime) best = p; }   // the oldest one is the main process
                    catch { }
                }
            return best != null ? best.Id : 0;
        }

        public static bool Alive(int pid)
        {
            try { var p = System.Diagnostics.Process.GetProcessById(pid); return !p.HasExited; } catch { return false; }
        }
    }

    // Per-app capture (Windows 10 2004+): ActivateAudioInterfaceAsync on the process-loopback device.
    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceAsyncOperation
    {
        int GetActivateResult(out int hr, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }
    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation op);
    }
    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAgileObject { }

    static class ProcessLoopback
    {
        [DllImport("Mmdevapi.dll", ExactSpelling = true)]
        static extern int ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid riid, IntPtr activationParams,
            IActivateAudioInterfaceCompletionHandler handler, out IActivateAudioInterfaceAsyncOperation op);

        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        class Done : IActivateAudioInterfaceCompletionHandler, IAgileObject
        {
            public readonly ManualResetEvent Ev = new ManualResetEvent(false);
            public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation op) { Ev.Set(); return 0; }
        }

        public static IAudioClient Activate(int pid)
        {
            // AUDIOCLIENT_ACTIVATION_PARAMS { PROCESS_LOOPBACK, { pid, INCLUDE_TARGET_PROCESS_TREE } }
            IntPtr prm = Marshal.AllocHGlobal(12);
            IntPtr pv = Marshal.AllocHGlobal(24);
            try
            {
                Marshal.WriteInt32(prm, 0, 1); Marshal.WriteInt32(prm, 4, pid); Marshal.WriteInt32(prm, 8, 0);
                for (int i = 0; i < 24; i++) Marshal.WriteByte(pv, i, 0);
                Marshal.WriteInt16(pv, 0, 65);                                   // VT_BLOB
                Marshal.WriteInt32(pv, 8, 12);                                   // blob size
                Marshal.WriteIntPtr(pv, IntPtr.Size == 8 ? 16 : 12, prm);        // blob data
                Guid iid = typeof(IAudioClient).GUID;
                var done = new Done();
                IActivateAudioInterfaceAsyncOperation op;
                Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, pv, done, out op));
                if (!done.Ev.WaitOne(3000)) throw new TimeoutException("activation timed out");
                int hr; object o;
                Marshal.ThrowExceptionForHR(op.GetActivateResult(out hr, out o));
                Marshal.ThrowExceptionForHR(hr);
                return (IAudioClient)o;
            }
            finally { Marshal.FreeHGlobal(prm); Marshal.FreeHGlobal(pv); }
        }

        // 48 kHz, stereo, 32-bit float
        public static IntPtr FloatFormat()
        {
            IntPtr f = Marshal.AllocHGlobal(18);
            Marshal.WriteInt16(f, 0, 3); Marshal.WriteInt16(f, 2, 2); Marshal.WriteInt32(f, 4, 48000);
            Marshal.WriteInt32(f, 8, 48000 * 8); Marshal.WriteInt16(f, 12, 8); Marshal.WriteInt16(f, 14, 32); Marshal.WriteInt16(f, 16, 0);
            return f;
        }
    }

    static class Volume
    {
        static IAudioEndpointVolume Ep()
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            IMMDevice dev;
            Marshal.ThrowExceptionForHR(en.GetDefaultAudioEndpoint(0, 1, out dev)); // render, multimedia
            Guid iid = typeof(IAudioEndpointVolume).GUID;
            object o;
            Marshal.ThrowExceptionForHR(dev.Activate(ref iid, 23, IntPtr.Zero, out o));
            return (IAudioEndpointVolume)o;
        }

        public static void Set(int pct) { Guid g = Guid.Empty; Ep().SetMasterVolumeLevelScalar(Math.Max(0, Math.Min(100, pct)) / 100f, ref g); }
        public static void Mute(bool m) { Guid g = Guid.Empty; Ep().SetMute(m, ref g); }

        public static Dictionary<string, object> Info(string app)
        {
            var d = new Dictionary<string, object>();
            if (app == Aimp.Id && Aimp.Running())
            {
                d["target"] = "aimp"; d["level"] = Aimp.Volume(); d["muted"] = Aimp.Muted();
                return d;
            }
            try
            {
                var ep = Ep();
                float v; bool m;
                ep.GetMasterVolumeLevelScalar(out v); ep.GetMute(out m);
                d["target"] = "system"; d["level"] = (int)Math.Round(v * 100); d["muted"] = m;
            }
            catch { d["target"] = "none"; d["level"] = -1; d["muted"] = false; }
            return d;
        }
    }

    // ------------------------------------------------------------------ players with their own local web interface
    // VLC (its built-in "Web" interface) and foobar2000 (the Beefweb component) are asked on 127.0.0.1 only.
    // A background thread reads their state twice a second while a widget is polling /sessions, so the http
    // thread never waits for a player. Commands go out directly with a short timeout.
    // Nothing here runs unless the player was set up for it: VLC needs a web password, foobar2000 needs Beefweb.

    static class Web
    {
        public class Reply
        {
            public int Code;                 // 0 = no answer
            public byte[] Body;
            public string Text { get { try { return Body == null ? "" : Encoding.UTF8.GetString(Body); } catch { return ""; } } }
            public bool Ok { get { return Code >= 200 && Code < 300; } }
        }

        public static string Basic(string user, string password)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes((user ?? "") + ":" + (password ?? "")));
        }

        // method GET or POST; auth = Basic value or ""; json = POST body or null. Never throws.
        public static Reply Call(string method, string url, string auth, string json, int ms)
        {
            var r = new Reply();
            try
            {
                var rq = (HttpWebRequest)WebRequest.Create(url);
                rq.Method = method; rq.Proxy = null; rq.Timeout = ms; rq.ReadWriteTimeout = ms;
                rq.AllowAutoRedirect = false; rq.KeepAlive = true;
                rq.UserAgent = "NocturneBridge/" + Program.Version;
                try { rq.ServicePoint.Expect100Continue = false; } catch { }
                if (!string.IsNullOrEmpty(auth)) rq.Headers["Authorization"] = "Basic " + auth;
                if (method == "POST")
                {
                    byte[] body = Encoding.UTF8.GetBytes(json ?? "");
                    rq.ContentLength = body.Length;
                    if (body.Length > 0)
                    {
                        rq.ContentType = "application/json";
                        using (var s = rq.GetRequestStream()) s.Write(body, 0, body.Length);
                    }
                }
                using (var rs = (HttpWebResponse)rq.GetResponse()) Read(rs, r);
            }
            catch (WebException e)
            {
                var rs = e.Response as HttpWebResponse;
                if (rs != null)
                {
                    try { Read(rs, r); } catch { }
                    try { rs.Close(); } catch { }
                }
            }
            catch { }
            return r;
        }

        static void Read(HttpWebResponse rs, Reply r)
        {
            r.Code = (int)rs.StatusCode;
            using (var s = rs.GetResponseStream())
            using (var ms = new MemoryStream())
            {
                if (s != null)
                {
                    var buf = new byte[16384];
                    int n;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0)
                    {
                        ms.Write(buf, 0, n);
                        if (ms.Length > 24 * 1024 * 1024) break;     // nothing a player sends is this large
                    }
                }
                r.Body = ms.ToArray();
            }
        }
    }

    // Optional NocturneBridge.ini next to the exe. Only needed when a player does not use its defaults:
    //   [vlc]          port=8080   password=...   enabled=off
    //   [foobar2000]   port=8880   user=...       password=...   enabled=off
    static class Ini
    {
        static readonly object gate = new object();
        static Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static DateTime stamp = DateTime.MinValue;
        static int checkedAt;
        static bool looked;

        public static string Get(string section, string key)
        {
            lock (gate)
            {
                if (!looked || unchecked(Environment.TickCount - checkedAt) >= 3000)
                {
                    looked = true; checkedAt = Environment.TickCount;
                    try { Load(); } catch { }
                }
                string v;
                return values.TryGetValue(section + "." + key, out v) ? v : "";
            }
        }

        public static bool Off(string section)
        {
            string v = Get(section, "enabled").ToLowerInvariant();
            return v == "off" || v == "0" || v == "false" || v == "no";
        }

        static void Load()
        {
            string path = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath) ?? "", "NocturneBridge.ini");
            if (!File.Exists(path))
            {
                if (values.Count > 0) values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                stamp = DateTime.MinValue;
                return;
            }
            DateTime t = File.GetLastWriteTimeUtc(path);
            if (t == stamp) return;
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string section = "";
            foreach (var raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                if (line[0] == '[' && line[line.Length - 1] == ']') { section = line.Substring(1, line.Length - 2).Trim(); continue; }
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                d[section + "." + line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            values = d; stamp = t;
        }
    }

    static class Remote
    {
        static readonly object gate = new object();
        static Thread thread;
        static int touchedAt;
        static bool touched;

        // a widget asked for /sessions within the last 10 s
        static bool Wanted { get { return touched && unchecked(Environment.TickCount - touchedAt) < 10000; } }

        public static void Touch()
        {
            touchedAt = Environment.TickCount; touched = true;
            if (thread != null) return;
            lock (gate)
            {
                if (thread != null) return;
                thread = new Thread(Loop) { IsBackground = true, Name = "players" };
                thread.Start();
            }
        }

        static void Loop()
        {
            while (true)
            {
                if (Wanted)
                {
                    try { Vlc.Tick(); } catch { }
                    try { Foobar.Tick(); } catch { }
                }
                Thread.Sleep(500);
            }
        }

        public static long Hash(long h, string s)
        {
            if (s == null) s = "";
            unchecked
            {
                foreach (char c in s) h = (h ^ c) * 1099511628211L;
                return (h ^ 0x1F) * 1099511628211L;
            }
        }
        public const long HashSeed = 1469598103934665603L;
    }

    // ------------------------------------------------------------------ VLC (desktop) through its Web interface
    // VLC 3 does not report to Windows, so without this it is not a source at all. Setup in VLC:
    //   Tools > Preferences > Show settings: All > Interface > Main interfaces: tick "Web"
    //   Interface > Main interfaces > Lua > Lua HTTP > Password: any password. Save, restart VLC.
    // The bridge reads port and password from VLC's own settings file (%APPDATA%\vlc\vlcrc) or from NocturneBridge.ini.
    static class Vlc
    {
        public const string Id = "vlc-http";
        const int MaxTracks = 3000;
        static readonly object gate = new object();

        // connection
        static string baseUrl = "", auth = "";
        static bool cfgOk, cfgLooked;
        static int cfgAt;
        static string rcPath = "", rcPass = "", rcPort = "";
        static DateTime rcStamp = DateTime.MinValue;

        // state
        static Dictionary<string, object> snap;        // the session as /sessions shows it
        static int snapAt;
        static double snapRate = 1;
        static string curId = "";                      // playlist id of the playing item
        static int failures, failedAt;
        static string note = "not set up";

        // art
        static string artKey = "";
        static int artSeq;

        // playlist
        static List<string> listIds = new List<string>();
        static List<object> listTracks = new List<object>();
        static string listStamp = "", listName = "";
        static bool listHave;
        static int listAt, listWantAt;
        static bool listWant;

        static bool Configure()
        {
            if (cfgLooked && unchecked(Environment.TickCount - cfgAt) < 3000) return cfgOk;
            cfgLooked = true; cfgAt = Environment.TickCount;
            cfgOk = false;
            if (Ini.Off("vlc")) { note = "switched off in NocturneBridge.ini"; return false; }
            string pass = Ini.Get("vlc", "password"), port = Ini.Get("vlc", "port");
            if (pass == "" || port == "")
            {
                try { ReadRc(); } catch { }
                if (pass == "") pass = rcPass;
                if (port == "") port = rcPort;
            }
            if (pass == "") { note = "not set up (no web password in VLC)"; return false; }
            int p;
            if (!int.TryParse(port, NumberStyles.Integer, CultureInfo.InvariantCulture, out p) || p < 1 || p > 65535) p = 8080;
            baseUrl = "http://127.0.0.1:" + p.ToString(CultureInfo.InvariantCulture);
            auth = Web.Basic("", pass);
            cfgOk = true;
            return true;
        }

        // VLC writes "http-password=..." (and "http-port=...") without the leading # once they are set.
        static void ReadRc()
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "vlc", "vlcrc");
            if (!File.Exists(path)) { rcPath = ""; rcPass = ""; rcPort = ""; rcStamp = DateTime.MinValue; return; }
            DateTime t = File.GetLastWriteTimeUtc(path);
            if (path == rcPath && t == rcStamp) return;
            string pass = "", port = "";
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, Encoding.UTF8, true))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (line.StartsWith("http-password=", StringComparison.Ordinal)) pass = line.Substring(14).Trim();
                    else if (line.StartsWith("http-port=", StringComparison.Ordinal)) port = line.Substring(10).Trim();
                }
            }
            rcPath = path; rcStamp = t; rcPass = pass; rcPort = port;
        }

        static void Drop()
        {
            lock (gate) { snap = null; curId = ""; listHave = false; }
        }

        // the session for /sessions, or null when VLC is not reachable
        public static Dictionary<string, object> Info()
        {
            lock (gate)
            {
                if (snap == null || unchecked(Environment.TickCount - snapAt) > 3000) return null;
                var d = new Dictionary<string, object>(snap);
                if ((string)d["status"] == "playing")
                {
                    double pos = (double)d["position"] + unchecked(Environment.TickCount - snapAt) / 1000.0 * snapRate;
                    double dur = (double)d["duration"];
                    if (dur > 0) pos = Math.Min(pos, dur);
                    d["position"] = Math.Round(Math.Max(0, pos), 1);
                }
                return d;
            }
        }

        public static bool Live() { lock (gate) return snap != null && unchecked(Environment.TickCount - snapAt) <= 3000; }

        // A desktop VLC that also reports to Windows would show twice. The Store app is a different program and stays.
        public static bool Same(string mediaId)
        {
            string low = (mediaId ?? "").ToLowerInvariant();
            return low.Contains("vlc") && !low.StartsWith("videolan.vlc_", StringComparison.Ordinal);
        }

        public static Dictionary<string, object> Status()
        {
            var d = new Dictionary<string, object>();
            bool ok = false;
            try { ok = Configure(); } catch { }
            d["setUp"] = ok;
            d["address"] = ok ? baseUrl : "";
            d["state"] = Live() ? "connected" : note;
            return d;
        }

        public static void Tick()
        {
            if (!Configure()) { Drop(); return; }
            int now = Environment.TickCount;
            if (failures > 0 && unchecked(now - failedAt) < (failures > 3 ? 5000 : 1200)) return;
            var j = Ask("");
            if (j == null) return;
            try { Art(j); } catch { }
            bool want;
            lock (gate) want = listWant && unchecked(Environment.TickCount - listWantAt) < 12000
                               && (!listHave || unchecked(Environment.TickCount - listAt) >= 2000 || (curId != "" && !listIds.Contains(curId)));
            if (want) { try { LoadList(); } catch { } }
        }

        // One call to status.json, with or without a command. Updates the session. null = no usable answer.
        static Dictionary<string, object> Ask(string command)
        {
            var r = Web.Call("GET", baseUrl + "/requests/status.json" + (command == "" ? "" : "?command=" + command), auth, null, 1500);
            var j = r.Code == 200 ? Json.Parse(r.Text) as Dictionary<string, object> : null;
            if (j == null || !j.ContainsKey("state"))
            {
                failures++; failedAt = Environment.TickCount;
                note = r.Code == 401 ? "wrong password (VLC answered 401)" : r.Code == 0 ? "no answer (VLC closed, or its Web interface is off)" : "unexpected answer (" + r.Code + ")";
                if (failures >= 2) Drop();
                return null;
            }
            failures = 0; note = "connected";
            Build(j);
            return j;
        }

        static void Build(Dictionary<string, object> j)
        {
            var meta = Json.At(j, "information", "category", "meta") as Dictionary<string, object>;
            string title = Json.Text(meta, "title"), artist = Json.Text(meta, "artist"), album = Json.Text(meta, "album");
            string file = Json.Text(meta, "filename"), radio = Json.Text(meta, "now_playing");
            if (title == "") title = NoExt(file);
            if (radio != "") { if (album == "") album = title; title = radio; }

            string state = Json.Text(j, "state").ToLowerInvariant();
            if (state != "playing" && state != "paused") state = "stopped";
            double length = Math.Max(0, Json.Number(j, "length", 0)), frac = Json.Number(j, "position", -1), time = Math.Max(0, Json.Number(j, "time", 0));
            double pos = length > 0 && frac >= 0 && frac <= 1 ? frac * length : time;
            if (state == "stopped") { pos = 0; }
            double rate = Json.Number(j, "rate", 1);
            if (rate <= 0 || rate > 8) rate = 1;

            // audio details: VLC translates the labels, so look at the values ("44100 Hz", "320 kb/s", "Stereo")
            int hz = 0, kbps = 0, ch = 0;
            var cats = Json.At(j, "information", "category") as Dictionary<string, object>;
            if (cats != null)
            {
                foreach (var kv in cats)
                {
                    if (kv.Key == "meta") continue;
                    var c = kv.Value as Dictionary<string, object>;
                    if (c == null) continue;
                    int h = 0, k = 0, n = 0;
                    foreach (var f in c)
                    {
                        string v = f.Value as string;
                        if (v == null) continue;
                        int num = Lead(v);
                        if (num > 0 && v.EndsWith(" Hz", StringComparison.OrdinalIgnoreCase)) h = num;
                        else if (num > 0 && v.EndsWith(" kb/s", StringComparison.OrdinalIgnoreCase)) k = num;
                        else if (string.Equals(v, "Stereo", StringComparison.OrdinalIgnoreCase)) n = 2;
                        else if (string.Equals(v, "Mono", StringComparison.OrdinalIgnoreCase)) n = 1;
                    }
                    if (h > 0 || k > 0) { hz = h; kbps = k; ch = n; break; }      // the first stream with a sample rate or bit rate is the audio
                }
            }
            string ext = "";
            try { ext = Path.GetExtension(file).TrimStart(new[] { '.' }).ToUpperInvariant(); } catch { }
            if (ext.Length == 0 || ext.Length > 5) ext = "";

            var d = new Dictionary<string, object>();
            d["id"] = Id; d["app"] = "VLC"; d["current"] = false;
            d["title"] = title; d["artist"] = artist; d["album"] = album;
            d["status"] = state;
            d["position"] = Math.Round(pos, 1); d["duration"] = Math.Round(length, 1);
            d["bitrate"] = kbps; d["sampleRate"] = hz; d["channels"] = ch;
            d["format"] = ext;
            d["repeat"] = Json.Flag(j, "loop") || Json.Flag(j, "repeat");
            d["shuffle"] = Json.Flag(j, "random");
            d["playlist"] = true;
            string plid = Json.Text(j, "currentplid");
            lock (gate)
            {
                object art;
                d["art"] = snap != null && snap.TryGetValue("art", out art) ? art : "";
                snap = d; snapAt = Environment.TickCount; snapRate = rate;
                curId = plid == "-1" ? "" : plid;
            }
        }

        static int Lead(string v)
        {
            int n = 0, i = 0;
            while (i < v.Length && v[i] >= '0' && v[i] <= '9' && i < 9) { n = n * 10 + (v[i] - '0'); i++; }
            return i == 0 ? 0 : n;
        }

        static readonly string[] MediaExt = { ".mp3", ".flac", ".wav", ".ogg", ".oga", ".opus", ".m4a", ".aac", ".wma", ".ape", ".wv", ".mpc",
            ".aiff", ".aif", ".alac", ".dsf", ".dff", ".mka", ".mp4", ".mkv", ".avi", ".mov", ".webm", ".m4v", ".wmv", ".mpg", ".mpeg", ".ts", ".flv" };
        static string NoExt(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            foreach (var e in MediaExt)
                if (name.Length > e.Length && name.EndsWith(e, StringComparison.OrdinalIgnoreCase)) return name.Substring(0, name.Length - e.Length);
            return name;
        }

        // Cover of the playing item: VLC serves it on /art once "artwork_url" is known.
        static void Art(Dictionary<string, object> j)
        {
            string url = Json.Text(Json.At(j, "information", "category", "meta"), "artwork_url");
            string key = Json.Text(j, "currentplid") + "|" + url;
            if (key == artKey) return;
            byte[] img = null;
            if (url != "")
            {
                var r = Web.Call("GET", baseUrl + "/art", auth, null, 2000);
                if (r.Code == 200 && r.Body != null && r.Body.Length > 64 && Image(r.Body)) img = r.Body;
                else if (r.Code == 0) return;                             // try again on the next tick
            }
            artKey = key;
            if (img != null) { try { img = ArtTools.Trim(img); } catch { } }
            var a = new Media.Art { Key = key, Id = Interlocked.Increment(ref artSeq) + 200000, Bytes = img, Type = img != null ? Media.Sniff(img) : null };
            lock (Media.ArtCache) Media.ArtCache[Id] = a;
            lock (gate) if (snap != null) snap["art"] = img != null ? a.Id.ToString() : "";
        }

        static bool Image(byte[] b)
        {
            return (b[0] == 0xFF && b[1] == 0xD8) || (b[0] == 0x89 && b[1] == 0x50) || (b[0] == 0x47 && b[1] == 0x49)
                || (b[0] == 0x42 && b[1] == 0x4D) || (b.Length > 12 && b[8] == 0x57 && b[9] == 0x45);
        }

        public static bool Command(string c)
        {
            if (!Configure()) return false;
            string status = "";
            lock (gate) if (snap != null) status = (string)snap["status"];
            string cmd = c == "toggle" ? "pl_pause"
                       : c == "play" ? (status == "paused" ? "pl_forceresume" : status == "playing" ? "" : "pl_play")
                       : c == "pause" ? "pl_forcepause" : c == "stop" ? "pl_stop" : c == "next" ? "pl_next" : c == "prev" ? "pl_previous" : null;
            if (cmd == null) return false;
            if (cmd == "") return true;                                    // already playing
            return Ask(cmd) != null;
        }

        public static bool Seek(double sec)
        {
            if (!Configure() || sec < 0) return false;
            return Ask("seek&val=" + ((long)Math.Round(sec)).ToString(CultureInfo.InvariantCulture)) != null;
        }

        // what = repeat | shuffle, set = on | off | "" (flip). VLC only has toggles, so the state is read first.
        // "Repeat" here is VLC's "loop" (repeat the whole list); switching it off also clears "repeat one".
        public static Dictionary<string, object> Flag(string what, string set)
        {
            var d = new Dictionary<string, object>();
            d["ok"] = false;
            if ((what != "repeat" && what != "shuffle") || !Configure()) return d;
            var j = Ask("");
            if (j == null) return d;
            bool random = Json.Flag(j, "random"), loop = Json.Flag(j, "loop"), one = Json.Flag(j, "repeat");
            bool cur = what == "shuffle" ? random : (loop || one);
            bool want = set == "on" ? true : set == "off" ? false : !cur;
            if (want != cur)
            {
                if (what == "shuffle") j = Ask("pl_random");
                else if (want) j = Ask("pl_loop");
                else
                {
                    if (loop) j = Ask("pl_loop");
                    if (one && j != null) j = Ask("pl_repeat");
                }
                if (j == null) return d;
                Thread.Sleep(40);
                j = Ask("") ?? j;
            }
            d["ok"] = true;
            d["on"] = what == "shuffle" ? Json.Flag(j, "random") : (Json.Flag(j, "loop") || Json.Flag(j, "repeat"));
            return d;
        }

        // The play queue: first branch of VLC's playlist tree (the second one is the media library).
        static bool LoadList()
        {
            var r = Web.Call("GET", baseUrl + "/requests/playlist.json", auth, null, 2500);
            var root = r.Code == 200 ? Json.Parse(r.Text) as Dictionary<string, object> : null;
            if (root == null) return false;
            var ids = new List<string>();
            var tracks = new List<object>();
            string name = "";
            var top = root.ContainsKey("children") ? root["children"] as List<object> : null;
            Dictionary<string, object> queue = null;
            if (top != null)
                foreach (var o in top)
                {
                    var n = o as Dictionary<string, object>;
                    if (n != null && Json.Text(n, "type") == "node") { queue = n; break; }
                }
            if (queue == null) queue = root;
            name = Json.Text(queue, "name");
            Walk(queue, ids, tracks, 0);
            long h = Remote.HashSeed;
            for (int i = 0; i < ids.Count; i++)
            {
                var t = (Dictionary<string, object>)tracks[i];
                h = Remote.Hash(h, ids[i]); h = Remote.Hash(h, (string)t["title"]);
                h = Remote.Hash(h, ((double)t["duration"]).ToString("0", CultureInfo.InvariantCulture));
            }
            lock (gate)
            {
                listIds = ids; listTracks = tracks; listName = name;
                listStamp = "vlc-" + ids.Count.ToString(CultureInfo.InvariantCulture) + "-" + h.ToString("x", CultureInfo.InvariantCulture);
                listHave = true; listAt = Environment.TickCount;
            }
            return true;
        }

        static void Walk(Dictionary<string, object> node, List<string> ids, List<object> tracks, int depth)
        {
            var kids = node.ContainsKey("children") ? node["children"] as List<object> : null;
            if (kids == null || depth > 12) return;
            foreach (var o in kids)
            {
                var n = o as Dictionary<string, object>;
                if (n == null) continue;
                if (Json.Text(n, "type") == "node") { Walk(n, ids, tracks, depth + 1); continue; }
                if (ids.Count >= MaxTracks) return;
                string id = Json.Text(n, "id"), title = Json.Text(n, "name"), uri = Json.Text(n, "uri");
                if (id == "") continue;
                // an item VLC has not read the tags of yet is listed under its file name: drop the extension
                try
                {
                    string last = Uri.UnescapeDataString(uri.Substring(uri.LastIndexOf('/') + 1));
                    if (last != "" && last == title) title = NoExt(title);
                }
                catch { }
                var t = new Dictionary<string, object>();
                t["title"] = title; t["artist"] = ""; t["duration"] = Math.Round(Math.Max(0, Json.Number(n, "duration", 0)), 1);
                ids.Add(id); tracks.Add(t);
            }
        }

        public static Dictionary<string, object> Playlist(string since)
        {
            var d = new Dictionary<string, object>();
            d["ok"] = false;
            try
            {
                if (!Configure() || !Live()) { d["error"] = "VLC is not connected"; return d; }
                bool load;
                lock (gate)
                {
                    listWant = true; listWantAt = Environment.TickCount;
                    load = !listHave || unchecked(Environment.TickCount - listAt) > 6000;
                }
                if (load && !LoadList()) { d["error"] = "VLC did not send its playlist"; return d; }
                lock (gate)
                {
                    if (listIds.Count == 0) { d["error"] = "the VLC playlist is empty"; return d; }
                    d["ok"] = true; d["app"] = Id; d["name"] = listName; d["stamp"] = listStamp;
                    d["count"] = listIds.Count; d["index"] = curId == "" ? -1 : listIds.IndexOf(curId);
                    if (since != "" && since == listStamp) d["unchanged"] = true; else d["tracks"] = listTracks;
                }
            }
            catch (Exception e) { d["ok"] = false; d["error"] = e.Message; }
            return d;
        }

        public static Dictionary<string, object> Jump(int index)
        {
            var d = new Dictionary<string, object>();
            d["ok"] = false;
            try
            {
                if (!Configure()) { d["error"] = "not available"; return d; }
                bool have;
                lock (gate) have = listHave;
                if (!have && !LoadList()) { d["error"] = "VLC did not send its playlist"; return d; }
                string id;
                lock (gate)
                {
                    if (index < 0 || index >= listIds.Count) { d["error"] = "no such track"; return d; }
                    id = listIds[index];
                }
                if (Ask("pl_play&id=" + Uri.EscapeDataString(id)) == null) { d["error"] = "VLC did not answer"; return d; }
                lock (gate) curId = id;
                d["ok"] = true; d["index"] = index;
            }
            catch (Exception e) { d["ok"] = false; d["error"] = e.Message; }
            return d;
        }
    }

    // ------------------------------------------------------------------ foobar2000 through the Beefweb component
    // foobar2000 already shows up as a source through Windows. With Beefweb installed (foo_beefweb, port 8880) the
    // bridge adds its playlist, "play track n", shuffle / repeat and the audio details to that same source.
    // Nothing is asked unless foobar2000 is listed as a source right now.
    static class Foobar
    {
        const int MaxTracks = 3000;
        const string PlayerColumns = "%5B%25codec%25%5D,%5B%25bitrate%25%5D,%5B%25samplerate%25%5D,%5B%25channels%25%5D";
        const string ListColumns = "%25title%25,%5B%25artist%25%5D,%5B%25length_seconds%25%5D";
        static readonly object gate = new object();

        static string baseUrl = "", auth = "";
        static bool cfgOk, cfgLooked;
        static int cfgAt;

        static bool seen;
        static int seenAt;

        // player state
        static bool have;
        static int haveAt;
        static string plId = "";
        static int item = -1, mode = -1;
        static List<string> modes = new List<string>();
        static string codec = "";
        static int kbps, hz, ch;
        static int failures, failedAt;
        static string note = "foobar2000 is not a source right now";

        // playlist
        static List<object> listTracks = new List<object>();
        static string listId = "", listName = "", listStamp = "", listSig = "";
        static bool listHave;
        static int listAt, listFullAt, listWantAt;
        static bool listWant;

        public static bool Is(string app)
        {
            return !string.IsNullOrEmpty(app) && app != Aimp.Id && app != Vlc.Id && Names.Friendly(app) == "foobar2000";
        }

        static bool Configure()
        {
            if (cfgLooked && unchecked(Environment.TickCount - cfgAt) < 3000) return cfgOk;
            cfgLooked = true; cfgAt = Environment.TickCount;
            cfgOk = false;
            if (Ini.Off("foobar2000")) { note = "switched off in NocturneBridge.ini"; return false; }
            string port = Ini.Get("foobar2000", "port"), user = Ini.Get("foobar2000", "user"), pass = Ini.Get("foobar2000", "password");
            if (port == "" || (user == "" && pass == ""))
            {
                // Beefweb's own optional config file overrides what is set in foobar2000's preferences
                try
                {
                    string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    foreach (var dir in new[] { "foobar2000-v2", "foobar2000" })
                    {
                        string f = Path.Combine(appData, dir, "beefweb", "config.json");
                        if (!File.Exists(f)) continue;
                        var c = Json.Parse(File.ReadAllText(f)) as Dictionary<string, object>;
                        if (c == null) continue;
                        if (port == "" && c.ContainsKey("port")) port = Json.Number(c, "port", 0).ToString("0", CultureInfo.InvariantCulture);
                        if (user == "" && pass == "" && Json.Flag(c, "authRequired")) { user = Json.Text(c, "authUser"); pass = Json.Text(c, "authPassword"); }
                        break;
                    }
                }
                catch { }
            }
            int p;
            if (!int.TryParse(port, NumberStyles.Integer, CultureInfo.InvariantCulture, out p) || p < 1 || p > 65535) p = 8880;
            baseUrl = "http://127.0.0.1:" + p.ToString(CultureInfo.InvariantCulture) + "/api";
            auth = user != "" || pass != "" ? Web.Basic(user, pass) : "";
            cfgOk = true;
            return true;
        }

        static bool Fresh { get { return have && unchecked(Environment.TickCount - haveAt) <= 3000; } }

        public static Dictionary<string, object> Status()
        {
            var d = new Dictionary<string, object>();
            bool ok = false;
            try { ok = Configure(); } catch { }
            d["address"] = ok ? baseUrl : "";
            lock (gate) d["state"] = Fresh ? "connected" : note;
            return d;
        }

        // Called for the foobar2000 entry of /sessions: remembers that it is there and adds what Beefweb knows.
        public static void Decorate(Dictionary<string, object> s)
        {
            lock (gate)
            {
                seen = true; seenAt = Environment.TickCount;
                if (!Fresh) return;
                s["playlist"] = true;
                if (mode >= 0 && mode < modes.Count)
                {
                    s["shuffle"] = IsShuffle(modes[mode]);
                    s["repeat"] = IsRepeat(modes[mode]);
                }
                if ((string)s["status"] != "stopped" && (codec != "" || kbps > 0 || hz > 0))
                {
                    s["format"] = codec; s["bitrate"] = kbps; s["sampleRate"] = hz; s["channels"] = ch;
                }
            }
        }

        static bool IsShuffle(string name) { string n = name.ToLowerInvariant(); return n.StartsWith("shuffle", StringComparison.Ordinal) || n.StartsWith("random", StringComparison.Ordinal); }
        static bool IsRepeat(string name) { return name.ToLowerInvariant().StartsWith("repeat", StringComparison.Ordinal); }

        public static void Tick()
        {
            bool wanted;
            lock (gate) wanted = seen && unchecked(Environment.TickCount - seenAt) < 10000;
            if (!wanted) { lock (gate) { have = false; note = "foobar2000 is not a source right now"; } return; }
            if (!Configure()) { lock (gate) have = false; return; }
            int now = Environment.TickCount;
            if (failures > 0 && unchecked(now - failedAt) < (failures > 3 ? 10000 : 1500)) return;
            if (!Ask()) return;
            bool want;
            lock (gate) want = listWant && unchecked(Environment.TickCount - listWantAt) < 12000
                               && (!listHave || unchecked(Environment.TickCount - listAt) >= 2000);
            if (want) { try { LoadList(); } catch { } }
        }

        static bool Ask()
        {
            var r = Web.Call("GET", baseUrl + "/player?columns=" + PlayerColumns, auth, null, 1500);
            var p = r.Code == 200 ? Json.At(Json.Parse(r.Text), "player") as Dictionary<string, object> : null;
            if (p == null)
            {
                lock (gate)
                {
                    failures++; failedAt = Environment.TickCount;
                    note = r.Code == 401 ? "Beefweb asks for a password: put user and password in NocturneBridge.ini"
                         : r.Code == 0 ? "no answer on " + baseUrl + " (Beefweb component not installed?)" : "unexpected answer (" + r.Code + ")";
                    if (failures >= 2) have = false;
                }
                return false;
            }
            var a = Json.At(p, "activeItem") as Dictionary<string, object>;
            var cols = a != null && a.ContainsKey("columns") ? a["columns"] as List<object> : null;
            var names = p.ContainsKey("playbackModes") ? p["playbackModes"] as List<object> : null;
            var m = new List<string>();
            if (names != null) foreach (var o in names) m.Add(o as string ?? "");
            lock (gate)
            {
                failures = 0; note = "connected";
                plId = Json.Text(a, "playlistId");
                item = (int)Json.Number(a, "index", -1);
                mode = (int)Json.Number(p, "playbackMode", -1);
                modes = m;
                codec = ""; kbps = 0; hz = 0; ch = 0;
                if (cols != null && item >= 0)
                {
                    string c = Col(cols, 0).ToUpperInvariant();
                    codec = c.Length > 0 && c.Length <= 8 ? c : "";
                    kbps = Num(Col(cols, 1)); hz = Num(Col(cols, 2));
                    string chan = Col(cols, 3).ToLowerInvariant();
                    ch = chan == "mono" ? 1 : chan == "stereo" ? 2 : Num(chan);
                }
                have = true; haveAt = Environment.TickCount;
            }
            return true;
        }

        static string Col(List<object> cols, int i) { return i < cols.Count ? (cols[i] as string ?? "").Trim() : ""; }
        static int Num(string v)
        {
            int n = 0, i = 0;
            while (i < v.Length && v[i] >= '0' && v[i] <= '9' && i < 9) { n = n * 10 + (v[i] - '0'); i++; }
            return i == 0 ? 0 : n;
        }

        // The playlist that is playing; while stopped, the one that is open in foobar2000.
        static bool LoadList()
        {
            var r = Web.Call("GET", baseUrl + "/playlists", auth, null, 1500);
            var all = r.Code == 200 ? Json.At(Json.Parse(r.Text), "playlists") as List<object> : null;
            if (all == null) return false;
            string want;
            lock (gate) want = item >= 0 ? plId : "";
            Dictionary<string, object> pick = null, open = null;
            foreach (var o in all)
            {
                var p = o as Dictionary<string, object>;
                if (p == null) continue;
                if (want != "" && Json.Text(p, "id") == want) pick = p;
                if (Json.Flag(p, "isCurrent")) open = p;
            }
            if (pick == null) pick = open;
            if (pick == null) { foreach (var o in all) { pick = o as Dictionary<string, object>; if (pick != null) break; } }
            if (pick == null) { lock (gate) { listTracks = new List<object>(); listId = ""; listName = ""; listStamp = "foobar-empty"; listSig = ""; listHave = true; listAt = Environment.TickCount; } return true; }

            string id = Json.Text(pick, "id");
            string sig = id + "|" + Json.Number(pick, "itemCount", 0).ToString("0", CultureInfo.InvariantCulture) + "|"
                       + Json.Number(pick, "totalTime", 0).ToString("0.###", CultureInfo.InvariantCulture);
            lock (gate)
            {
                // same list, same size, same total time: keep the tracks; they are read again every 20 s in case of a re-order
                if (listHave && sig == listSig && unchecked(Environment.TickCount - listFullAt) < 20000)
                {
                    listName = Json.Text(pick, "title"); listAt = Environment.TickCount;
                    return true;
                }
            }
            r = Web.Call("GET", baseUrl + "/playlists/" + Uri.EscapeDataString(id) + "/items/0:" + MaxTracks.ToString(CultureInfo.InvariantCulture) + "?columns=" + ListColumns, auth, null, 3000);
            var items = r.Code == 200 ? Json.At(Json.Parse(r.Text), "playlistItems", "items") as List<object> : null;
            if (items == null) return false;
            var tracks = new List<object>();
            long h = Remote.HashSeed;
            foreach (var o in items)
            {
                var row = o as Dictionary<string, object>;
                var cols = row != null && row.ContainsKey("columns") ? row["columns"] as List<object> : null;
                if (cols == null) cols = new List<object>();
                var t = new Dictionary<string, object>();
                string title = Col(cols, 0), artist = Col(cols, 1);
                int sec = Num(Col(cols, 2));
                t["title"] = title; t["artist"] = artist; t["duration"] = (double)sec;
                tracks.Add(t);
                h = Remote.Hash(h, title); h = Remote.Hash(h, artist); h = Remote.Hash(h, sec.ToString(CultureInfo.InvariantCulture));
                if (tracks.Count >= MaxTracks) break;
            }
            lock (gate)
            {
                listTracks = tracks; listId = id; listName = Json.Text(pick, "title"); listSig = sig;
                listStamp = "foobar-" + id + "-" + tracks.Count.ToString(CultureInfo.InvariantCulture) + "-" + h.ToString("x", CultureInfo.InvariantCulture);
                listHave = true; listAt = Environment.TickCount; listFullAt = listAt;
            }
            return true;
        }

        public static Dictionary<string, object> Playlist(string since)
        {
            var d = new Dictionary<string, object>();
            d["ok"] = false;
            try
            {
                if (!Configure()) { d["error"] = "not available"; return d; }
                bool fresh, load, resting;
                lock (gate)
                {
                    seen = true; seenAt = Environment.TickCount;
                    listWant = true; listWantAt = Environment.TickCount;
                    fresh = Fresh;
                    load = !listHave || unchecked(Environment.TickCount - listAt) > 6000;
                    resting = failures > 0 && unchecked(Environment.TickCount - failedAt) < 5000;   // just failed: do not make the caller wait again
                }
                if (!fresh && (resting || !Ask())) { lock (gate) d["error"] = note; return d; }
                if (load && !LoadList()) { d["error"] = "foobar2000 did not send its playlist"; return d; }
                lock (gate)
                {
                    if (listTracks.Count == 0) { d["error"] = "the foobar2000 playlist is empty"; return d; }
                    d["ok"] = true; d["name"] = listName; d["stamp"] = listStamp;
                    d["count"] = listTracks.Count;
                    d["index"] = item >= 0 && plId == listId && item < listTracks.Count ? item : -1;
                    if (since != "" && since == listStamp) d["unchanged"] = true; else d["tracks"] = listTracks;
                }
            }
            catch (Exception e) { d["ok"] = false; d["error"] = e.Message; }
            return d;
        }

        public static Dictionary<string, object> Jump(int index)
        {
            var d = new Dictionary<string, object>();
            d["ok"] = false;
            try
            {
                if (!Configure()) { d["error"] = "not available"; return d; }
                bool haveList;
                lock (gate) haveList = listHave;
                if (!haveList && !LoadList()) { d["error"] = "foobar2000 did not send its playlist"; return d; }
                string id;
                lock (gate)
                {
                    if (listId == "" || index < 0 || index >= listTracks.Count) { d["error"] = "no such track"; return d; }
                    id = listId;
                }
                var r = Web.Call("POST", baseUrl + "/player/play/" + Uri.EscapeDataString(id) + "/" + index.ToString(CultureInfo.InvariantCulture), auth, null, 2000);
                if (!r.Ok) { d["error"] = r.Code == 0 ? "foobar2000 did not answer" : "foobar2000 answered " + r.Code; return d; }
                lock (gate) { plId = id; item = index; }
                d["ok"] = true; d["index"] = index;
            }
            catch (Exception e) { d["ok"] = false; d["error"] = e.Message; }
            return d;
        }

        // what = repeat | shuffle, set = on | off | "" (flip). foobar2000 has one "playback order": Default, Repeat (playlist),
        // Repeat (track), Random, Shuffle (tracks) ... so switching one on replaces the other, switching off goes back to Default.
        public static Dictionary<string, object> Flag(string what, string set)
        {
            var d = new Dictionary<string, object>();
            d["ok"] = false;
            try
            {
                if ((what != "repeat" && what != "shuffle") || !Configure() || !Ask()) return d;
                List<string> m; int cur;
                lock (gate) { m = modes; cur = mode; }
                if (m.Count == 0 || cur < 0 || cur >= m.Count) return d;
                bool isOn = what == "shuffle" ? IsShuffle(m[cur]) : IsRepeat(m[cur]);
                bool want = set == "on" ? true : set == "off" ? false : !isOn;
                if (want != isOn)
                {
                    int target = 0;
                    if (want)
                    {
                        target = -1;
                        string first = what == "shuffle" ? "shuffle" : "repeat";
                        for (int i = 0; i < m.Count && target < 0; i++) if (m[i].ToLowerInvariant().StartsWith(first, StringComparison.Ordinal)) target = i;
                        if (target < 0 && what == "shuffle")
                            for (int i = 0; i < m.Count && target < 0; i++) if (IsShuffle(m[i])) target = i;
                        if (target < 0) return d;
                    }
                    var r = Web.Call("POST", baseUrl + "/player", auth, "{\"playbackMode\":" + target.ToString(CultureInfo.InvariantCulture) + "}", 2000);
                    if (!r.Ok) return d;
                    Thread.Sleep(40);
                    if (!Ask()) return d;
                    lock (gate) { m = modes; cur = mode; }
                    if (cur < 0 || cur >= m.Count) return d;
                }
                d["ok"] = true;
                d["on"] = what == "shuffle" ? IsShuffle(m[cur]) : IsRepeat(m[cur]);
            }
            catch { d["ok"] = false; }
            return d;
        }
    }

    // ------------------------------------------------------------------ tiny JSON writer and reader
    static class Json
    {
        public static string Obj(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return Write(d);
        }

        public static string Write(object o, bool pretty = false)
        {
            var sb = new StringBuilder();
            W(sb, o, pretty, 0);
            return sb.ToString();
        }

        static void W(StringBuilder sb, object o, bool pretty, int depth)
        {
            string nl = pretty ? "\n" + new string(' ', (depth + 1) * 2) : "";
            string end = pretty ? "\n" + new string(' ', depth * 2) : "";
            if (o == null) { sb.Append("null"); return; }
            if (o is string) { Str(sb, (string)o); return; }
            if (o is bool) { sb.Append((bool)o ? "true" : "false"); return; }
            if (o is int || o is long || o is double || o is float)
            {
                sb.Append(Convert.ToDouble(o).ToString("0.###", CultureInfo.InvariantCulture)); return;
            }
            var dict = o as IDictionary;
            if (dict != null)
            {
                sb.Append('{'); bool first = true;
                foreach (DictionaryEntry e in dict)
                {
                    if (!first) sb.Append(','); first = false;
                    sb.Append(nl); Str(sb, e.Key.ToString()); sb.Append(pretty ? ": " : ":"); W(sb, e.Value, pretty, depth + 1);
                }
                if (!first) sb.Append(end);
                sb.Append('}'); return;
            }
            var list = o as IEnumerable;
            if (list != null)
            {
                sb.Append('['); bool first = true;
                foreach (var x in list) { if (!first) sb.Append(','); first = false; sb.Append(nl); W(sb, x, pretty, depth + 1); }
                if (!first) sb.Append(end);
                sb.Append(']'); return;
            }
            Str(sb, o.ToString());
        }

        static void Str(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            sb.Append('"');
        }

        // ---- reader, for what VLC and Beefweb answer: objects -> Dictionary<string, object>, arrays -> List<object>,
        //      numbers -> double, strings, bool, null. Returns null when the text is not JSON.
        public static object Parse(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            try
            {
                int i = 0;
                object v = Value(s, ref i, 0);
                Space(s, ref i);
                return i == s.Length ? v : null;
            }
            catch { return null; }
        }

        static void Space(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\n' || s[i] == '\r' || s[i] == '\t' || s[i] == (char)0xFEFF)) i++;
        }

        static object Value(string s, ref int i, int depth)
        {
            if (depth > 64) throw new FormatException("too deep");
            Space(s, ref i);
            if (i >= s.Length) throw new FormatException("end");
            char c = s[i];
            if (c == '{')
            {
                var d = new Dictionary<string, object>();
                i++; Space(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return d; }
                while (true)
                {
                    Space(s, ref i);
                    string key = Quoted(s, ref i);
                    Space(s, ref i);
                    if (i >= s.Length || s[i] != ':') throw new FormatException("colon");
                    i++;
                    d[key] = Value(s, ref i, depth + 1);
                    Space(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == '}') { i++; return d; }
                    throw new FormatException("object");
                }
            }
            if (c == '[')
            {
                var l = new List<object>();
                i++; Space(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return l; }
                while (true)
                {
                    l.Add(Value(s, ref i, depth + 1));
                    Space(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == ']') { i++; return l; }
                    throw new FormatException("array");
                }
            }
            if (c == '"') return Quoted(s, ref i);
            if (string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            int start = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '-' || s[i] == '+' || s[i] == '.' || s[i] == 'e' || s[i] == 'E')) i++;
            if (i == start) throw new FormatException("value");
            return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        static string Quoted(string s, ref int i)
        {
            if (i >= s.Length || s[i] != '"') throw new FormatException("string");
            i++;
            var sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length) throw new FormatException("open string");
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) throw new FormatException("open escape");
                char e = s[i++];
                if (e == 'n') sb.Append('\n');
                else if (e == 't') sb.Append('\t');
                else if (e == 'r') sb.Append('\r');
                else if (e == 'b') sb.Append('\b');
                else if (e == 'f') sb.Append('\f');
                else if (e == 'u')
                {
                    if (i + 4 > s.Length) throw new FormatException("open \\u");
                    sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 4;
                }
                else sb.Append(e);                     // \" \\ \/
            }
        }

        // Steps into nested objects: At(o, "a", "b") = o.a.b, null when anything on the way is missing.
        public static object At(object o, params string[] path)
        {
            foreach (var key in path)
            {
                var d = o as Dictionary<string, object>;
                if (d == null || !d.TryGetValue(key, out o)) return null;
            }
            return o;
        }

        // o[key] as text ("" when missing); numbers and booleans are written out.
        public static string Text(object o, string key)
        {
            object v = At(o, key);
            if (v == null) return "";
            if (v is string) return (string)v;
            if (v is bool) return (bool)v ? "true" : "false";
            if (v is double) return ((double)v).ToString("0.######", CultureInfo.InvariantCulture);
            return "";
        }

        public static double Number(object o, string key, double fallback)
        {
            object v = At(o, key);
            if (v is double) return (double)v;
            double n;
            if (v is string && double.TryParse((string)v, NumberStyles.Float, CultureInfo.InvariantCulture, out n)) return n;
            return fallback;
        }

        public static bool Flag(object o, string key)
        {
            object v = At(o, key);
            if (v is bool) return (bool)v;
            if (v is double) return (double)v != 0;
            if (v is string) return string.Equals((string)v, "true", StringComparison.OrdinalIgnoreCase) || (string)v == "1";
            return false;
        }
    }
}

// Lumikey — lighting control for the FL-ESPORTS FL980 V2 keyboard (wired).
// Single-file app for .NET Framework 4.x (C# 5): HID access + local HTTP API + embedded web UI
// shown in a Chromium "app" window (Edge or Chrome), with a tray icon.
// Protocol notes: docs/PROTOCOL.md

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

[assembly: AssemblyTitle("Lumikey")]
[assembly: AssemblyDescription("Lighting control for the FL-ESPORTS FL980 V2 keyboard")]
[assembly: AssemblyProduct("Lumikey")]
[assembly: AssemblyCopyright("MIT License")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace Lumikey
{
    // ============================================================ HID
    static class Hid
    {
        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid g; public int Flags; public IntPtr Reserved; }

        [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
        [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetFeature(SafeFileHandle h, byte[] b, int len);
        [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_SetFeature(SafeFileHandle h, byte[] b, int len);
        [DllImport("hid.dll", CharSet = CharSet.Unicode)] static extern bool HidD_GetManufacturerString(SafeFileHandle h, byte[] b, int len);
        [DllImport("setupapi.dll", SetLastError = true)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr w, int f);
        [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, int i, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr s, ref SP_DEVICE_INTERFACE_DATA data, IntPtr det, int size, out int req, IntPtr dev);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFile(string n, uint a, uint s, IntPtr sa, uint d, uint f, IntPtr t);

        // Opcodes that erase flash / jump to the bootloader / factory-reset. Never sent.
        static readonly HashSet<byte> Forbidden = new HashSet<byte> { 0xAC, 0x2C, 0x7F, 0x30, 0x01, 0x02, 0x1C, 0x1E };

        static List<string> Paths()
        {
            Guid g; HidD_GetHidGuid(out g);
            IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12); // PRESENT | DEVICEINTERFACE
            var d = new SP_DEVICE_INTERFACE_DATA(); d.cbSize = Marshal.SizeOf(d);
            var list = new List<string>();
            for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref d); i++)
            {
                int req; SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out req, IntPtr.Zero);
                IntPtr buf = Marshal.AllocHGlobal(req);
                Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                if (SetupDiGetDeviceInterfaceDetail(set, ref d, buf, req, out req, IntPtr.Zero))
                    list.Add(Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4)));
                Marshal.FreeHGlobal(buf);
            }
            SetupDiDestroyDeviceInfoList(set);
            return list;
        }

        // Wired FL980 V2 (SONiX) keyboard collection MI_00. The 2.4G receiver (RONGYUAN) does not relay — skipped.
        public static string FindWired()
        {
            foreach (var p in Paths())
            {
                if (!p.ToLowerInvariant().Contains("vid_05ac&pid_024f&mi_00")) continue;
                using (var h = CreateFile(p, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
                {
                    if (h.IsInvalid) continue;
                    var b = new byte[256];
                    string man = HidD_GetManufacturerString(h, b, 256) ? Encoding.Unicode.GetString(b).TrimEnd('\0') : "";
                    if (man.ToUpperInvariant().Contains("RONGYUAN")) continue;
                    return p;
                }
            }
            return null;
        }

        static SafeFileHandle Open()
        {
            string path = FindWired();
            if (path == null) throw new InvalidOperationException("NOT_CONNECTED");
            var h = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero); // access 0: feature reports only
            if (h.IsInvalid) throw new InvalidOperationException("OPEN_FAILED");
            return h;
        }

        public static byte[] Transact(byte[] payload, int delayMs)
        {
            if (Forbidden.Contains(payload[0])) throw new InvalidOperationException("destructive opcode refused");
            using (var h = Open())
            {
                var pkt = new byte[65]; Array.Copy(payload, 0, pkt, 1, Math.Min(64, payload.Length));
                Thread.Sleep(15);
                if (!HidD_SetFeature(h, pkt, 65)) throw new InvalidOperationException("SetFeature error " + Marshal.GetLastWin32Error());
                Thread.Sleep(delayMs);
                var rx = new byte[65];
                if (!HidD_GetFeature(h, rx, 65)) throw new InvalidOperationException("GetFeature error " + Marshal.GetLastWin32Error());
                var r = new byte[64]; Array.Copy(rx, 1, r, 0, 64); return r;
            }
        }

        // packets[0] carries the opcode; the rest are raw data
        public static void WriteRaw(byte[][] packets)
        {
            if (packets.Length > 0 && packets[0].Length > 0 && Forbidden.Contains(packets[0][0])) throw new InvalidOperationException("destructive opcode refused");
            using (var h = Open())
            {
                foreach (var p in packets)
                {
                    var pkt = new byte[65]; Array.Copy(p, 0, pkt, 1, Math.Min(64, p.Length));
                    Thread.Sleep(15);
                    if (!HidD_SetFeature(h, pkt, 65)) throw new InvalidOperationException("SetFeature error " + Marshal.GetLastWin32Error());
                }
            }
        }
    }

    // ============================================================ keyboard protocol (k68_k237)
    static class Kbd
    {
        public static readonly object Sync = new object();
        public static int[] Matrix;   // LED index -> HID usage (Fn = 0x0A010000), 112 entries

        // Official driver puts the colour selector in the HIGH nibble for these modes; only 0x70 (own RGB) works there.
        static readonly int[] HighNibbleModes = { 2, 3, 5, 7, 8, 10 };
        // Palette colours are sent as RGB in those modes. Values match the swatches in the UI.
        static readonly int[][] PaletteRgb = {
            new[] { 255, 30, 30 }, new[] { 30, 230, 70 }, new[] { 40, 80, 255 },
            new[] { 255, 60, 200 }, new[] { 255, 220, 30 }, new[] { 255, 255, 255 } };

        static bool IsHigh(int mode) { return Array.IndexOf(HighNibbleModes, mode) >= 0; }

        public static Dictionary<string, object> Status()
        {
            var o = new Dictionary<string, object>();
            if (Hid.FindWired() == null) { o["connected"] = false; return o; }
            o["connected"] = true;
            try
            {
                var r = Hid.Transact(new byte[] { 0x8F }, 300);
                if (r[0] == 0x8F) o["id"] = r[1] + 256 * r[2];
            }
            catch { }
            return o;
        }

        public static Dictionary<string, object> ReadLed()
        {
            for (int t = 0; t < 3; t++)
            {
                var r = Hid.Transact(new byte[] { 0x84 }, 400);
                if (r[0] == 0x84)
                {
                    int mode = r[1];
                    int color = Math.Min(7, r[4] & 0x0F), R = r[5], G = r[6], B = r[7], slot = 1;
                    if (IsHigh(mode))
                    {
                        if (r[4] == 0x06) color = 6;
                        else
                        {
                            color = Math.Min(7, r[4] >> 4);
                            if (color == 7)
                                for (int i = 0; i < PaletteRgb.Length; i++)
                                    if (PaletteRgb[i][0] == R && PaletteRgb[i][1] == G && PaletteRgb[i][2] == B) color = i;
                        }
                    }
                    if (mode == 12) { slot = (r[4] >> 4) + 1; color = 7; R = 255; G = 0; B = 0; }
                    var o = new Dictionary<string, object>();
                    o["mode"] = mode; o["speed"] = Math.Max(0, Math.Min(5, 5 - r[2])); o["bright"] = Math.Min(4, (int)r[3]);
                    o["color"] = color; o["r"] = R; o["g"] = G; o["b"] = B; o["slot"] = slot;
                    return o;
                }
                Thread.Sleep(250);
            }
            throw new InvalidOperationException("NO_REPLY");
        }

        public static void WriteLed(int mode, int speed, int bright, int color, int R, int G, int B, int slot)
        {
            byte spd = (byte)(5 - Math.Max(0, Math.Min(5, speed)));   // driver: byte2 = 5 - speed
            byte[] p;
            if (mode == 12)
            {
                slot = Math.Max(1, Math.Min(3, slot));
                p = new byte[] { 0x04, 12, spd, 4, (byte)((slot - 1) * 16), 0x00, 0xC8, 0xC8 };
            }
            else
            {
                int c = color & 0x0F, colorByte;
                if (IsHigh(mode))
                {
                    if (c == 6) colorByte = 0x06;                          // rainbow (breath: new colour each breath)
                    else
                    {
                        if (c < PaletteRgb.Length) { R = PaletteRgb[c][0]; G = PaletteRgb[c][1]; B = PaletteRgb[c][2]; c = 7; }
                        colorByte = c << 4;                                // 0x70 = own RGB
                    }
                }
                else colorByte = c;                                        // static: low nibble (0..6 palette, 7 own RGB)
                p = new byte[] { 0x04, (byte)mode, spd, (byte)Math.Min(4, Math.Max(0, bright)), (byte)colorByte, Clamp(R), Clamp(G), Clamp(B) };
            }
            Hid.Transact(p, 250);
        }

        static byte Clamp(int v) { return (byte)Math.Max(0, Math.Min(255, v)); }

        // User picture: 09 00 80 01 (384 bytes follow) + 6 raw 64-byte RGB packets in LED-index order
        public static void UploadPic(int[] rgb)
        {
            var packets = new byte[7][];
            packets[0] = new byte[64]; packets[0][0] = 0x09; packets[0][2] = 0x80; packets[0][3] = 0x01;
            for (int n = 0; n < 6; n++)
            {
                packets[n + 1] = new byte[64];
                for (int j = 0; j < 64; j++) packets[n + 1][j] = Clamp(rgb[64 * n + j]);
            }
            Hid.WriteRaw(packets);
        }
    }

    // ============================================================ picture storage (%APPDATA%\Lumikey)
    static class Store
    {
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lumikey");
        static string SlotFile(int slot) { return Path.Combine(Dir, "slot" + slot + ".txt"); }
        static readonly Regex Line = new Regex(@"^\s*#(\d+)\s*=\s*#?([0-9A-Fa-f]{6})");

        public static int[] ReadPic(int slot)
        {
            var pic = new int[384];
            string f = SlotFile(slot);
            if (!File.Exists(f)) return pic;
            foreach (var line in File.ReadAllLines(f))
            {
                var m = Line.Match(line);
                if (!m.Success) continue;
                int i = int.Parse(m.Groups[1].Value);
                if (i >= 128) continue;
                for (int c = 0; c < 3; c++) pic[3 * i + c] = Convert.ToInt32(m.Groups[2].Value.Substring(2 * c, 2), 16);
            }
            return pic;
        }

        public static void SavePic(int slot, int[] rgb)
        {
            Directory.CreateDirectory(Dir);
            string f = SlotFile(slot);
            if (File.Exists(f))
            {   // keep the previous picture: history\slotN_<time>.txt, last 20 per slot
                string hist = Path.Combine(Dir, "history");
                Directory.CreateDirectory(hist);
                File.Copy(f, Path.Combine(hist, string.Format("slot{0}_{1:yyyyMMdd_HHmmss_fff}.txt", slot, DateTime.Now)));
                var old = Directory.GetFiles(hist, "slot" + slot + "_*.txt");
                Array.Sort(old); Array.Reverse(old);
                for (int i = 20; i < old.Length; i++) try { File.Delete(old[i]); } catch { }
            }
            var sb = new StringBuilder();
            for (int i = 0; i < 128; i++) sb.AppendFormat("#{0}={1:X2}{2:X2}{3:X2}\r\n", i, rgb[3 * i], rgb[3 * i + 1], rgb[3 * i + 2]);
            File.WriteAllText(f, sb.ToString());
        }
    }

    // ============================================================ HTTP API
    class Server
    {
        public int Port;
        public string Token = Guid.NewGuid().ToString("N");
        public string Url { get { return "http://127.0.0.1:" + Port + "/?t=" + Token; } }
        public DateTime LastSeen = DateTime.Now;
        public bool SeenAny;
        readonly HttpListener listener = new HttpListener();
        readonly byte[] page;
        readonly JavaScriptSerializer json = new JavaScriptSerializer();

        public Server(byte[] page) { this.page = page; }

        public void Start()
        {
            foreach (int p in new[] { 47980, 0 })
            {
                Port = p != 0 ? p : FreePort();
                try
                {
                    listener.Prefixes.Clear();
                    listener.Prefixes.Add("http://127.0.0.1:" + Port + "/");
                    listener.Start();
                    break;
                }
                catch (HttpListenerException) { if (p == 0) throw; }
            }
            var th = new Thread(Loop); th.IsBackground = true; th.Start();
        }

        static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0); l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return port;
        }

        void Loop()
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); } catch { return; }
                ThreadPool.QueueUserWorkItem(delegate { Handle(ctx); });
            }
        }

        void Send(HttpListenerContext ctx, int code, byte[] body, string type)
        {
            try
            {
                var res = ctx.Response;
                res.StatusCode = code; res.ContentType = type;
                res.Headers["Cache-Control"] = "no-store";
                res.ContentLength64 = body.Length;
                res.OutputStream.Write(body, 0, body.Length);
                res.OutputStream.Close();
            }
            catch { }
        }
        void SendJson(HttpListenerContext ctx, object o, int code)
        {
            Send(ctx, code, Encoding.UTF8.GetBytes(json.Serialize(o)), "application/json; charset=utf-8");
        }
        static Dictionary<string, object> Obj(string k, object v) { var d = new Dictionary<string, object>(); d[k] = v; return d; }
        static int I(Dictionary<string, object> d, string k, int def)
        {
            object v; return d != null && d.TryGetValue(k, out v) && v != null ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : def;
        }

        void Handle(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            string path = req.Url.AbsolutePath;
            try
            {
                if (path == "/" || path == "/index.html")
                {
                    if (req.QueryString["t"] != Token) { Send(ctx, 403, Encoding.UTF8.GetBytes("forbidden"), "text/plain"); return; }
                    Send(ctx, 200, page, "text/html; charset=utf-8"); return;
                }
                if (path == "/favicon.ico") { Send(ctx, 204, new byte[0], "text/plain"); return; }
                if (req.Headers["X-Token"] != Token) { SendJson(ctx, Obj("error", "forbidden"), 403); return; }
                SeenAny = true; LastSeen = DateTime.Now;

                Dictionary<string, object> body = null;
                if (req.HttpMethod == "POST")
                    using (var sr = new StreamReader(req.InputStream, Encoding.UTF8))
                        body = json.DeserializeObject(sr.ReadToEnd()) as Dictionary<string, object>;

                string route = req.HttpMethod + " " + path;
                lock (Kbd.Sync)
                {
                    switch (route)
                    {
                        case "GET /api/ping": SendJson(ctx, Obj("ok", true), 200); return;
                        case "GET /api/status": SendJson(ctx, Kbd.Status(), 200); return;
                        case "GET /api/matrix": SendJson(ctx, Obj("matrix", Kbd.Matrix), 200); return;
                        case "GET /api/led": SendJson(ctx, Kbd.ReadLed(), 200); return;
                        case "POST /api/led":
                            Kbd.WriteLed(I(body, "mode", 1), I(body, "speed", 3), I(body, "bright", 4), I(body, "color", 0),
                                         I(body, "r", 255), I(body, "g", 0), I(body, "b", 0), I(body, "slot", 1));
                            SendJson(ctx, Obj("ok", true), 200); return;
                        case "GET /api/pic":
                            {
                                int slot; if (!int.TryParse(req.QueryString["slot"], out slot) || slot < 1 || slot > 3) slot = 1;
                                var o = Obj("slot", slot); o["rgb"] = Store.ReadPic(slot);
                                SendJson(ctx, o, 200); return;
                            }
                        case "POST /api/pic":
                            {
                                int slot = Math.Max(1, Math.Min(3, I(body, "slot", 1)));
                                var arr = body != null && body.ContainsKey("rgb") ? body["rgb"] as IList : null;
                                if (arr == null || arr.Count != 384) { SendJson(ctx, Obj("error", "rgb must have 384 values"), 400); return; }
                                var rgb = new int[384];
                                for (int i = 0; i < 384; i++) rgb[i] = Convert.ToInt32(arr[i], CultureInfo.InvariantCulture);
                                Store.SavePic(slot, rgb);
                                Kbd.UploadPic(rgb);
                                Thread.Sleep(400);
                                Kbd.WriteLed(12, 3, 4, 0, 0, 0, 0, slot);
                                SendJson(ctx, Obj("ok", true), 200); return;
                            }
                        default: SendJson(ctx, Obj("error", "not found"), 404); return;
                    }
                }
            }
            catch (Exception e)
            {
                SendJson(ctx, Obj("error", e.Message), e.Message.Contains("NOT_CONNECTED") ? 409 : 500);
            }
        }
    }

    // ============================================================ app / tray
    static class Program
    {
        static Server server;
        static NotifyIcon tray;
        static readonly string InstanceFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lumikey", "instance.txt");

        static byte[] Resource(string name)
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            using (var ms = new MemoryStream()) { s.CopyTo(ms); return ms.ToArray(); }
        }

        static int[] LoadMatrix()
        {
            string txt = Encoding.UTF8.GetString(Resource("keymatrix.txt"));
            var data = new StringBuilder();
            foreach (var line in txt.Split('\n')) if (!line.TrimStart().StartsWith("#")) data.Append(line.Trim());
            var items = data.ToString().Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            var m = new int[items.Length];
            for (int i = 0; i < items.Length; i++)
            {
                var f = items[i].Split(',');
                m[i] = int.Parse(f[0]) * 0x1000000 + int.Parse(f[1]) * 0x10000 + int.Parse(f[2]);
            }
            return m;
        }

        static string FindBrowser()
        {
            var c = new List<string> {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) + @"\Microsoft\Edge\Application\msedge.exe",
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + @"\Microsoft\Edge\Application\msedge.exe" };
            foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
                foreach (var exe in new[] { "msedge.exe", "chrome.exe" })
                    try
                    {
                        using (var k = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe))
                            if (k != null && k.GetValue(null) != null) c.Add(k.GetValue(null).ToString().Trim('"'));
                    }
                    catch { }
            c.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + @"\Google\Chrome\Application\chrome.exe");
            c.Add(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + @"\Google\Chrome\Application\chrome.exe");
            foreach (var p in c) if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
            return null;
        }

        static void OpenWindow(string url)
        {
            string browser = FindBrowser();
            if (browser != null)
            {
                string profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lumikey", "browser");
                Process.Start(browser, "--app=\"" + url + "\" --window-size=1320,860 --user-data-dir=\"" + profile + "\" --no-first-run --no-default-browser-check");
            }
            else Process.Start(url);
        }

        // first run of the packaged app: pick up pictures made with the old scripts next to the exe
        static void MigrateOldPictures()
        {
            try
            {
                Directory.CreateDirectory(Store.Dir);
                string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                for (int s = 1; s <= 3; s++)
                {
                    string dst = Path.Combine(Store.Dir, "slot" + s + ".txt");
                    foreach (var dir in new[] { exeDir, Path.Combine(exeDir, "..") })
                    {
                        string src = Path.Combine(dir, "userpic_slot" + s + ".txt");
                        if (!File.Exists(dst) && File.Exists(src)) File.Copy(src, dst);
                    }
                }
            }
            catch { }
        }

        [STAThread]
        static void Main()
        {
            bool first;
            using (var mutex = new Mutex(true, "Lumikey.SingleInstance", out first))
            {
                if (!first)
                {   // already running: just open its window
                    try { OpenWindow(File.ReadAllText(InstanceFile).Trim()); } catch { }
                    return;
                }

                Application.EnableVisualStyles();
                Kbd.Matrix = LoadMatrix();
                MigrateOldPictures();

                server = new Server(Resource("index.html"));
                try { server.Start(); }
                catch (Exception e) { MessageBox.Show("Lumikey could not start its local server:\n" + e.Message, "Lumikey"); return; }
                Directory.CreateDirectory(Path.GetDirectoryName(InstanceFile));
                File.WriteAllText(InstanceFile, server.Url);

                tray = new NotifyIcon();
                try { tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { tray.Icon = SystemIcons.Application; }
                tray.Text = "Lumikey";
                var menu = new ContextMenuStrip();
                menu.Items.Add("Open Lumikey", null, delegate { OpenWindow(server.Url); });
                menu.Items.Add("Exit", null, delegate { Application.Exit(); });
                tray.ContextMenuStrip = menu;
                tray.DoubleClick += delegate { OpenWindow(server.Url); };
                tray.Visible = true;

                OpenWindow(server.Url);

                // the UI pings every 2.5 s; quit ~20 s after the window is closed (or if it never showed up)
                var timer = new System.Windows.Forms.Timer { Interval = 2000 };
                var started = DateTime.Now;
                timer.Tick += delegate
                {
                    double idle = (DateTime.Now - server.LastSeen).TotalSeconds;
                    if ((server.SeenAny && idle > 20) || (!server.SeenAny && (DateTime.Now - started).TotalSeconds > 120)) Application.Exit();
                };
                timer.Start();

                Application.Run();
                tray.Visible = false;
                try { File.Delete(InstanceFile); } catch { }
            }
        }
    }
}

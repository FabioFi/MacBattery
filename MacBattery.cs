// MacBattery - a macOS-style battery indicator for the Windows system tray.
// Build: C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /out:MacBattery.exe MacBattery.cs
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;        // 0 offline, 1 online, 255 unknown
        public byte BatteryFlag;         // 8 charging, 128 no battery, 255 unknown
        public byte BatteryLifePercent;  // 0-100, 255 unknown
        public byte SystemStatusFlag;    // 1 = battery saver on
        public int BatteryLifeTime;      // seconds, -1 unknown
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")] public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr handle);
    public const int SM_CXSMICON = 49;
}

class BatteryState
{
    public int Percent;          // 0-100
    public bool HasBattery;
    public bool PluggedIn;
    public bool Charging;
    public bool Saver;
    public int SecondsLeft;      // -1 unknown

    public static BatteryState Read()
    {
        Native.SYSTEM_POWER_STATUS s;
        var b = new BatteryState { Percent = 100, HasBattery = false, SecondsLeft = -1 };
        if (!Native.GetSystemPowerStatus(out s)) return b;
        b.HasBattery = s.BatteryFlag != 128 && s.BatteryFlag != 255;
        b.Percent = s.BatteryLifePercent <= 100 ? s.BatteryLifePercent : 100;
        b.PluggedIn = s.ACLineStatus == 1;
        b.Charging = (s.BatteryFlag & 8) != 0;
        b.Saver = (s.SystemStatusFlag & 1) != 0;
        b.SecondsLeft = s.BatteryLifeTime;
        return b;
    }

    public string Key(bool showPercent, bool light, int size)
    {
        return string.Join("|", Percent, HasBattery, PluggedIn, Charging, Saver, showPercent, light, size);
    }

    public string Describe()
    {
        if (!HasBattery) return "No battery detected";
        string text = Percent + "%";
        if (Charging) text += " — Charging";
        else if (PluggedIn) text += Percent >= 100 ? " — Fully charged" : " — Plugged in, not charging";
        else if (SecondsLeft > 0)
        {
            int h = SecondsLeft / 3600, m = (SecondsLeft % 3600) / 60;
            text += " — " + (h > 0 ? h + " h " : "") + m + " min remaining";
        }
        if (Saver) text += " (Energy saver)";
        return text;
    }
}

static class BatteryRenderer
{
    // 3x5 pixel font for crisp digits at 16px.
    static readonly string[] Digits = {
        "111101101101111", "010110010010111", "111001111100111", "111001111001111", "101101111001001",
        "111100111001111", "111100111101111", "111001001001001", "111101111101111", "111101111001111" };

    static readonly Color Red = Color.FromArgb(255, 69, 58);
    static readonly Color Green = Color.FromArgb(48, 209, 88);
    static readonly Color Yellow = Color.FromArgb(255, 214, 10);

    static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static GraphicsPath PixelDigits(string num)
    {
        var p = new GraphicsPath();
        int x0 = 1 + (12 - (num.Length * 4 - 1) + 1) / 2;
        for (int i = 0; i < num.Length; i++)
        {
            string glyph = Digits[num[i] - '0'];
            for (int k = 0; k < 15; k++)
                if (glyph[k] == '1') p.AddRectangle(new RectangleF(x0 + i * 4 + k % 3, 5 + k / 3, 1, 1));
        }
        return p;
    }

    // Above 16px the pixel font scales unevenly, so use a real font fitted to the battery's inner area.
    static GraphicsPath VectorDigits(string num)
    {
        var p = new GraphicsPath();
        using (var family = new FontFamily("Segoe UI"))
        using (var fmt = StringFormat.GenericTypographic)
            p.AddString(num, family, (int)FontStyle.Bold, 100f, PointF.Empty, fmt);
        RectangleF b = p.GetBounds();
        float scale = Math.Min(11f / b.Width, 5.6f / b.Height);
        using (var m = new Matrix())
        {
            m.Translate(7f, 7.5f);
            m.Scale(scale, scale);
            m.Translate(-(b.X + b.Width / 2), -(b.Y + b.Height / 2));
            p.Transform(m);
        }
        return p;
    }

    static Bitmap Mask(int size, GraphicsPath path, bool crisp)
    {
        var m = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(m))
        {
            g.SmoothingMode = crisp ? SmoothingMode.None : SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.ScaleTransform(size / 16f, size / 16f);
            g.FillPath(Brushes.White, path);
        }
        return m;
    }

    // Draws a shape in fg, but punched out (transparent) where it overlaps the battery fill.
    // Done per pixel so anti-aliased edges blend cleanly (icons are at most ~32x32).
    static void Knockout(Bitmap bmp, GraphicsPath shape, RectangleF fillRect, Color fg, bool crisp)
    {
        int size = bmp.Width;
        using (var fill = new GraphicsPath())
        {
            fill.AddRectangle(fillRect);
            using (var shapeMask = Mask(size, shape, crisp))
            using (var fillMask = Mask(size, fill, crisp))
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        float t = shapeMask.GetPixel(x, y).A / 255f;
                        if (t == 0) continue;
                        float f = fillMask.GetPixel(x, y).A / 255f;
                        Color p = bmp.GetPixel(x, y);
                        float pa = p.A / 255f * (1 - t * f);   // punch a hole where over the fill
                        float ta = t * (1 - f);                 // draw fg where over empty space
                        float a = ta + pa * (1 - ta);
                        if (a <= 0) { bmp.SetPixel(x, y, Color.Transparent); continue; }
                        Func<int, int, int> mix = (c, b) => (int)Math.Round((c * ta + b * pa * (1 - ta)) / a);
                        bmp.SetPixel(x, y, Color.FromArgb((int)Math.Round(a * 255), mix(fg.R, p.R), mix(fg.G, p.G), mix(fg.B, p.B)));
                    }
        }
    }

    // Everything is laid out on a 16x16 grid and scaled to the real icon size.
    public static Bitmap Render(BatteryState st, bool showPercent, bool lightTaskbar, int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        Color fg = lightTaskbar ? Color.Black : Color.White;
        Color dim = Color.FromArgb(lightTaskbar ? 110 : 140, fg);

        Color fillColor = fg;
        if (st.Charging) fillColor = Green;
        else if (st.Saver) fillColor = Yellow;
        else if (st.HasBattery && st.Percent <= 20 && !st.PluggedIn) fillColor = Red;

        GraphicsPath knockShape = null;
        RectangleF knockFill = RectangleF.Empty;
        bool crisp = false;
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            float s = size / 16f;
            g.ScaleTransform(s, s);

            // Body outline (pixel rows 3..11, cols 0..13) and terminal nub.
            using (var pen = new Pen(dim, 1f))
            using (var body = RoundRect(new RectangleF(0.5f, 3.5f, 13f, 8f), 2.2f))
                g.DrawPath(pen, body);
            using (var nubBrush = new SolidBrush(dim))
            using (var nub = RoundRect(new RectangleF(14f, 6f, 1.6f, 3f), 0.7f))
                g.FillPath(nubBrush, nub);

            int pct = st.HasBattery ? st.Percent : 100;

            if (showPercent && st.HasBattery)
            {
                // Mac "show percentage": fill the whole inner area proportionally, number knocked out of it.
                float w = 12f * pct / 100f;
                if (size == 16) w = (float)Math.Round(w);
                knockFill = new RectangleF(1f, 4f, w, 7f);
                using (var brush = new SolidBrush(fillColor))
                using (var fillPath = RoundRect(knockFill, 1.4f))
                    if (w >= 2.8f) g.FillPath(brush, fillPath); else g.FillRectangle(brush, knockFill);

                knockShape = size == 16 ? PixelDigits(pct.ToString()) : VectorDigits(pct.ToString());
                crisp = size == 16;
            }
            else
            {
                // Classic icon: inset fill with a 1px gap from the outline.
                float w = st.HasBattery ? Math.Max(1f, 10f * pct / 100f) : 0f;
                knockFill = new RectangleF(2f, 5f, w, 5f);
                if (w > 0)
                    using (var brush = new SolidBrush(fillColor))
                    using (var fillPath = RoundRect(knockFill, 1f))
                        if (w >= 2f) g.FillPath(brush, fillPath); else g.FillRectangle(brush, knockFill);

                if (st.PluggedIn || !st.HasBattery)
                {
                    knockShape = new GraphicsPath();
                    knockShape.AddPolygon(new[] {
                        new PointF(8.1f, 4.2f), new PointF(4.4f, 8.1f), new PointF(6.7f, 8.1f),
                        new PointF(5.9f, 10.8f), new PointF(9.6f, 6.9f), new PointF(7.3f, 6.9f) });
                }
            }
        }
        if (knockShape != null)
            using (knockShape) Knockout(bmp, knockShape, knockFill, fg, crisp);
        return bmp;
    }
}

class TrayApp : ApplicationContext
{
    const string SettingsKey = @"Software\MacBattery";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    readonly NotifyIcon tray = new NotifyIcon();
    readonly Timer timer = new Timer { Interval = 5000 };
    readonly ToolStripMenuItem statusItem = new ToolStripMenuItem { Enabled = false };
    readonly ToolStripMenuItem percentItem = new ToolStripMenuItem("Show percentage inside icon");
    readonly ToolStripMenuItem startupItem = new ToolStripMenuItem("Start with Windows");
    Icon currentIcon;
    string lastKey;
    bool showPercent;

    public TrayApp()
    {
        showPercent = ReadSetting("ShowPercent", 1) == 1;
        percentItem.Checked = showPercent;
        startupItem.Checked = IsStartupEnabled();

        percentItem.Click += delegate
        {
            showPercent = !showPercent;
            percentItem.Checked = showPercent;
            WriteSetting("ShowPercent", showPercent ? 1 : 0);
            Refresh();
        };
        startupItem.Click += delegate { SetStartup(!startupItem.Checked); startupItem.Checked = IsStartupEnabled(); };

        var menu = new ContextMenuStrip();
        menu.Items.Add(statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(percentItem);
        menu.Items.Add(startupItem);
        menu.Items.Add("Battery settings…", null, delegate { Open("ms-settings:batterysaver"); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, delegate { ExitThread(); });
        menu.Opening += delegate { Refresh(); };
        tray.ContextMenuStrip = menu;

        // Left click opens the same menu, like clicking the battery in the macOS menu bar.
        tray.MouseUp += (s, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            var mi = typeof(NotifyIcon).GetMethod("ShowContextMenu",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (mi != null) mi.Invoke(tray, null);
        };

        timer.Tick += delegate { Refresh(); };
        SystemEvents.PowerModeChanged += OnSystemChange;
        SystemEvents.UserPreferenceChanged += OnSystemChange;
        SystemEvents.DisplaySettingsChanged += OnSystemChange;

        Refresh();
        tray.Visible = true;
        timer.Start();
    }

    void OnSystemChange(object sender, EventArgs e) { Refresh(); }

    static bool TaskbarIsLight()
    {
        return ReadValue(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) == 1;
    }

    void Refresh()
    {
        var st = BatteryState.Read();
        bool light = TaskbarIsLight();
        int size = Native.GetSystemMetrics(Native.SM_CXSMICON);
        if (size <= 0) size = 16;

        string text = st.Describe();
        statusItem.Text = text;
        tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;

        string key = st.Key(showPercent, light, size);
        if (key == lastKey) return;
        lastKey = key;

        using (var bmp = BatteryRenderer.Render(st, showPercent, light, size))
        {
            var icon = Icon.FromHandle(bmp.GetHicon());
            tray.Icon = icon;
            if (currentIcon != null) { Native.DestroyIcon(currentIcon.Handle); currentIcon.Dispose(); }
            currentIcon = icon;
        }
    }

    static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }

    static int ReadValue(string path, string name, int fallback)
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(path))
            {
                object v = k == null ? null : k.GetValue(name);
                return v is int ? (int)v : fallback;
            }
        }
        catch { return fallback; }
    }

    static int ReadSetting(string name, int fallback) { return ReadValue(SettingsKey, name, fallback); }

    static void WriteSetting(string name, int value)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(SettingsKey)) k.SetValue(name, value, RegistryValueKind.DWord);
    }

    static bool IsStartupEnabled()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
            return k != null && k.GetValue("MacBattery") != null;
    }

    static void SetStartup(bool enable)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (enable) k.SetValue("MacBattery", "\"" + Application.ExecutablePath + "\"");
            else k.DeleteValue("MacBattery", false);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.PowerModeChanged -= OnSystemChange;
            SystemEvents.UserPreferenceChanged -= OnSystemChange;
            SystemEvents.DisplaySettingsChanged -= OnSystemChange;
            timer.Dispose();
            tray.Visible = false;
            tray.Dispose();
            if (currentIcon != null) { Native.DestroyIcon(currentIcon.Handle); currentIcon.Dispose(); }
        }
        base.Dispose(disposing);
    }
}

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Native.SetProcessDPIAware();

        // MacBattery.exe --preview <dir> writes sample icons for every state (useful for tweaking the look).
        if (args.Length == 2 && args[0] == "--preview") { WritePreview(args[1]); return; }

        bool created;
        using (new System.Threading.Mutex(true, "MacBattery.SingleInstance", out created))
        {
            if (!created) return;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayApp());
        }
    }

    static void WritePreview(string dir)
    {
        Directory.CreateDirectory(dir);
        var states = new[] {
            new BatteryState { HasBattery = true, Percent = 100, PluggedIn = true },
            new BatteryState { HasBattery = true, Percent = 76 },
            new BatteryState { HasBattery = true, Percent = 42, Charging = true, PluggedIn = true },
            new BatteryState { HasBattery = true, Percent = 35, Saver = true },
            new BatteryState { HasBattery = true, Percent = 12 } };
        int[] sizes = { 16, 20, 24, 32 };
        const int zoom = 6, pad = 8;
        int cellW = 32 * zoom + pad;
        using (var sheet = new Bitmap(states.Length * 2 * cellW + pad, sizes.Length * 2 * (32 * zoom + pad) + pad))
        using (var g = Graphics.FromImage(sheet))
        {
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.Clear(Color.Gray);
            for (int theme = 0; theme < 2; theme++)
                for (int si = 0; si < sizes.Length; si++)
                {
                    int y = pad + (theme * sizes.Length + si) * (32 * zoom + pad);
                    for (int c = 0; c < states.Length * 2; c++)
                    {
                        int x = pad + c * cellW;
                        bool light = theme == 1;
                        using (var bg = new SolidBrush(light ? Color.FromArgb(243, 243, 243) : Color.FromArgb(32, 32, 32)))
                            g.FillRectangle(bg, x, y, 32 * zoom, 32 * zoom);
                        using (var icon = BatteryRenderer.Render(states[c % states.Length], c >= states.Length, light, sizes[si]))
                            g.DrawImage(icon, x, y, sizes[si] * zoom, sizes[si] * zoom);
                    }
                }
            sheet.Save(Path.Combine(dir, "preview.png"), ImageFormat.Png);
        }
    }
}

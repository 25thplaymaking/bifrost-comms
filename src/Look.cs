// How Bifrost Comms looks: its colours, its fonts and its icon (a radio mast sending, on a dark green tile), plus the small
// JSON helper the settings, comms.json, the manifest and the game's bridge files go through. C# 5 for Windows' own
// compiler (.NET Framework 4.x).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Web.Script.Serialization;

// Plain objects of strings, numbers and booleans. A missing or wrongly typed value reads as null, false or 0.
static class Json {
  public static Dictionary<string, object> Parse(string text) {
    return new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object> ?? new Dictionary<string, object>();
  }

  public static string Write(object value) { return new JavaScriptSerializer().Serialize(value); }

  public static string Str(Dictionary<string, object> d, string key) {
    object v;
    if (d == null || !d.TryGetValue(key, out v) || v == null) return null;
    return Convert.ToString(v, CultureInfo.InvariantCulture);
  }

  public static bool Bool(Dictionary<string, object> d, string key) {
    object v;
    return d != null && d.TryGetValue(key, out v) && v is bool && (bool)v;
  }

  public static long Long(Dictionary<string, object> d, string key) {
    object v;
    if (d == null || !d.TryGetValue(key, out v) || v == null || v is bool) return 0;
    try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); } catch (Exception) { return 0; }
  }
}

// Night-vision greens around a warm amber accent.
static class Palette {
  public static readonly Color
    Bg = Hex("#090c0b"), Surface = Hex("#101513"), Field = Hex("#0c1110"), Rule = Hex("#202a26"), Edge = Hex("#2c3833"),
    Fg = Hex("#e3ece7"), Soft = Hex("#bfcbc5"), Muted = Hex("#9aaba3"), Dim = Hex("#82948b"), Faint = Hex("#3c4a44"),
    Track = Hex("#26302c"), Accent = Hex("#e3a33b"), OnAccent = Hex("#090c0b"), Red = Hex("#e4493f");

  public static Color Hex(string hex) {
    int v = int.Parse(hex.TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    return Color.FromArgb((v >> 16) & 0xff, (v >> 8) & 0xff, v & 0xff);
  }

  public static Color Alpha(Color c, double a) { return Color.FromArgb((int)Math.Round(Math.Max(0, Math.Min(1, a)) * 255), c.R, c.G, c.B); }

  // t > 0 mixes towards white, t < 0 towards black.
  public static Color Shade(Color c, double t) {
    double to = t > 0 ? 255 : 0, k = Math.Min(1, Math.Abs(t));
    Func<int, int> mix = delegate(int x) { return (int)Math.Round(x + (to - x) * k); };
    return Color.FromArgb(c.A, mix(c.R), mix(c.G), mix(c.B));
  }
}

// Windows 10 and 11 ship Bahnschrift (condensed, for labels) and Cascadia Mono (the terminal); older systems fall back to
// Segoe UI and Consolas, Mono's Linux build to DejaVu.
static class Fonts {
  static readonly HashSet<string> families = Families();
  static readonly Dictionary<string, Font> made = new Dictionary<string, Font>();

  public static readonly string
    Ui = First("Bahnschrift SemiCondensed", "Segoe UI", "DejaVu Sans"),
    UiBold = First("Bahnschrift SemiBold SemiConden", "Segoe UI Semibold", "DejaVu Sans"),
    Mono = First("Cascadia Mono", "Consolas", "DejaVu Sans Mono");

  static HashSet<string> Families() {
    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    try { using (var all = new InstalledFontCollection()) foreach (var f in all.Families) names.Add(f.Name); } catch (Exception) { }
    return names;
  }

  static string First(params string[] wanted) {
    foreach (var n in wanted) if (families.Contains(n)) return n;
    return FontFamily.GenericSansSerif.Name;
  }

  // px: the size in pixels, already scaled for the screen.
  public static Font Get(string family, float px) {
    string key = family + "/" + px.ToString("0.0", CultureInfo.InvariantCulture);
    Font f;
    lock (made) if (!made.TryGetValue(key, out f)) made[key] = f = new Font(family, px, FontStyle.Regular, GraphicsUnit.Pixel);
    return f;
  }
}

// The icon's tile and signal colours, which the window's highlights use too.
static class CommsLook {
  public static readonly Color Tile = Palette.Hex("#16281e"), Signal = Palette.Hex("#5fe08a"), Mast = Palette.Hex("#dde8e1");
}

// The icon: a lattice radio mast with its beacon lit and two rings of signal either side, on a rounded tile. Drawn in a
// 1000-unit square and scaled; below 32 px it drops the outer rings and the bracing so it stays readable.
static class Badge {
  public static void Draw(Graphics g, RectangleF r) {
    var saved = g.Save();
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.TranslateTransform(r.X, r.Y);
    g.ScaleTransform(r.Width / 1000f, r.Height / 1000f);
    bool small = Math.Min(r.Width, r.Height) < 32;

    using (var tile = RoundRect(new RectangleF(40, 40, 920, 920), 190)) {
      using (var b = new SolidBrush(CommsLook.Tile)) g.FillPath(b, tile);
      using (var rim = new Pen(Palette.Alpha(CommsLook.Signal, 0.28), 14)) g.DrawPath(rim, tile);
    }

    var top = new PointF(500, 380);
    using (var mast = Stroke(CommsLook.Mast, small ? 70 : 42)) {
      g.DrawLine(mast, top.X, top.Y + 40, 372, 850);
      g.DrawLine(mast, top.X, top.Y + 40, 628, 850);
      if (!small) {
        g.DrawLine(mast, 430, 625, 570, 625);
        g.DrawLine(mast, 410, 735, 590, 735);
        using (var brace = Stroke(CommsLook.Mast, 26)) { g.DrawLine(brace, 430, 625, 590, 735); g.DrawLine(brace, 570, 625, 410, 735); }
      }
    }

    using (var near = Stroke(CommsLook.Signal, small ? 64 : 42)) using (var far = Stroke(Palette.Alpha(CommsLook.Signal, 0.6), 42)) {
      Rings(g, near, top, 150);
      if (!small) Rings(g, far, top, 255);
    }
    using (var beacon = new SolidBrush(CommsLook.Signal)) g.FillEllipse(beacon, top.X - 50, top.Y - 50, 100, 100);
    g.Restore(saved);
  }

  static void Rings(Graphics g, Pen pen, PointF c, float radius) {
    var box = new RectangleF(c.X - radius, c.Y - radius, radius * 2, radius * 2);
    g.DrawArc(pen, box, -38, 76);
    g.DrawArc(pen, box, 142, 76);
  }

  static Pen Stroke(Color c, float width) { return new Pen(c, width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round }; }

  public static GraphicsPath RoundRect(RectangleF r, float radius) {
    float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
    var p = new GraphicsPath();
    p.AddArc(r.Left, r.Top, d, d, 180, 90);
    p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
    p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
    p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
    p.CloseFigure();
    return p;
  }

  public static Bitmap Render(int px) {
    var bmp = new Bitmap(px, px, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(bmp)) { g.Clear(Color.Transparent); Draw(g, new RectangleF(0, 0, px, px)); }
    return bmp;
  }

  // An .ico of PNG frames (read by Windows Vista and later at every size): the header, one 16-byte entry per frame, then
  // the frames.
  public static void WriteIco(string file) {
    int[] sizes = { 256, 64, 48, 32, 24, 16 };
    var frames = new List<byte[]>();
    foreach (int s in sizes) using (var bmp = Render(s)) using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); frames.Add(ms.ToArray()); }
    using (var w = new BinaryWriter(File.Create(file))) {
      w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)sizes.Length);
      int at = 6 + 16 * sizes.Length;
      for (int i = 0; i < sizes.Length; i++) {
        byte side = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);   // 0 means 256
        w.Write(side); w.Write(side); w.Write((byte)0); w.Write((byte)0);
        w.Write((ushort)1); w.Write((ushort)32); w.Write(frames[i].Length); w.Write(at);
        at += frames[i].Length;
      }
      foreach (var f in frames) w.Write(f);
    }
  }
}

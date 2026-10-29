// The Bifrost Comms window: a console in the Bifrost style. Status rail on the left (each part of the radio chain with a
// light), the terminal on the right (every step, typed out, with a download bar), the actions along the bottom. All of
// it is painted here (RenderTo), so the tests can draw any state into a picture without showing the window.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Threading;
using System.Windows.Forms;

class TerminalForm : Form, IConsole {
  const float TitleH = 46, RailW = 336, FooterH = 74, Pad = 18, LineH = 19, Font13 = 13;
  static readonly Color Signal = CommsLook.Signal;
  static readonly Color Phosphor = Palette.Hex("#A6EBBB"), PhosphorDim = Palette.Hex("#4F7D5E"), Stamp = Palette.Hex("#35503F"),
    OkGreen = Palette.Hex("#4CD873"), Amber = Palette.Hex("#F2A541"), Screen = Palette.Hex("#07090b");

  class Hot { public RectangleF R; public string Key; public Action Click; public bool Enabled = true; }
  class LogLine { public string Time, Text; public Tone Tone; }

  static readonly string[] Banner = {
    " ██████╗ ██╗███████╗██████╗  ██████╗ ███████╗████████╗",
    " ██╔══██╗██║██╔════╝██╔══██╗██╔═══██╗██╔════╝╚══██╔══╝",
    " ██████╔╝██║█████╗  ██████╔╝██║   ██║███████╗   ██║   ",
    " ██╔══██╗██║██╔══╝  ██╔══██╗██║   ██║╚════██║   ██║   ",
    " ██████╔╝██║██║     ██║  ██║╚██████╔╝███████║   ██║   ",
    " ╚═════╝ ╚═╝╚═╝     ╚═╝  ╚═╝ ╚═════╝ ╚══════╝   ╚═╝   ",
  };

  readonly Comms core;
  readonly bool launchOnStart, setupOnStart;
  readonly object gate = new object();
  readonly List<LogLine> lines = new List<LogLine>();
  readonly Queue<LogLine> incoming = new Queue<LogLine>();
  readonly List<Hot> hots = new List<Hot>();
  readonly System.Windows.Forms.Timer frame = new System.Windows.Forms.Timer();
  int typed = int.MaxValue;   // characters of the newest line shown so far
  int scroll;                 // rows scrolled up from the bottom (0 = follow)
  string hover, pressed;
  float S = 1f;
  bool closing, booted;
  Thread monitor;
  DateTime opened = DateTime.Now;

  // the download bar
  string progLabel; long progGot, progTotal; double progRate;
  // a question from the core (Ask), or a line to type in (Input)
  string askText; string[] askChoices; string askAnswer; ManualResetEvent askDone;
  string inputPrompt, inputText = "", inputError; Func<string, string> inputProblem; ManualResetEvent inputDone; bool inputOk; int inputMax = 30;

  public TerminalForm(Comms core, bool launch, bool setup = false) {
    this.core = core; launchOnStart = launch; setupOnStart = setup;
    core.Out = this;
    Text = "Bifrost Comms";
    FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.CenterScreen; KeyPreview = true; BackColor = Palette.Bg;
    SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }
    using (var g = Graphics.FromHwnd(IntPtr.Zero)) S = g.DpiX / 96f;
    ClientSize = Size.Round(new SizeF(1040 * S, 680 * S));
    MinimumSize = Size.Round(new SizeF(1000 * S, 580 * S));
    frame.Interval = 16;
    frame.Tick += delegate { Animate(); };
    Boot();
  }

  public float UiScale { get { return S; } set { S = value; } }

  // The PC's name in the terminal; the tests' pictures use a stand-in.
  public static string Machine = Environment.MachineName.ToLowerInvariant();

  // For the tests' pictures: everything queued shown at once, as after the boot.
  public void ShowAllForPicture() {
    lock (gate) { while (incoming.Count > 0) lines.Add(incoming.Dequeue()); typed = int.MaxValue; }
    booted = true;
  }

  protected override CreateParams CreateParams {
    get {
      var cp = base.CreateParams;
      cp.Style |= 0x00020000 | 0x00080000;   // minimise box, system menu: the taskbar button and Win+arrow keys work
      cp.ClassStyle |= 0x00020000;           // drop shadow
      return cp;
    }
  }

  // ---------------------------------------------------------------------------------------------- start and jobs

  void Boot() {
    foreach (var b in Banner) Queue(b, Tone.Accent);
    Queue("   C O M M S   //   TEAMSPEAK 3  ·  COALITION VON  ·  ARMA REFORGER", Tone.Accent);
    Queue("", Tone.Dim);
    Queue("bifrost comms " + core.Version + "  ::  " + Machine + "  ::  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"), Tone.Dim);
  }

  protected override void OnShown(EventArgs e) {
    base.OnShown(e);
    frame.Start();
    Run(delegate {
      if (!core.Boot(launchOnStart)) return;
      booted = true;
      if (launchOnStart) core.Launch();
      else if (setupOnStart) core.Setup();
      else if (core.Ts == null || !core.PluginCurrent()) Line("press SET UP to install what's missing", Tone.Accent);
      else if (string.IsNullOrEmpty(core.M.Host)) Line("press CHANGE beside TS SERVER to set your unit's TeamSpeak address", Tone.Accent);
      else Line("ready. LAUNCH starts TeamSpeak on " + core.M.ServerName + ", checks the radio plugin, then Reforger.", Tone.Text);
    });
    monitor = new Thread(delegate() {
      while (!closing) {
        Thread.Sleep(2000);
        if (!closing && booted && !core.Busy) try {
          core.Tick();
          string why = core.TakePendingCheck();
          if (why != null) BeginInvokeSafe(delegate { Run(delegate { core.RadioCheck(why); }); });
        } catch (Exception) { }
      }
    }) { IsBackground = true, Name = "monitor" };
    monitor.Start();
  }

  void Run(Action job) {
    if (core.Busy) return;
    core.Busy = true; core.Cancel = false;
    Invalidate();
    var t = new Thread(delegate() {
      try { job(); } catch (Exception e) { Line("error: " + Comms.Short(e), Tone.Bad); } finally { core.Busy = false; Changed(); }
    }) { IsBackground = true, Name = "job" };
    t.Start();
  }

  public void RequestLaunch() {
    BringUp();
    if (booted) Run(core.Launch);
  }

  void BringUp() {
    if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
    Activate();
  }

  protected override void OnFormClosing(FormClosingEventArgs e) {
    closing = true; core.Cancel = true;
    lock (gate) {
      if (askDone != null) { askAnswer = null; askDone.Set(); }
      if (inputDone != null) { inputOk = false; inputDone.Set(); }
    }
    frame.Stop();
    base.OnFormClosing(e);
  }

  // ---------------------------------------------------------------------------------------------- IConsole

  public void Line(string text, Tone tone) { Queue(text, tone); Poke(); }

  void Queue(string text, Tone tone) {
    lock (gate) {
      incoming.Enqueue(new LogLine { Time = DateTime.Now.ToString("HH:mm:ss"), Text = text ?? "", Tone = tone });
    }
  }

  public void Progress(string label, long got, long total, double rate) {
    lock (gate) { progLabel = label; progGot = got; progTotal = total; if (rate > 0 || label == null) progRate = rate; }
    Poke();
  }

  public string Ask(string question, params string[] choices) {
    var done = new ManualResetEvent(false);
    lock (gate) { if (closing) return null; askText = question; askChoices = choices; askAnswer = null; askDone = done; }
    Line("? " + question, Tone.Accent);
    done.WaitOne();
    string a;
    lock (gate) { a = askAnswer; askText = null; askChoices = null; askDone = null; }
    if (a != null) Line("> " + a, Tone.Dim);
    Poke();
    return a;
  }

  public string Input(string prompt, string current, Func<string, string> problem, int maxLength) {
    var done = new ManualResetEvent(false);
    lock (gate) {
      if (closing) return null;
      inputPrompt = prompt; inputText = current ?? ""; inputError = null; inputProblem = problem; inputOk = false; inputDone = done; inputMax = maxLength;
    }
    Line("? " + prompt, Tone.Accent);
    Poke();
    done.WaitOne();
    string r;
    lock (gate) { r = inputOk ? inputText : null; inputPrompt = null; inputDone = null; inputProblem = null; }
    Poke();
    return r;
  }

  public void Restart(string exe, string args) {
    BeginInvokeSafe(delegate {
      Instance.Release();
      bool shell = exe.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase);
      var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, WorkingDirectory = shell ? Path.GetTempPath() : Path.GetDirectoryName(exe) };
      if (shell) { psi.CreateNoWindow = true; psi.WindowStyle = ProcessWindowStyle.Hidden; }
      try { Process.Start(psi); } catch (Exception e) { Line("couldn't start " + Path.GetFileName(exe) + ": " + Comms.Short(e), Tone.Bad); return; }
      Close();
    });
  }

  public void Changed() { Poke(); }

  void Poke() { BeginInvokeSafe(delegate { Invalidate(); }); }

  void BeginInvokeSafe(Action a) {
    try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); } catch (InvalidOperationException) { }   // also ObjectDisposedException: the window is gone
  }

  // ---------------------------------------------------------------------------------------------- animation

  // Lines type out at about 1000 characters a second; a backlog over 40 lines jumps straight to the end.
  void Animate() {
    lock (gate) {
      bool more = false;
      if (lines.Count > 0 && typed < lines[lines.Count - 1].Text.Length) { typed += 18; more = true; }
      else if (incoming.Count > 0) {
        if (incoming.Count > 40) { while (incoming.Count > 0) lines.Add(incoming.Dequeue()); typed = int.MaxValue; }
        else { lines.Add(incoming.Dequeue()); typed = 0; }
        more = true;
      }
      while (lines.Count > 800) lines.RemoveAt(0);
      if (more && scroll > 0) scroll++;   // reading back: keep the same lines in view
    }
    Invalidate();
  }

  // ---------------------------------------------------------------------------------------------- input

  protected override void OnKeyPress(KeyPressEventArgs e) {
    lock (gate) {
      if (inputDone == null) return;
      if (e.KeyChar == '\b') { if (inputText.Length > 0) inputText = inputText.Substring(0, inputText.Length - 1); }
      else if (e.KeyChar == '\r') { SubmitInput(); }
      else if (e.KeyChar == 27) { inputOk = false; inputDone.Set(); }
      else if (!char.IsControl(e.KeyChar) && inputText.Length < inputMax) inputText += e.KeyChar;
      inputError = null;
    }
    e.Handled = true;
    Invalidate();
  }

  void SubmitInput() {
    string p = inputProblem == null ? null : inputProblem(inputText);
    if (p != null) { inputError = p; return; }
    inputOk = true; inputDone.Set();
  }

  protected override void OnKeyDown(KeyEventArgs e) {
    lock (gate) {
      if (askDone != null && askChoices != null) {
        int n = -1;
        if (e.KeyCode == Keys.Enter) n = 0;
        else if (e.KeyCode == Keys.Escape) n = askChoices.Length - 1;
        else if (e.KeyCode >= Keys.D1 && e.KeyCode <= Keys.D9) n = e.KeyCode - Keys.D1;
        if (n >= 0 && n < askChoices.Length) { askAnswer = askChoices[n]; askDone.Set(); e.Handled = true; }
      }
    }
    if (e.Control && e.KeyCode == Keys.C && inputDone == null) CopyLog();
    if (e.Control && e.KeyCode == Keys.V) Paste();
    base.OnKeyDown(e);
  }

  // CTRL+V into the input line: the clipboard's first line, without control characters (an address copied from chat).
  void Paste() {
    string clip = null;
    try { if (Clipboard.ContainsText()) clip = Clipboard.GetText(); } catch (Exception) { }
    if (string.IsNullOrEmpty(clip)) return;
    var sb = new System.Text.StringBuilder();
    foreach (char c in clip.Trim()) { if (c == '\r' || c == '\n') break; if (!char.IsControl(c)) sb.Append(c); }
    lock (gate) {
      if (inputDone == null) return;
      inputText = (inputText + sb.ToString());
      if (inputText.Length > inputMax) inputText = inputText.Substring(0, inputMax);
      inputError = null;
    }
    Invalidate();
  }

  protected override void OnMouseWheel(MouseEventArgs e) {
    scroll = Math.Max(0, scroll + (e.Delta > 0 ? 3 : -3));
    Invalidate();
  }

  protected override void OnMouseMove(MouseEventArgs e) {
    string h = HotAt(e.Location);
    if (h != hover) { hover = h; Cursor = h != null ? Cursors.Hand : Cursors.Default; Invalidate(); }
  }

  protected override void OnMouseLeave(EventArgs e) { hover = null; Invalidate(); }
  protected override void OnMouseDown(MouseEventArgs e) { pressed = HotAt(e.Location); Invalidate(); }

  protected override void OnMouseUp(MouseEventArgs e) {
    string h = HotAt(e.Location);
    if (h != null && h == pressed) foreach (var x in hots.ToArray()) if (x.Key == h && x.Enabled && x.Click != null) { x.Click(); break; }
    pressed = null; Invalidate();
  }

  string HotAt(Point p) {
    foreach (var h in hots) if (h.Enabled && h.R.Contains(p)) return h.Key;
    return null;
  }

  void CopyLog() {
    var sb = new System.Text.StringBuilder();
    lock (gate) foreach (var l in lines) sb.Append(l.Time).Append("  ").AppendLine(l.Text);
    try { Clipboard.SetText(sb.ToString()); Line("log copied to the clipboard", Tone.Dim); } catch (Exception) { }
  }

  [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ReleaseCapture();
  [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

  protected override void WndProc(ref Message m) {
    if (m.Msg == Instance.LaunchMessage && Instance.LaunchMessage != 0) { if (m.WParam.ToInt64() == 1) RequestLaunch(); else BringUp(); return; }
    if (m.Msg == 0x84) {   // WM_NCHITTEST: edges resize, the title bar drags
      base.WndProc(ref m);
      var p = PointToClient(new Point((short)(m.LParam.ToInt64() & 0xffff), (short)((m.LParam.ToInt64() >> 16) & 0xffff)));
      int edge = (int)(6 * S);
      bool l = p.X < edge, r = p.X >= ClientSize.Width - edge, t = p.Y < edge, b = p.Y >= ClientSize.Height - edge;
      if (t && l) m.Result = (IntPtr)13; else if (t && r) m.Result = (IntPtr)14; else if (b && l) m.Result = (IntPtr)16; else if (b && r) m.Result = (IntPtr)17;
      else if (l) m.Result = (IntPtr)10; else if (r) m.Result = (IntPtr)11; else if (t) m.Result = (IntPtr)12; else if (b) m.Result = (IntPtr)15;
      else if (p.Y < TitleH * S && HotAt(p) == null) m.Result = (IntPtr)2;
      return;
    }
    base.WndProc(ref m);
  }

  // ---------------------------------------------------------------------------------------------- painting

  protected override void OnPaint(PaintEventArgs e) { RenderTo(e.Graphics, ClientSize); }

  public void RenderTo(Graphics g, Size size) {
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    hots.Clear();
    float w = size.Width, h = size.Height;
    using (var b = new SolidBrush(Palette.Bg)) g.FillRectangle(b, 0, 0, w, h);
    PaintTitle(g, w);
    PaintRail(g, new RectangleF(0, TitleH * S, RailW * S, h - (TitleH + FooterH) * S));
    PaintTerminal(g, new RectangleF(RailW * S, TitleH * S, w - RailW * S, h - (TitleH + FooterH) * S));
    PaintFooter(g, new RectangleF(0, h - FooterH * S, w, FooterH * S));
    using (var p = new Pen(Palette.Edge, 1)) g.DrawRectangle(p, 0, 0, w - 1, h - 1);
  }

  void PaintTitle(Graphics g, float w) {
    float th = TitleH * S;
    using (var b = new SolidBrush(Palette.Surface)) g.FillRectangle(b, 0, 0, w, th);
    using (var p = new Pen(Palette.Alpha(Signal, 0.55), Math.Max(1, S))) g.DrawLine(p, 0, th - 1, w, th - 1);
    float icon = 28 * S;
    Badge.Draw(g, new RectangleF(Pad * S * 0.8f, (th - icon) / 2, icon, icon));
    float x = Pad * S * 0.8f + icon + 12 * S;
    var title = Fonts.Get(Fonts.UiBold, 17 * S);
    Txt(g, "BIFROST COMMS", title, Palette.Fg, x, (th - 17 * S * 1.2f) / 2);
    x += Measure(g, "BIFROST COMMS", title) + 14 * S;
    var mono = Fonts.Get(Fonts.Mono, 11.5f * S);
    Txt(g, "// TEAMSPEAK 3 + COALITION VON", mono, PhosphorDim, x, (th - 11.5f * S * 1.25f) / 2);

    float bw = 46 * S;
    var close = new RectangleF(w - bw, 0, bw, th - 1);
    var min = new RectangleF(w - 2 * bw, 0, bw, th - 1);
    WindowButton(g, min, "min", delegate { WindowState = FormWindowState.Minimized; });
    WindowButton(g, close, "close", Close);
    string ver = "v" + core.Version;
    Txt(g, ver, mono, Palette.Dim, min.X - Measure(g, ver, mono) - 14 * S, (th - 11.5f * S * 1.25f) / 2);
  }

  void WindowButton(Graphics g, RectangleF r, string key, Action click) {
    hots.Add(new Hot { R = r, Key = key, Click = click });
    if (hover == key) using (var b = new SolidBrush(key == "close" ? Palette.Red : Palette.Rule)) g.FillRectangle(b, r);
    float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, d = 5 * S;
    using (var p = new Pen(hover == key ? Palette.Fg : Palette.Muted, 1.3f * S)) {
      if (key == "close") { g.DrawLine(p, cx - d, cy - d, cx + d, cy + d); g.DrawLine(p, cx - d, cy + d, cx + d, cy - d); }
      else g.DrawLine(p, cx - d, cy, cx + d, cy);
    }
  }

  void PaintRail(Graphics g, RectangleF r) {
    using (var b = new SolidBrush(Palette.Surface)) g.FillRectangle(b, r);
    using (var p = new Pen(Palette.Rule, 1)) g.DrawLine(p, r.Right - 1, r.Y, r.Right - 1, r.Bottom);
    float x = r.X + Pad * S, y = r.Y + 16 * S, cw = r.Width - 2 * Pad * S;
    var head = Fonts.Get(Fonts.Mono, 11 * S);
    Txt(g, "SYSTEM STATUS", head, Amber, x, y);
    string spin = core.Busy ? "|/-\\".Substring((int)((DateTime.Now - opened).TotalMilliseconds / 120) % 4, 1) + " WORKING" : "● LIVE";
    float sw = Measure(g, spin, head);
    Txt(g, spin, head, core.Busy ? Amber : OkGreen, x + cw - sw, y);
    y += 26 * S;

    Status st;
    lock (core.Lock) st = core.St.Clone();
    float rowH = 49 * S;
    Row(g, x, y, cw, "UPLINK", st.Uplink, st.UplinkDetail, st.UplinkTone); y += rowH;
    Row(g, x, y, cw, "TEAMSPEAK 3", st.Ts, st.TsDetail, st.TsTone); y += rowH;
    Row(g, x, y, cw, "RADIO PLUGIN", st.Plugin, st.PluginDetail, st.PluginTone); y += rowH;
    bool canChange = !core.M.ServerFromManifest;   // a server the unit's manifest names isn't the player's to change
    Row(g, x, y, cw, "TS SERVER", st.Server, st.ServerDetail, st.ServerTone, canChange ? 82 * S : 0);
    if (canChange) LinkButton(g, new RectangleF(x + cw - 74 * S, y + 17 * S, 74 * S, 22 * S), "server", "CHANGE", booted && !core.Busy, delegate { Run(delegate { core.EnsureServer(true); }); });
    y += rowH;
    Row(g, x, y, cw, "TS CONNECTION", st.TsLink, st.TsLinkDetail, st.TsLinkTone); y += rowH;
    Row(g, x, y, cw, "ARMA REFORGER", st.Game, st.GameDetail, st.GameTone); y += rowH;
    Row(g, x, y, cw, "RADIO LINK", st.Link.Text, st.Link.Detail, st.Link.Tone); y += rowH;
    Row(g, x, y, cw, "UPDATES", st.Updates, st.UpdatesDetail, st.UpdatesTone); y += rowH;

    // the callsign, at the bottom of the rail
    float by = r.Bottom - 58 * S;
    if (by > y) {
      using (var p = new Pen(Palette.Rule, 1)) g.DrawLine(p, x, by, x + cw, by);
      var label = Fonts.Get(Fonts.Mono, 10.5f * S);
      Txt(g, "CALLSIGN", label, Palette.Dim, x, by + 12 * S);
      string cs = core.Settings.Callsign ?? "not set";
      Txt(g, Ellipsis(g, cs, Fonts.Get(Fonts.Mono, 14 * S), cw - 90 * S), Fonts.Get(Fonts.Mono, 14 * S), core.Settings.Callsign == null ? Amber : Phosphor, x, by + 28 * S);
      var change = new RectangleF(x + cw - 74 * S, by + 24 * S, 74 * S, 24 * S);
      LinkButton(g, change, "callsign", "CHANGE", !core.Busy, delegate { Run(delegate { core.EnsureCallsign(true); }); });
    }
  }

  // reserve: room kept free at the right of the detail line (for a CHANGE button)
  void Row(Graphics g, float x, float y, float w, string label, string value, string detail, Tone tone, float reserve = 0) {
    Color c = ToneColor(tone);
    float led = 8 * S;
    using (var glow = new SolidBrush(Palette.Alpha(c, 0.22))) g.FillEllipse(glow, x - 3 * S, y + 2 * S, led + 6 * S, led + 6 * S);
    using (var b = new SolidBrush(c)) g.FillEllipse(b, x, y + 5 * S, led, led);
    var lf = Fonts.Get(Fonts.Mono, 10.5f * S);
    float lx = x + led + 12 * S;
    Txt(g, label, lf, Palette.Dim, lx, y + 2 * S);
    var vf = Fonts.Get(Fonts.Mono, 12.5f * S);
    string v = Ellipsis(g, value ?? "", vf, w - (lx - x) - Measure(g, label, lf) - 10 * S);
    Txt(g, v, vf, c, x + w - Measure(g, v, vf), y + 1 * S);
    var df = Fonts.Get(Fonts.Ui, 12.5f * S);
    Txt(g, Ellipsis(g, detail ?? "", df, w - (lx - x) - reserve), df, Palette.Muted, lx, y + 20 * S);
  }

  void PaintTerminal(Graphics g, RectangleF r) {
    using (var b = new SolidBrush(Screen)) g.FillRectangle(b, r);
    // the header strip
    float hh = 30 * S;
    using (var b = new SolidBrush(Palette.Alpha(Palette.Surface, 0.6))) g.FillRectangle(b, r.X, r.Y, r.Width, hh);
    using (var p = new Pen(Palette.Rule, 1)) g.DrawLine(p, r.X, r.Y + hh, r.Right, r.Y + hh);
    var small = Fonts.Get(Fonts.Mono, 11 * S);
    Txt(g, "TERMINAL  //  comms@" + Machine, small, PhosphorDim, r.X + Pad * S, r.Y + 8 * S);
    LinkButton(g, new RectangleF(r.Right - 96 * S, r.Y + 3 * S, 86 * S, 24 * S), "copy", "COPY LOG", true, CopyLog);

    var font = Fonts.Get(Fonts.Mono, Font13 * S);
    float cw = Measure(g, "MMMMMMMMMM", font) / 10f;
    float lh = LineH * S;
    float stampW = cw * 9;
    float left = r.X + Pad * S, top = r.Y + hh + 10 * S;
    float bottom = r.Bottom - 12 * S;

    // pinned at the bottom: the prompt, and the download bar above it while one runs
    string label; long got, total; double rate; string prompt, text, error;
    lock (gate) { label = progLabel; got = progGot; total = progTotal; rate = progRate; prompt = inputPrompt; text = inputText; error = inputError; }
    float promptY = bottom - lh;
    PaintPrompt(g, left, promptY, r.Right - Pad * S, font, cw, prompt, text, error);
    float logBottom = promptY - 6 * S;
    if (label != null) {
      float barY = logBottom - 2 * lh - 4 * S;
      PaintBar(g, left, barY, r.Right - Pad * S - left, font, cw, label, got, total, rate);
      logBottom = barY - 8 * S;
    }

    // the log, wrapped to the width, newest at the bottom
    int cols = Math.Max(20, (int)((r.Right - Pad * S - left - stampW) / cw));
    var rows = new List<KeyValuePair<LogLine, string>>();
    lock (gate) {
      for (int i = 0; i < lines.Count; i++) {
        var l = lines[i];
        string t = l.Text;
        if (i == lines.Count - 1 && typed < t.Length) t = t.Substring(0, Math.Max(0, typed)) + "█";
        bool first = true;
        foreach (var part in Wrap(t, cols)) { rows.Add(new KeyValuePair<LogLine, string>(first ? l : null, part)); first = false; }
        if (t.Length == 0) rows.Add(new KeyValuePair<LogLine, string>(l, ""));
      }
    }
    // the banner's block letters sit on tighter rows so they join up
    float bannerH = (float)Math.Ceiling(font.GetHeight(g));
    Func<int, float> rowH = delegate(int i) { return IsBanner(rows[i].Value) ? bannerH : lh; };
    scroll = Math.Min(scroll, Math.Max(0, rows.Count - 1));
    int end = rows.Count - scroll, start = end;
    float used = 0;
    while (start > 0 && used + rowH(start - 1) <= logBottom - top) { used += rowH(start - 1); start--; }
    var state = g.Save();
    g.SetClip(new RectangleF(r.X, top, r.Width, logBottom - top));
    float y = logBottom - used;
    for (int i = start; i < end; i++) {
      var row = rows[i];
      LogLine l = row.Key;
      Tone tone = l != null ? l.Tone : FindTone(rows, i);
      bool flush = tone == Tone.Accent && (l == null || l.Text.StartsWith(" "));
      if (l != null && l.Text.Length > 0 && !flush) Txt(g, l.Time, font, Stamp, left, y);
      Glow(g, row.Value, font, ToneColor(tone), flush ? left : left + stampW, y);
      y += rowH(i);
    }
    g.Restore(state);
    if (scroll > 0) Txt(g, "▲ " + scroll + " more below (scroll down)", small, Amber, r.Right - Pad * S - Measure(g, "▲ " + scroll + " more below (scroll down)", small), r.Y + hh + 6 * S);

    // CRT: scanlines and a soft vignette over the whole screen
    using (var p = new Pen(Color.FromArgb(46, 0, 0, 0), 1))
      for (float sy = r.Y + hh + 1; sy < r.Bottom; sy += 3 * S) g.DrawLine(p, r.X, sy, r.Right, sy);
    using (var path = new GraphicsPath()) {
      path.AddRectangle(new RectangleF(r.X - r.Width * 0.25f, r.Y - r.Height * 0.25f, r.Width * 1.5f, r.Height * 1.5f));
      using (var br = new PathGradientBrush(path) { CenterColor = Color.FromArgb(0, 0, 0, 0), SurroundColors = new[] { Color.FromArgb(150, 0, 0, 0) } }) {
        br.FocusScales = new PointF(0.55f, 0.45f);
        var clip = g.Save();
        g.SetClip(r);
        g.FillRectangle(br, r);
        g.Restore(clip);
      }
    }
  }

  static bool IsBanner(string s) { return s.IndexOf('█') >= 0 || s.IndexOf('╚') >= 0; }

  static Tone FindTone(List<KeyValuePair<LogLine, string>> rows, int i) {
    for (int j = i; j >= 0; j--) if (rows[j].Key != null) return rows[j].Key.Tone;
    return Tone.Text;
  }

  void PaintPrompt(Graphics g, float x, float y, float right, Font font, float cw, string prompt, string text, string error) {
    bool blink = (DateTime.Now - opened).TotalMilliseconds % 1060 < 530;
    string ps = "bifrost//comms ▸ ";
    Txt(g, ps, font, Amber, x, y);
    float tx = x + ps.Length * cw;
    if (prompt != null) {
      string hint = error != null ? "✗ " + error : "ENTER saves · ESC cancels";
      var small = Fonts.Get(Fonts.Mono, 11 * Font13 / 13f * S);
      float hintW = Measure(g, hint, small);
      // a long line shows its end, where the typing is
      int room = Math.Max(8, (int)((right - hintW - 16 * S - tx) / cw) - 1);
      string shown = text.Length > room ? "…" + text.Substring(text.Length - room + 1) : text;
      Glow(g, shown + (blink ? "█" : " "), font, Palette.Fg, tx, y);
      Txt(g, hint, small, error != null ? Palette.Red : PhosphorDim, right - hintW, y + 2 * S);
    } else if (core.Busy) {
      Txt(g, "working" + new string('.', 1 + (int)((DateTime.Now - opened).TotalMilliseconds / 400) % 3), font, PhosphorDim, tx, y);
    } else if (blink) Glow(g, "█", font, Phosphor, tx, y);
  }

  // [████████████░░░░░░░░]  61%  68.9 / 107.8 MB  9.4 MB/s  ETA 4 s, drawn as cells so it looks right in any font
  void PaintBar(Graphics g, float x, float y, float w, Font font, float cw, string label, long got, long total, double rate) {
    double frac = total > 0 ? Math.Min(1, got / (double)total) : 0;
    string head = "▼ " + label;
    Glow(g, head, font, Amber, x, y);
    float lh = LineH * S;
    string pct = total > 0 ? ((int)(frac * 100)).ToString().PadLeft(3) + "%" : "  …";
    string nums = pct + "  " + Net.Size(got) + (total > 0 ? " / " + Net.Size(total) : "") + (rate > 0 ? "  " + Net.Size((long)rate) + "/s" : "");
    if (rate > 0 && total > 0) { double eta = (total - got) / rate; nums += "  ETA " + (eta >= 60 ? ((int)(eta / 60)) + " min" : ((int)Math.Ceiling(eta)) + " s"); }
    float numsW = Measure(g, nums, font);
    float barW = Math.Max(120 * S, w - numsW - 16 * S);
    int cells = Math.Max(10, (int)(barW / (cw * 0.8f)));
    float cell = barW / cells;
    int lit = (int)Math.Round(frac * cells);
    float by = y + lh + 3 * S, bh = lh - 7 * S;
    using (var on = new SolidBrush(OkGreen)) using (var off = new SolidBrush(Palette.Alpha(OkGreen, 0.13))) using (var glow = new SolidBrush(Palette.Alpha(OkGreen, 0.18))) {
      if (lit > 0) g.FillRectangle(glow, x - 2 * S, by - 2 * S, lit * cell + 2 * S, bh + 4 * S);
      for (int i = 0; i < cells; i++) g.FillRectangle(i < lit ? on : off, x + i * cell, by, cell - Math.Max(1, 2 * S), bh);
    }
    Glow(g, nums, font, Phosphor, x + barW + 16 * S, y + lh);
  }

  void PaintFooter(Graphics g, RectangleF r) {
    using (var b = new SolidBrush(Palette.Surface)) g.FillRectangle(b, r);
    using (var p = new Pen(Palette.Rule, 1)) g.DrawLine(p, r.X, r.Y, r.Right, r.Y);
    float x = r.X + Pad * S, bh = 40 * S, y = r.Y + (r.Height - bh) / 2;
    string ask; string[] choices; bool input;
    lock (gate) { ask = askText; choices = askChoices; input = inputDone != null; }

    if (ask != null && choices != null) {
      var qf = Fonts.Get(Fonts.Ui, 14 * S);
      float right = r.Right - Pad * S;
      float bx = right;
      for (int i = choices.Length - 1; i >= 0; i--) {
        string c = choices[i];
        float bw = Measure(g, c, Fonts.Get(Fonts.UiBold, 14 * S)) + 40 * S;
        bx -= bw;
        int n = i;
        Button(g, new RectangleF(bx, y, bw, bh), "ask" + i, c, i == 0, true, delegate { lock (gate) { if (askDone != null) { askAnswer = choices[n]; askDone.Set(); } } });
        bx -= 10 * S;
      }
      Txt(g, Ellipsis(g, ask, qf, bx - x - 12 * S), qf, Palette.Fg, x, y + (bh - 14 * S * 1.25f) / 2);
      return;
    }
    if (input) {
      var qf = Fonts.Get(Fonts.Ui, 14 * S);
      Txt(g, "Type your answer in the terminal (CTRL+V pastes), then press ENTER.", qf, Palette.Soft, x, y + (bh - 14 * S * 1.25f) / 2);
      float bw = 120 * S;
      Button(g, new RectangleF(r.Right - Pad * S - bw, y, bw, bh), "save", "SAVE", true, true, delegate { lock (gate) { if (inputDone != null) SubmitInput(); } });
      Button(g, new RectangleF(r.Right - Pad * S - 2 * bw - 10 * S, y, bw, bh), "cancel-input", "CANCEL", false, true, delegate { lock (gate) { if (inputDone != null) { inputOk = false; inputDone.Set(); } } });
      return;
    }

    bool idle = !core.Busy && booted;
    float launchW = 290 * S;
    Button(g, new RectangleF(x, y, launchW, bh), "launch", "▶  LAUNCH REFORGER + RADIO", true, idle, delegate { Run(core.Launch); });
    x += launchW + 12 * S;
    float sw = 166 * S;
    Button(g, new RectangleF(x, y, sw, bh), "setup", "SET UP / REPAIR", false, idle, delegate { Run(core.Setup); });
    x += sw + 10 * S;
    float cw = 190 * S;
    Button(g, new RectangleF(x, y, cw, bh), "connect", "CONNECT TEAMSPEAK", false, idle && core.Ts != null, delegate { Run(core.ConnectTeamSpeak); });
    x += cw + 10 * S;
    float rw = 150 * S;
    Button(g, new RectangleF(x, y, rw, bh), "check", "RADIO CHECK", false, idle, delegate { Run(delegate { core.RadioCheck(null); }); });
    bool downloading;
    lock (gate) downloading = progLabel != null;
    if (core.Busy && downloading)
      LinkButton(g, new RectangleF(r.Right - Pad * S - 90 * S, y + 8 * S, 90 * S, 24 * S), "cancel", "CANCEL", true, delegate { core.Cancel = true; });
    else
      LinkButton(g, new RectangleF(r.Right - Pad * S - 90 * S, y + 8 * S, 90 * S, 24 * S), "remove", "REMOVE", idle && core.RunningFromHome, delegate { Run(core.Remove); });
  }

  void Button(Graphics g, RectangleF r, string key, string text, bool primary, bool enabled, Action click) {
    hots.Add(new Hot { R = r, Key = key, Click = click, Enabled = enabled });
    bool hot = enabled && hover == key, down = hot && pressed == key;
    var font = Fonts.Get(Fonts.UiBold, 14 * S);
    using (var path = Badge.RoundRect(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), 6 * S)) {
      if (primary) {
        Color fill = !enabled ? Palette.Track : down ? Palette.Shade(Palette.Accent, -0.14) : hot ? Palette.Shade(Palette.Accent, 0.16) : Palette.Accent;
        if (enabled) using (var glow = new SolidBrush(Palette.Alpha(Palette.Accent, hot ? 0.28 : 0.14)))
          using (var gp = Badge.RoundRect(new RectangleF(r.X - 3 * S, r.Y - 3 * S, r.Width + 6 * S, r.Height + 6 * S), 9 * S)) g.FillPath(glow, gp);
        using (var b = new SolidBrush(fill)) g.FillPath(b, path);
        CenterTxt(g, text, font, enabled ? Palette.OnAccent : Palette.Dim, r);
      } else {
        using (var b = new SolidBrush(hot ? Palette.Alpha(Signal, 0.10) : Palette.Field)) g.FillPath(b, path);
        using (var p = new Pen(!enabled ? Palette.Rule : hot ? Signal : Palette.Edge, 1)) g.DrawPath(p, path);
        CenterTxt(g, text, font, !enabled ? Palette.Faint : hot ? Signal : Palette.Soft, r);
      }
    }
  }

  void LinkButton(Graphics g, RectangleF r, string key, string text, bool enabled, Action click) {
    hots.Add(new Hot { R = r, Key = key, Click = click, Enabled = enabled });
    var f = Fonts.Get(Fonts.Mono, 11 * S);
    bool hot = enabled && hover == key;
    if (hot) using (var b = new SolidBrush(Palette.Alpha(Signal, 0.10))) using (var path = Badge.RoundRect(r, 4 * S)) g.FillPath(b, path);
    CenterTxt(g, "[ " + text + " ]", f, !enabled ? Palette.Faint : hot ? Signal : PhosphorDim, r);
  }

  // ---------------------------------------------------------------------------------------------- text helpers

  static Color ToneColor(Tone t) {
    switch (t) {
      case Tone.Ok: return OkGreen;
      case Tone.Warn: return Amber;
      case Tone.Bad: return Palette.Red;
      case Tone.Accent: return Amber;
      case Tone.Dim: return PhosphorDim;
      default: return Phosphor;
    }
  }

  static readonly StringFormat Typo = MakeTypo();
  static StringFormat MakeTypo() {
    var f = (StringFormat)StringFormat.GenericTypographic.Clone();
    f.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
    return f;
  }

  static void Txt(Graphics g, string s, Font f, Color c, float x, float y) {
    if (string.IsNullOrEmpty(s)) return;
    using (var b = new SolidBrush(c)) g.DrawString(s, f, b, x, y, Typo);
  }

  // Phosphor text: a faint halo under the glyphs.
  static void Glow(Graphics g, string s, Font f, Color c, float x, float y) {
    if (string.IsNullOrEmpty(s)) return;
    using (var halo = new SolidBrush(Color.FromArgb(38, c))) {
      g.DrawString(s, f, halo, x - 0.8f, y, Typo); g.DrawString(s, f, halo, x + 0.8f, y, Typo);
    }
    Txt(g, s, f, c, x, y);
  }

  static void CenterTxt(Graphics g, string s, Font f, Color c, RectangleF r) {
    var size = g.MeasureString(s, f, 100000, Typo);
    Txt(g, s, f, c, r.X + (r.Width - size.Width) / 2, r.Y + (r.Height - size.Height) / 2);
  }

  static float Measure(Graphics g, string s, Font f) { return string.IsNullOrEmpty(s) ? 0 : g.MeasureString(s, f, 100000, Typo).Width; }

  static string Ellipsis(Graphics g, string s, Font f, float max) {
    if (Measure(g, s, f) <= max) return s;
    for (int n = s.Length - 1; n > 0; n--) { string t = s.Substring(0, n).TrimEnd() + "…"; if (Measure(g, t, f) <= max) return t; }
    return "…";
  }

  // Word wrap at a column count; words longer than a line are cut.
  public static List<string> Wrap(string s, int cols) {
    var list = new List<string>();
    if (s.Length <= cols) { if (s.Length > 0) list.Add(s); return list; }
    int i = 0;
    bool first = true;
    while (i < s.Length) {
      int width = first ? cols : cols - 2;
      if (s.Length - i <= width) { list.Add((first ? "" : "  ") + s.Substring(i)); break; }
      int cut = s.LastIndexOf(' ', i + width, width);
      if (cut <= i) cut = i + width;
      list.Add((first ? "" : "  ") + s.Substring(i, cut - i));
      i = cut;
      while (i < s.Length && s[i] == ' ') i++;
      first = false;
    }
    return list;
  }

}

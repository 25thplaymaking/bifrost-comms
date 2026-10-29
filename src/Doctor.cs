// RADIO CHECK: why the radio link won't come up on this PC, and what Bifrost Comms can fix by itself. Gather reads the PC
// (the radio files, the game's command line and Steam's launch options for it, TeamSpeak over ClientQuery); Diagnose
// decides from those facts alone, so the tests can hand it any situation. Comms runs it from the RADIO CHECK button, and
// by itself when the link stays stuck (Comms.Tick).
//
// The chain it checks: the plugin only reads Documents\My Games\ArmaReforger\profile (the Documents known folder) and only
// serves TeamSpeak's selected tab; the game writes its side of the radio files to its own profile folder, which -profile
// moves; and TeamSpeak loads the plugin only from its own config folder, on TeamSpeak 3.6 or newer, with Microsoft's
// Visual C++ runtime present.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

enum Fix { None, LinkProfile, PluginIntoRunningTs, UpdateTeamSpeak, RestartTeamSpeak, VcRuntime, FolderAccess, Connect }

class Finding {
  public string Problem, Advice;     // what's wrong, in a member's words; what to do about it
  public Tone Tone = Tone.Bad;       // Bad: something's broken; Accent: a hint, nothing broken
  public Fix Fix;                    // what the fix button does (None: nothing Bifrost Comms can do itself)
  public string FixLabel, Question;  // the button, and the short question beside it
  public string Target;              // the folder the fix works on
}

class Facts {
  public string ProfileDir;                       // where the plugin reads: Documents\My Games\ArmaReforger\profile
  public bool TsInstalled, TsRunning, PluginInstalled, PluginFresh, RuntimeMissing, FolderAccessOn;
  public string TsVersion, TsDir, RunningTsDir;
  public bool GameRunning;
  public string GameOptions;                      // the game's command line after the program; null when it can't be read
  public List<string> LaunchOptions = new List<string>();   // Steam's launch options for Reforger, one per Steam account
  public string GameProfileDir;                   // the folder -profile points the game at; null without -profile
  public double HeartbeatAge = -1;                // VONHeartbeat.json in ProfileDir; -1: none
  public string ElsewhereDir;                     // another profile folder the game is writing its heartbeat to
  public ServerData Sd;
  public List<TsConnection> Tabs;                 // TeamSpeak's connected tabs; null when ClientQuery can't tell
  public Func<TsConnection, bool> IsUnitServer;   // whether a tab is on the unit's server (a name is looked up); null: compare with the manifest's address
}

static class Doctor {
  public const string MinTeamSpeak = "3.6.0";   // the plugin is built for TeamSpeak's plugin API 26: 3.6 and newer
  public const string VcRuntimeUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe";
  public const string FolderAccessPage = "windowsdefender://RansomwareProtection";
  const double FreshSeconds = 10;   // the game rewrites its heartbeat every second while it's on a radio server

  public static bool HeartbeatFresh(double age) { return age >= 0 && age <= FreshSeconds; }

  // ---------------------------------------------------------------------------------------------- deciding

  // Problems first, in the order they have to be fixed; hints (Tone.Accent) when nothing is broken but the radio waits on
  // the member. Empty: nothing wrong that this PC can show.
  public static List<Finding> Diagnose(Facts f, Manifest m) {
    var list = new List<Finding>();
    if (!f.TsInstalled) {
      list.Add(new Finding { Problem = "TeamSpeak 3 isn't installed.", Advice = "Press SET UP / REPAIR." });
      return list;
    }
    bool oldTs = Ver.Valid(f.TsVersion) && Ver.Compare(f.TsVersion, MinTeamSpeak) < 0;
    if (oldTs)
      list.Add(new Finding { Problem = "TeamSpeak " + f.TsVersion + " is too old for the Coalition plugin (it needs 3.6 or newer).",
        Advice = "UPDATE TEAMSPEAK installs TeamSpeak " + m.TsVersion + " (your bookmarks and settings stay).",
        Fix = Fix.UpdateTeamSpeak, FixLabel = "UPDATE TEAMSPEAK", Question = "Update TeamSpeak to " + m.TsVersion + " now?" });
    if (!f.PluginInstalled) list.Add(new Finding { Problem = "The Coalition plugin isn't installed in TeamSpeak.", Advice = "Press SET UP / REPAIR." });
    bool otherTs = f.TsRunning && f.RunningTsDir != null && f.TsDir != null && !SameDir(f.RunningTsDir, f.TsDir);
    if (otherTs)
      list.Add(new Finding { Problem = "The TeamSpeak that's running (" + f.RunningTsDir + ") isn't the one the plugin is installed in (" + f.TsDir + ").",
        Advice = "INSTALL IT THERE puts the plugin into the running TeamSpeak and restarts it.",
        Fix = Fix.PluginIntoRunningTs, FixLabel = "INSTALL IT THERE", Question = "Install the plugin into the running TeamSpeak?", Target = f.RunningTsDir });
    else if (f.TsRunning && f.PluginInstalled && !f.PluginFresh && !oldTs) {
      if (f.RuntimeMissing)
        list.Add(new Finding { Problem = "Microsoft's Visual C++ runtime is missing, so TeamSpeak can't load the Coalition plugin.",
          Advice = "GET THE RUNTIME downloads Microsoft's installer (vc_redist.x64.exe): run it, then restart TeamSpeak.",
          Fix = Fix.VcRuntime, FixLabel = "GET THE RUNTIME", Question = "Download Microsoft's Visual C++ runtime?" });
      else
        list.Add(new Finding { Problem = "TeamSpeak is running but hasn't loaded the Coalition plugin.",
          Advice = "A restart loads it. If it still doesn't: in TeamSpeak, Tools > Options > Addons > Plugins, tick \"" + m.PluginName + "\".",
          Fix = Fix.RestartTeamSpeak, FixLabel = "RESTART TEAMSPEAK", Question = "Restart TeamSpeak so it loads the plugin?" });
    }
    if (!f.GameRunning) return list;

    // The game: is it writing its radio files where the plugin reads them?
    if (!HeartbeatFresh(f.HeartbeatAge)) {
      if (f.GameProfileDir != null && !SameDir(f.GameProfileDir, f.ProfileDir))
        list.Add(LinkProfile("Reforger is started with -profile, so it keeps its radio files in " + f.GameProfileDir +
                             ", but the Coalition plugin only looks in " + f.ProfileDir + ".", f.GameProfileDir));
      else if (f.ElsewhereDir != null)
        list.Add(LinkProfile("Reforger writes its radio files to " + f.ElsewhereDir + ", but the Coalition plugin only looks in " + f.ProfileDir + ".", f.ElsewhereDir));
      else if (f.FolderAccessOn)
        list.Add(new Finding { Problem = "Windows' Controlled folder access is on: it can stop Reforger or TeamSpeak writing the radio files in Documents.",
          Advice = "In Windows Security > Ransomware protection > Allow an app through, add ArmaReforgerSteam.exe and ts3client_win64.exe.",
          Fix = Fix.FolderAccess, FixLabel = "OPEN WINDOWS SECURITY", Question = "Open Windows Security's Controlled folder access settings?" });
      else
        list.Add(new Finding { Tone = Tone.Accent, Problem = "No radio signal from the game yet.", Advice = "Join a Coalition radio server and spawn in: the radio links once you're in." });
      return list;
    }
    if (f.Sd == null || !f.Sd.InGame) {
      list.Add(new Finding { Tone = Tone.Accent, Problem = "You're on a radio server but haven't spawned in yet.", Advice = "The radio links when you spawn." });
      return list;
    }

    // In game: is TeamSpeak's selected tab the unit's server, and are we in the radio channel? (Without a server set there
    // is no "the unit's tab": only what the plugin itself reports can be checked.)
    string server = m.ServerName ?? m.Host ?? "your unit's server";
    bool serverSet = !string.IsNullOrEmpty(m.Host);
    TsConnection unit = null;
    if (serverSet && f.Tabs != null) foreach (var t in f.Tabs) if (unit == null && OnUnit(f, t, m)) unit = t;
    if (serverSet && f.Tabs != null && unit == null)
      list.Add(new Finding { Problem = "TeamSpeak isn't connected to " + server + ".", Advice = "CONNECT TEAMSPEAK connects it (the plugin also asks you in TeamSpeak).",
        Fix = Fix.Connect, FixLabel = "CONNECT TEAMSPEAK", Question = "Connect TeamSpeak to " + server + "?" });
    else if (unit != null && !unit.Active)
      list.Add(new Finding { Problem = "TeamSpeak's selected tab isn't " + server + ": the Coalition plugin only works on the selected tab.",
        Advice = "Click the " + server + " tab in TeamSpeak (or close the other tabs)." });
    else if (f.Sd.ClientId == 0)
      list.Add(new Finding { Problem = "The Coalition plugin can't see your TeamSpeak connection (the game shows \"Teamspeak Plugin Not Detected\").",
        Advice = serverSet ? "Click the " + server + " tab in TeamSpeak and check it says connected." : "Click the tab of the server you play on in TeamSpeak and check it says connected." });
    else if (unit != null && !string.IsNullOrEmpty(f.Sd.ChannelName) && TeamSpeak.ChannelName(unit.ChannelPath) != f.Sd.ChannelName)
      list.Add(new Finding { Problem = "TeamSpeak hasn't moved you into " + f.Sd.ChannelName + ".",
        Advice = "Give it a few seconds. If it never moves you, tell your unit's admins: the channel or its password may have changed." });
    return list;
  }

  public static Finding LinkProfile(string problem, string target) {
    return new Finding { Problem = problem, Target = target, Fix = Fix.LinkProfile, FixLabel = "LINK THE FOLDERS",
      Question = "Link the plugin's folder to Reforger's profile folder?",
      Advice = "LINK THE FOLDERS makes the plugin's folder open Reforger's (your profile, settings and mods stay where they are)." };
  }

  // Whether a TeamSpeak tab is on the unit's server: the program's own test when Gather set one (comms.json may give a
  // name, which is looked up), otherwise the manifest's address as it stands.
  public static bool OnUnit(Facts f, TsConnection c, Manifest m) {
    if (c == null) return false;
    if (f.IsUnitServer != null) return f.IsUnitServer(c);
    return !string.IsNullOrEmpty(m.Host) && string.Equals(c.Ip, m.Host, StringComparison.OrdinalIgnoreCase) && (c.Port == 0 || c.Port == m.Port);
  }

  // What RADIO CHECK prints before its findings: everything it looked at, for COPY LOG.
  public static List<KeyValuePair<string, Tone>> Report(Facts f, Manifest m) {
    var r = new List<KeyValuePair<string, Tone>>();
    Action<string, Tone> add = delegate(string s, Tone t) { r.Add(new KeyValuePair<string, Tone>(s, t)); };
    add("radio files folder: " + f.ProfileDir, Tone.Dim);
    if (!f.TsInstalled) add("teamspeak 3: not installed", Tone.Warn);
    else add("teamspeak " + f.TsVersion + ": " + (f.TsRunning ? "running from " + (f.RunningTsDir ?? f.TsDir) : "not running") + " (installed in " + f.TsDir + ")", Tone.Dim);
    add("coalition plugin: " + (!f.PluginInstalled ? "not installed" : f.PluginFresh ? "loaded" : "installed, not loaded"), f.PluginFresh ? Tone.Ok : Tone.Dim);
    if (f.RuntimeMissing) add("visual c++ runtime: missing", Tone.Warn);
    add("reforger: " + (f.GameRunning ? "running" + (f.GameOptions == null ? "" : ", options '" + f.GameOptions + "'") : "not running"), Tone.Dim);
    if (f.LaunchOptions.Count > 0) add("steam launch options: '" + string.Join("' / '", f.LaunchOptions.ToArray()) + "'", Tone.Dim);
    if (f.GameProfileDir != null) add("game profile (-profile): " + f.GameProfileDir, Tone.Dim);
    add("game radio heartbeat: " + (f.HeartbeatAge < 0 ? "none" : ((int)f.HeartbeatAge) + " s ago"), HeartbeatFresh(f.HeartbeatAge) ? Tone.Ok : Tone.Dim);
    if (f.ElsewhereDir != null) add("game heartbeat found in " + f.ElsewhereDir, Tone.Warn);
    if (f.Sd != null) add("in game: " + (f.Sd.InGame ? "yes" : "no") + ", teamspeak id " + f.Sd.ClientId + ", radio channel '" + f.Sd.ChannelName + "'", Tone.Dim);
    if (string.IsNullOrEmpty(m.Host)) add("unit's teamspeak server: not set (CHANGE beside TS SERVER)", Tone.Warn);
    if (f.Tabs == null) add("teamspeak tabs: can't see them (TeamSpeak closed or ClientQuery off)", Tone.Dim);
    else if (f.Tabs.Count == 0) add("teamspeak tabs: none connected", Tone.Dim);
    else foreach (var t in f.Tabs)
      add("teamspeak tab: " + t.Ip + ":" + t.Port + (OnUnit(f, t, m) ? " (" + (m.ServerName ?? m.Host) + ")" : "") + ", channel '" + TeamSpeak.ChannelName(t.ChannelPath) + "'" + (t.Active ? "  <- selected" : ""), Tone.Dim);
    if (f.FolderAccessOn) add("controlled folder access: on", Tone.Dim);
    return r;
  }

  // ---------------------------------------------------------------------------------------------- reading the PC

  public static Facts Gather(Comms c) {
    var env = c.Env; var m = c.M; var ts = c.Ts;
    var bridge = new Bridge(env.ProfileDir);
    var f = new Facts { ProfileDir = env.ProfileDir, TsInstalled = ts != null };
    if (ts != null) { f.TsVersion = ts.Version; f.TsDir = ts.Dir; }
    var running = TeamSpeak.Running();
    f.TsRunning = running.Length > 0;
    foreach (var p in running) p.Dispose();
    f.RunningTsDir = f.TsRunning ? TeamSpeak.RunningDir() : null;
    f.PluginInstalled = ts != null && TeamSpeak.InstalledPluginHash(ts, m.PluginDll) != null;
    f.PluginFresh = Bridge.Fresh(bridge.Plugin(), DateTime.UtcNow);
    f.RuntimeMissing = RuntimeMissing(env, f.RunningTsDir ?? f.TsDir);
    f.FolderAccessOn = env.ReadReg("HKLM", @"SOFTWARE\Microsoft\Windows Defender\Windows Defender Exploit Guard\Controlled Folder Access", "EnableControlledFolderAccess") == "1"
                    || env.ReadReg("HKLM", @"SOFTWARE\Policies\Microsoft\Windows Defender\Windows Defender Exploit Guard\Controlled Folder Access", "EnableControlledFolderAccess") == "1";
    f.HeartbeatAge = bridge.HeartbeatAge(DateTime.UtcNow);
    f.Sd = bridge.Server();

    bool gameRunning;
    string options;
    f.GameProfileDir = GameProfile(env, m, out gameRunning, out options);
    f.GameRunning = gameRunning || c.GameRunning();
    f.GameOptions = options;
    f.LaunchOptions = SteamConfig.LaunchOptionsOnPc(env, m.SteamAppId);
    f.ElsewhereDir = HeartbeatElsewhere(env, f.GameProfileDir);

    f.IsUnitServer = delegate(TsConnection t) { return c.ConnectedToUnit(new List<TsConnection> { t }) != null; };

    TsInstall query = ts;
    if (f.RunningTsDir != null && (ts == null || !SameDir(f.RunningTsDir, ts.Dir))) query = TeamSpeak.AtDir(env, f.RunningTsDir) ?? ts;
    if (f.TsRunning && query != null) f.Tabs = TeamSpeak.Connections(query);
    return f;
  }

  // The plugin's DLL needs VCRUNTIME140.dll: from Windows (the runtime installed) or beside TeamSpeak.
  public static bool RuntimeMissing(Env env, string tsDir) {
    if (string.IsNullOrEmpty(env.SystemDir)) return false;   // can't tell
    if (File.Exists(Path.Combine(env.SystemDir, "vcruntime140.dll"))) return false;
    return string.IsNullOrEmpty(tsDir) || !File.Exists(Path.Combine(tsDir, "vcruntime140.dll"));
  }

  // The profile folder Reforger keeps its radio files in when it isn't the default (null: the default, the plugin's own):
  // the running game's own command line is the truth; when the game isn't running, or Windows won't show its command line,
  // it's what Steam's launch options will start it with.
  public static string GameProfile(Env env, Manifest m, out bool running, out string options) {
    string gameDir;
    running = GameProcess(m.GameProcess, out options, out gameDir);
    if (options != null) return ProfileArg(options, env.Documents, gameDir);
    return SteamConfig.LaunchProfile(env, m.SteamAppId);
  }

  // Whether the game runs, its command line after the program and its folder (null when Windows won't say).
  static bool GameProcess(string name, out string options, out string gameDir) {
    options = null; gameDir = null;
    bool running = false;
    foreach (char ch in name ?? "") if (!char.IsLetterOrDigit(ch) && ch != '_' && ch != '-' && ch != '.') return false;   // goes into a WMI query
    try {
      using (var s = new ManagementObjectSearcher("SELECT CommandLine, ExecutablePath FROM Win32_Process WHERE Name LIKE '" + name + "%'"))
      using (var all = s.Get())
        foreach (ManagementObject o in all) using (o) {
          running = true;
          var line = o["CommandLine"] as string;
          if (line != null) options = AfterProgram(line);
          var exe = o["ExecutablePath"] as string;
          if (!string.IsNullOrEmpty(exe)) gameDir = Path.GetDirectoryName(exe);
        }
    } catch (Exception) { }
    return running;
  }

  // "\"C:\\Steam\\...\\ArmaReforgerSteam.exe\" -window" -> "-window"
  public static string AfterProgram(string commandLine) {
    string s = commandLine.TrimStart();
    int end = s.StartsWith("\"") ? s.IndexOf('"', 1) + 1 : s.IndexOf(' ');
    return end <= 0 || end >= s.Length ? "" : s.Substring(end).Trim();
  }

  // The profile folder a -profile option points the game at, as Reforger reads it: a name is a folder in
  // Documents\My Games, a full path is used as it is, and a relative one starts from the game's folder (where Steam
  // starts it; null when that isn't known); the game keeps its files in that folder's "profile". Null without one.
  public static string ProfileArg(string options, string documents, string gameDir = null) {
    if (string.IsNullOrEmpty(options)) return null;
    var a = SplitArgs(options);
    for (int i = 0; i + 1 < a.Count; i++) {
      if (!string.Equals(a[i], "-profile", StringComparison.OrdinalIgnoreCase)) continue;
      string v = a[i + 1].Trim();
      if (v.Length == 0 || v.StartsWith("-")) return null;
      try {
        if (v.IndexOfAny(new[] { '\\', '/', ':' }) < 0) return Path.Combine(documents, "My Games", v, "profile");
        bool full = v.StartsWith(@"\\") || v.StartsWith("//") || (v.Length >= 3 && char.IsLetter(v[0]) && v[1] == ':' && (v[2] == '\\' || v[2] == '/'));
        if (!full) { if (gameDir == null) return null; v = Path.Combine(gameDir, v); }
        return Path.Combine(Path.GetFullPath(v), "profile");
      } catch (Exception) { return null; }
    }
    return null;
  }

  public static List<string> SplitArgs(string s) {
    var list = new List<string>();
    var cur = new StringBuilder();
    bool quoted = false, any = false;
    foreach (char ch in s) {
      if (ch == '"') { quoted = !quoted; any = true; }
      else if (char.IsWhiteSpace(ch) && !quoted) { if (any || cur.Length > 0) list.Add(cur.ToString()); cur.Length = 0; any = false; }
      else cur.Append(ch);
    }
    if (any || cur.Length > 0) list.Add(cur.ToString());
    return list;
  }

  // A profile folder other than the plugin's where the game's heartbeat is fresh: the -profile folder, or any
  // "My Games\<name>\profile" in the Documents folders this PC has (OneDrive's, the one under the user folder).
  public static string HeartbeatElsewhere(Env env, string gameProfileDir) {
    var dirs = new List<string>();
    if (gameProfileDir != null) dirs.Add(gameProfileDir);
    var roots = new List<string> { env.Documents };
    if (env.OtherDocuments != null) roots.AddRange(env.OtherDocuments);
    foreach (var root in roots) {
      try {
        string games = Path.Combine(root, "My Games");
        if (Directory.Exists(games))
          foreach (var d in Directory.GetDirectories(games))
            if (Path.GetFileName(d).IndexOf("Workbench", StringComparison.OrdinalIgnoreCase) < 0) dirs.Add(Path.Combine(d, "profile"));   // a modder's Workbench isn't the game
      } catch (Exception) { }
    }
    var now = DateTime.UtcNow;
    foreach (var d in dirs) {
      if (SameDir(d, env.ProfileDir)) continue;
      if (HeartbeatFresh(new Bridge(d).HeartbeatAge(now))) return d;
    }
    return null;
  }

  // ---------------------------------------------------------------------------------------------- folders

  public static bool SameDir(string a, string b) {
    if (a == null || b == null) return false;
    return string.Equals(RealPath(a), RealPath(b), StringComparison.OrdinalIgnoreCase);
  }

  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint size, uint flags);
  [DllImport("kernel32.dll", SetLastError = true)]
  static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputSize, IntPtr output, int outputSize, out uint returned, IntPtr overlapped);

  const uint OpenExisting = 3, BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000, ShareAll = 7, GenericWrite = 0x40000000;

  // The folder's real place, links followed; the full path as given when Windows can't say (missing, or not Windows).
  public static string RealPath(string dir) {
    string full;
    try { full = Path.GetFullPath(dir).TrimEnd('\\', '/'); } catch (Exception) { return dir; }
    try {
      using (var h = CreateFile(full, 0, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero)) {
        if (h.IsInvalid) return full;
        var sb = new StringBuilder(1024);
        uint n = GetFinalPathNameByHandle(h, sb, (uint)sb.Capacity, 0);
        if (n == 0 || n >= sb.Capacity) return full;
        string p = sb.ToString();
        if (p.StartsWith(@"\\?\UNC\")) p = @"\\" + p.Substring(8);
        else if (p.StartsWith(@"\\?\")) p = p.Substring(4);
        return p.TrimEnd('\\');
      }
    } catch (Exception) { return full; }
  }

  // Makes pluginDir open gameDir (a directory junction, what mklink /J makes: no administrator rights needed). Whatever was
  // at pluginDir moves aside and is kept, never deleted; if the link fails it moves back. Returns where it went (null: there
  // was nothing).
  public static string LinkFolders(string pluginDir, string gameDir) {
    if (!Directory.Exists(gameDir)) throw new IOException("Reforger's profile folder " + gameDir + " doesn't exist");
    if (SameDir(pluginDir, gameDir)) return null;
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(pluginDir).TrimEnd('\\')));
    string aside = null;
    if (Directory.Exists(pluginDir) || File.Exists(pluginDir)) {
      aside = pluginDir + ".bifrost-old";
      for (int i = 2; Directory.Exists(aside) || File.Exists(aside); i++) aside = pluginDir + ".bifrost-old-" + i;
      Directory.Move(pluginDir, aside);   // a folder or an old link; a link moves as a link
    }
    try {
      MakeJunction(pluginDir, gameDir);
      if (!SameDir(pluginDir, gameDir)) throw new IOException("Windows didn't keep the link");
    } catch (Exception) {
      try { if (Directory.Exists(pluginDir)) Directory.Delete(pluginDir, false); } catch (Exception) { }
      if (aside != null) try { Directory.Move(aside, pluginDir); } catch (Exception) { }
      throw;
    }
    return aside;
  }

  // A mount-point reparse point on a new empty folder: REPARSE_DATA_BUFFER with the target as "\??\C:\..." and its
  // printable form.
  public static void MakeJunction(string link, string target) {
    string t = Path.GetFullPath(target).TrimEnd('\\');
    byte[] sub = Encoding.Unicode.GetBytes(@"\??\" + t), print = Encoding.Unicode.GetBytes(t);
    int names = sub.Length + 2 + print.Length + 2;
    var buf = new byte[16 + names];
    BitConverter.GetBytes(0xA0000003u).CopyTo(buf, 0);              // IO_REPARSE_TAG_MOUNT_POINT
    BitConverter.GetBytes((ushort)(8 + names)).CopyTo(buf, 4);      // ReparseDataLength (2 reserved bytes follow)
    BitConverter.GetBytes((ushort)0).CopyTo(buf, 8);                // SubstituteNameOffset
    BitConverter.GetBytes((ushort)sub.Length).CopyTo(buf, 10);      // SubstituteNameLength
    BitConverter.GetBytes((ushort)(sub.Length + 2)).CopyTo(buf, 12);   // PrintNameOffset
    BitConverter.GetBytes((ushort)print.Length).CopyTo(buf, 14);    // PrintNameLength
    sub.CopyTo(buf, 16);
    print.CopyTo(buf, 16 + sub.Length + 2);
    Directory.CreateDirectory(link);
    using (var h = CreateFile(Path.GetFullPath(link), GenericWrite, 0, IntPtr.Zero, OpenExisting, BackupSemantics | OpenReparsePoint, IntPtr.Zero)) {
      if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
      uint got;
      if (!DeviceIoControl(h, 0x000900A4, buf, buf.Length, IntPtr.Zero, 0, out got, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());   // FSCTL_SET_REPARSE_POINT
    }
  }

  public static bool IsLink(string dir) {
    try { return Directory.Exists(dir) && (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0; } catch (Exception) { return false; }
  }
}

// Steam's launch options for a game, from localconfig.vdf (one per Steam account on the PC):
//   "apps" { "1874880" { ... "LaunchOptions" "-profile \"D:\\Reforger\"" } }
static class SteamConfig {
  public static List<string> LaunchOptionsOnPc(Env env, int appId) {
    return new List<string>(LaunchOptionsByAccount(env, appId).Values);
  }

  // The profile folder Steam's launch options start the game in (null: the default folder): the signed-in account's
  // options while Steam is running; otherwise the -profile every account on the PC agrees on (null when they differ).
  public static string LaunchProfile(Env env, int appId) {
    var byAccount = LaunchOptionsByAccount(env, appId);
    string active = ActiveAccount(env), o;
    if (active != null) return byAccount.TryGetValue(active, out o) ? Doctor.ProfileArg(o, env.Documents) : null;
    string found = null;
    foreach (var opts in byAccount.Values) {
      string d = Doctor.ProfileArg(opts, env.Documents);
      if (d == null) continue;
      if (found != null && !string.Equals(found, d, StringComparison.OrdinalIgnoreCase)) return null;
      found = d;
    }
    return found;
  }

  // Steam account id (its userdata folder) -> the game's launch options, for the accounts that set some.
  static Dictionary<string, string> LaunchOptionsByAccount(Env env, int appId) {
    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    string users = UserData(env);
    if (users == null) return map;
    try {
      foreach (var dir in Directory.GetDirectories(users)) {
        string f = Path.Combine(dir, "config", "localconfig.vdf");
        if (!File.Exists(f)) continue;
        foreach (var o in LaunchOptions(File.ReadAllText(f, Encoding.UTF8), appId)) if (o.Trim().Length > 0) map[Path.GetFileName(dir)] = o.Trim();
      }
    } catch (Exception) { }
    return map;
  }

  // The account signed in to the running Steam (null: Steam isn't running, or that account has no folder here).
  static string ActiveAccount(Env env) {
    string id = env.ReadReg("HKCU", @"Software\Valve\Steam\ActiveProcess", "ActiveUser");
    string users = UserData(env);
    if (string.IsNullOrEmpty(id) || id == "0" || users == null) return null;
    try { return Directory.Exists(Path.Combine(users, id)) ? id : null; } catch (Exception) { return null; }
  }

  static string UserData(Env env) {
    string steam = env.ReadReg("HKCU", @"Software\Valve\Steam", "SteamPath");
    if (string.IsNullOrEmpty(steam)) return null;
    try { string d = Path.Combine(steam, "userdata"); return Directory.Exists(d) ? d : null; } catch (Exception) { return null; }
  }

  // Every "LaunchOptions" directly inside a "<appId>" { } block: a small KeyValues reader (quoted strings with \" and \\
  // escapes, braces, // comments).
  public static List<string> LaunchOptions(string vdf, int appId) {
    var found = new List<string>();
    string id = appId.ToString(CultureInfo.InvariantCulture);
    var blocks = new List<string>();
    string key = null;
    int i = 0;
    while (i < vdf.Length) {
      char c = vdf[i];
      if (c == '"') {
        string s = ReadString(vdf, ref i);
        if (key == null) { key = s; continue; }
        if (blocks.Count > 0 && blocks[blocks.Count - 1] == id && string.Equals(key, "LaunchOptions", StringComparison.OrdinalIgnoreCase)) found.Add(s);
        key = null;
        continue;
      }
      if (c == '{') blocks.Add(key ?? "");
      else if (c == '}') { if (blocks.Count > 0) blocks.RemoveAt(blocks.Count - 1); }
      else if (c == '/' && i + 1 < vdf.Length && vdf[i + 1] == '/') { while (i < vdf.Length && vdf[i] != '\n') i++; continue; }
      if (c == '{' || c == '}') key = null;
      i++;
    }
    return found;
  }

  static string ReadString(string s, ref int i) {
    var sb = new StringBuilder();
    i++;   // the opening quote
    while (i < s.Length) {
      char c = s[i];
      if (c == '\\' && i + 1 < s.Length) {
        char n = s[i + 1];
        sb.Append(n == 'n' ? '\n' : n == 't' ? '\t' : n);
        i += 2;
        continue;
      }
      i++;
      if (c == '"') break;
      sb.Append(c);
    }
    return sb.ToString();
  }
}

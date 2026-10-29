// What Bifrost Comms does, apart from its window: setup (TeamSpeak 3, the Coalition plugin, shortcuts, the unit's
// bookmark), the launch (TeamSpeak connected and the plugin alive, then Reforger through Steam), watching the radio link,
// installing and updating itself in %LOCALAPPDATA%\Bifrost Comms, and removing itself. Jobs run on a worker thread and
// talk to the window through IConsole.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

enum Tone { Text, Dim, Ok, Warn, Bad, Accent }

interface IConsole {
  void Line(string text, Tone tone);
  void Progress(string label, long got, long total, double rate);   // label null clears the bar
  string Ask(string question, params string[] choices);              // blocks until a choice is picked (null: window closed)
  string Input(string prompt, string current, Func<string, string> problem, int maxLength);   // problem(text): what's wrong, null = fine
  void Restart(string exe, string args);                             // close this window; that program takes over
  void Changed();
}

// Where things are on this PC. The tests point it at a sandbox folder and a dictionary instead of the registry.
class Env {
  public string AppData, LocalAppData, Documents, Temp, Desktop, StartMenu, ProgramFiles, ProgramFilesX86;
  public string SystemDir;          // 64-bit Windows\System32, where the Visual C++ runtime goes (null: don't check)
  public string[] OtherDocuments;   // Documents folders besides the known one (under the user folder, OneDrive's) for RADIO CHECK
  public Func<string, string, string, string> Reg;   // hive ("HKCU"/"HKLM"), key, value name ("" = default) -> value or null

  public string Home { get { return Path.Combine(LocalAppData, "Bifrost Comms"); } }
  public string HomeExe { get { return Path.Combine(Home, "BifrostComms.exe"); } }
  public string HomeUnit { get { return Path.Combine(Home, "comms.json"); } }
  public string Downloads { get { return Path.Combine(Home, "downloads"); } }
  public string ProfileDir { get { return Path.Combine(Documents, "My Games", "ArmaReforger", "profile"); } }
  public string StartMenuDir { get { return Path.Combine(StartMenu, "Bifrost Comms"); } }

  public string ReadReg(string hive, string key, string value) {
    try { return Reg == null ? null : Reg(hive, key, value); } catch (Exception) { return null; }
  }

  public static Env Real() {
    return new Env {
      AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
      LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
      // The plugin finds the profile through the Documents known folder, so a Documents moved to OneDrive works for both.
      Documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
      Temp = Path.GetTempPath(),
      Desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
      StartMenu = Environment.GetFolderPath(Environment.SpecialFolder.Programs),
      ProgramFiles = Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
      ProgramFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
      SystemDir = Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Sysnative") : Environment.SystemDirectory,
      OtherDocuments = OtherDocumentFolders(),
      Reg = RealRegistry,
    };
  }

  static string[] OtherDocumentFolders() {
    var list = new List<string>();
    foreach (var v in new[] { "USERPROFILE", "OneDrive", "OneDriveConsumer", "OneDriveCommercial" }) {
      string root = Environment.GetEnvironmentVariable(v);
      if (string.IsNullOrEmpty(root)) continue;
      string d = Path.Combine(root, "Documents");
      if (Directory.Exists(d) && !list.Contains(d)) list.Add(d);
    }
    return list.ToArray();
  }

  // TeamSpeak's installer writes the 64-bit view (SetRegView 64), so read that one whatever this process is.
  static string RealRegistry(string hive, string key, string value) {
    var root = Microsoft.Win32.RegistryKey.OpenBaseKey(hive == "HKLM" ? Microsoft.Win32.RegistryHive.LocalMachine : Microsoft.Win32.RegistryHive.CurrentUser,
                                                       Environment.Is64BitOperatingSystem ? Microsoft.Win32.RegistryView.Registry64 : Microsoft.Win32.RegistryView.Default);
    using (root) using (var k = root.OpenSubKey(key)) {
      if (k == null) return null;
      object v = k.GetValue(value);
      return v == null ? null : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
    }
  }
}

// The player's own choices. The unit's (server, manifest) are in comms.json (UnitConfig).
class Settings {
  public string Callsign;
  public string PluginVersion, PluginDllSha;   // what SET UP last installed: how a release without a pinned DLL hash is recognised
  public string Bookmarked;                    // "host:port" TeamSpeak was last given a bookmark for
  public string File;

  public static Settings Load(string file) {
    var s = new Settings { File = file };
    try {
      if (System.IO.File.Exists(file)) {
        var d = Json.Parse(System.IO.File.ReadAllText(file, Encoding.UTF8));
        s.Callsign = Json.Str(d, "Callsign"); s.Bookmarked = Json.Str(d, "Bookmarked");
        s.PluginVersion = Json.Str(d, "PluginVersion"); s.PluginDllSha = Json.Str(d, "PluginDllSha");
      }
    } catch (Exception) { }
    return s;
  }

  public void Save() {
    var d = new Dictionary<string, object>();
    if (!string.IsNullOrEmpty(Callsign)) d["Callsign"] = Callsign;
    if (!string.IsNullOrEmpty(Bookmarked)) d["Bookmarked"] = Bookmarked;
    if (!string.IsNullOrEmpty(PluginVersion)) d["PluginVersion"] = PluginVersion;
    if (!string.IsNullOrEmpty(PluginDllSha)) d["PluginDllSha"] = PluginDllSha;
    Directory.CreateDirectory(Path.GetDirectoryName(File));
    string tmp = File + ".tmp";
    System.IO.File.WriteAllText(tmp, Json.Write(d), new UTF8Encoding(false));
    if (System.IO.File.Exists(File)) System.IO.File.Delete(File);
    System.IO.File.Move(tmp, File);
  }

  // TeamSpeak takes 3 to 30 characters.
  public static string CallsignProblem(string s) {
    s = (s ?? "").Trim();
    if (s.Length < 3) return "at least 3 characters";
    if (s.Length > 30) return "30 characters at most";
    foreach (char c in s) if (char.IsControl(c)) return "no control characters";
    return null;
  }
}

// What the status rail shows. The core fills it; the window reads it under the lock.
class Status {
  public string Uplink = "CHECKING", UplinkDetail = ""; public Tone UplinkTone = Tone.Dim;
  public string Ts = "CHECKING", TsDetail = ""; public Tone TsTone = Tone.Dim;
  public string Plugin = "CHECKING", PluginDetail = ""; public Tone PluginTone = Tone.Dim;
  public string Server = "", ServerDetail = ""; public Tone ServerTone = Tone.Dim;
  public string TsLink = "UNKNOWN", TsLinkDetail = "checked while TeamSpeak runs"; public Tone TsLinkTone = Tone.Dim;
  public string Game = "NOT RUNNING", GameDetail = "Arma Reforger"; public Tone GameTone = Tone.Dim;
  public LinkState Link = Bridge.Derive(false, false, false, null, -1, null);
  public string Updates = "CHECKING", UpdatesDetail = ""; public Tone UpdatesTone = Tone.Dim;

  public Status Clone() { return (Status)MemberwiseClone(); }
}

class Comms {
  public readonly Env Env;
  public readonly string Exe, Version;
  public readonly Settings Settings;
  public readonly UnitConfig Unit;
  public readonly Status St = new Status();
  public readonly object Lock = new object();
  public IConsole Out;
  public string Args = "";   // the command line, passed on when another copy takes over (installed copy, update)
  public Manifest M = Manifest.BuiltIn();
  public TsInstall Ts;
  public volatile bool Cancel, Busy;
  public bool NoGame { get; set; }   // --no-game, for testing: never start Steam
  string lastLinkLine;
  bool lastGame, lastPluginFresh, pluginEverFresh;
  DateTime lastQuery = DateTime.MinValue;
  List<TsConnection> lastConnections;
  string[] hostIps = new string[0]; string hostIpsFor; DateTime hostIpsAt;
  // The automatic RADIO CHECK: what's stuck, since when, and whether it already ran for it (once each time).
  string stuck, pendingCheck;
  DateTime stuckSince;
  bool stuckChecked;
  // The running game's profile folder when -profile moves it (read once per game start), and a link LAUNCH was told not to make.
  string gameProfile, declinedProfile;
  bool gameProfileRead;
  int gameProfileTries;

  public Comms(Env env, string exe, string version) {
    Env = env; Exe = exe; Version = version;
    Settings = Settings.Load(Path.Combine(env.Home, "BifrostComms.settings.json"));
    // comms.json beside a downloaded copy is the unit's newest word (it's copied home on install); otherwise the
    // installed copy's.
    var beside = UnitConfig.Load(Path.Combine(Path.GetDirectoryName(exe), "comms.json"));
    Unit = File.Exists(beside.File) ? beside : UnitConfig.Load(env.HomeUnit);
    ApplyUnit();
  }

  public bool RunningFromHome { get { return string.Equals(Path.GetFullPath(Exe), Path.GetFullPath(Env.HomeExe), StringComparison.OrdinalIgnoreCase); } }

  void Say(string text, Tone tone) { if (Out != null) Out.Line(text, tone); }
  void Changed() { if (Out != null) Out.Changed(); }

  // ---------------------------------------------------------------------------------------------- start

  // What the window runs first. Returns false when this copy handed over to another one (installed or updated).
  public bool Boot(bool launch) {
    if (Unit.ManifestUrl != null) Say("uplink " + new Uri(Unit.ManifestUrl).Host + " ...", Tone.Dim);
    FetchManifest();
    if (!RunningFromHome) return InstallHome(launch);
    if (SelfUpdate(launch)) return false;
    Detect(true);
    string moved = SteamConfig.LaunchProfile(Env, M.SteamAppId);
    if (moved != null) {
      bool linked = Doctor.SameDir(moved, Env.ProfileDir);
      Say("reforger profile (steam -profile): " + moved + (linked ? " ........ LINKED" : ": the radio plugin doesn't look there; LAUNCH links them"), linked ? Tone.Ok : Tone.Warn);
    }
    if (string.IsNullOrEmpty(M.Host)) Say("no TeamSpeak server yet: SET UP asks for the address your unit gave you", Tone.Accent);
    else Say("teamspeak server " + M.Host + ":" + M.Port + (M.ServerName != M.Host ? " (" + M.ServerName + ")" : ""), Tone.Dim);
    Say(Settings.Callsign == null ? "no callsign yet: you'll be asked the first time TeamSpeak connects" : "callsign " + Settings.Callsign, Settings.Callsign == null ? Tone.Accent : Tone.Dim);
    return true;
  }

  // The unit's manifest when comms.json names one, otherwise the built-in pins; then the server on top.
  public void FetchManifest() {
    string url = Unit.ManifestUrl;
    if (url == null) {
      M = Manifest.BuiltIn();
      lock (Lock) { St.Uplink = "LOCAL"; St.UplinkDetail = "built-in pins (no unit manifest)"; St.UplinkTone = Tone.Ok; }
      if (Unit.Manifest != null) Say("comms.json's manifest must be an https:// address: ignored, using the built-in pins", Tone.Warn);
    } else {
      string host = new Uri(url).Host;
      try {
        M = Manifest.Parse(Net.GetText(url, 8000, false), url);
        lock (Lock) { St.Uplink = "ONLINE"; St.UplinkDetail = "unit manifest: " + host; St.UplinkTone = Tone.Ok; }
        Say("uplink established: " + host + " .......... OK", Tone.Ok);
      } catch (Exception e) {
        M = Manifest.BuiltIn();
        lock (Lock) { St.Uplink = "OFFLINE"; St.UplinkDetail = "using the built-in pins"; St.UplinkTone = Tone.Warn; }
        Say("uplink failed (" + Short(e) + "): using the built-in pins", Tone.Warn);
      }
    }
    ApplyUnit();
    Changed();
  }

  // The server is the manifest's when it names one, otherwise comms.json's (which the player may have filled in).
  void ApplyUnit() {
    if (M.ServerFromManifest) { if (M.Password == null) M.Password = Unit.Password; }
    else {
      M.Host = Unit.Host; M.Port = Unit.Port; M.Password = Unit.Password;
      M.ServerName = Unit.Name ?? Unit.Host; M.Bookmark = Unit.Bookmark ?? M.ServerName;
    }
    lock (Lock) {
      if (string.IsNullOrEmpty(M.Host)) { St.Server = "NOT SET"; St.ServerDetail = "SET UP asks for the address"; St.ServerTone = Tone.Warn; }
      else {
        St.Server = M.Host + ":" + M.Port;
        if (!M.StatusKnown) { St.ServerDetail = M.ServerName; St.ServerTone = Tone.Dim; }
        else if (M.ServerOnline) { St.ServerDetail = "ONLINE  " + M.Clients + "/" + M.Slots + " connected"; St.ServerTone = Tone.Ok; }
        else { St.ServerDetail = "not answering"; St.ServerTone = Tone.Bad; }
      }
    }
  }

  // First run from the browser's download: copy the newest build (and the unit's comms.json beside it) to
  // %LOCALAPPDATA%\Bifrost Comms, make the shortcuts, and reopen from there, so shortcuts and updates always use the one
  // installed copy.
  bool InstallHome(bool launch) {
    Directory.CreateDirectory(Env.Home);
    Say("installing Bifrost Comms to " + Env.Home, Tone.Text);
    bool newer = M.LauncherVersion != null && Ver.Compare(M.LauncherVersion, Version) > 0;
    if (newer) {
      try {
        Fetch("Bifrost Comms " + M.LauncherVersion, M.LauncherUrl, Path.Combine(Env.Downloads, "BifrostComms-" + M.LauncherVersion + ".exe"), M.LauncherSha, M.LauncherSize, true);
        CheckBuild(Path.Combine(Env.Downloads, "BifrostComms-" + M.LauncherVersion + ".exe"), M.LauncherVersion);
        CopyOver(Path.Combine(Env.Downloads, "BifrostComms-" + M.LauncherVersion + ".exe"), Env.HomeExe);
      } catch (Exception e) {
        Say("couldn't get Bifrost Comms " + M.LauncherVersion + " (" + Short(e) + "): installing this copy (" + Version + ")", Tone.Warn);
        newer = false;
      }
    }
    if (!newer) CopyOver(Exe, Env.HomeExe);
    Unblock(Env.HomeExe);
    if (File.Exists(Unit.File) && !SamePath(Unit.File, Env.HomeUnit)) {
      CopyOver(Unit.File, Env.HomeUnit);
      Say("unit settings (comms.json) installed" + (Unit.Name != null ? ": " + Unit.Name : ""), Tone.Ok);
    }
    WriteShortcuts();
    Say("installed. Reopening from the installed copy ...", Tone.Ok);
    if (Out != null) Out.Restart(Env.HomeExe, Args);
    return false;
  }

  // A newer build in the unit's manifest replaces this one: the running program can be renamed but not overwritten, so it
  // moves aside to BifrostComms.old.exe (removed on the next start) and the new build reopens with the same request.
  public bool SelfUpdate(bool launch) {
    if (M.LauncherVersion == null || Ver.Compare(M.LauncherVersion, Version) <= 0) return false;
    Say("update: Bifrost Comms " + Version + " -> " + M.LauncherVersion + (string.IsNullOrEmpty(M.LauncherNotes) ? "" : " (" + M.LauncherNotes + ")"), Tone.Accent);
    string file = Path.Combine(Env.Downloads, "BifrostComms-" + M.LauncherVersion + ".exe");
    try {
      Fetch("Bifrost Comms " + M.LauncherVersion, M.LauncherUrl, file, M.LauncherSha, M.LauncherSize, true);
      CheckBuild(file, M.LauncherVersion);
      string old = Path.Combine(Env.Home, "BifrostComms.old.exe");
      if (File.Exists(old)) File.Delete(old);
      File.Move(Env.HomeExe, old);
      try { File.Copy(file, Env.HomeExe); } catch (Exception) { File.Move(old, Env.HomeExe); throw; }
      Say("updated. Reopening ...", Tone.Ok);
      if (Out != null) Out.Restart(Env.HomeExe, Args);
      return true;
    } catch (Exception e) {
      Say("update failed (" + Short(e) + "): carrying on with " + Version, Tone.Warn);
      return false;
    }
  }

  // The build must be the version the manifest says it is: a mismatch would update, reopen and update again forever.
  public static void CheckBuild(string exe, string version) {
    string got = ExeVersion(exe);
    if (got != version) throw new IOException("the manifest's Bifrost Comms " + version + " is really " + (got ?? "not a program") + ": not installed");
  }

  public static string ExeVersion(string exe) {
    try {
      var v = FileVersionInfo.GetVersionInfo(exe);
      if (v.FileMajorPart == 0 && v.FileMinorPart == 0 && v.FileBuildPart == 0) return null;
      return v.FileMajorPart + "." + v.FileMinorPart + "." + v.FileBuildPart;
    } catch (Exception) { return null; }
  }

  public static void CleanUpAfterUpdate(string dir) {
    string old = Path.Combine(dir, "BifrostComms.old.exe");
    for (int i = 0; i < 10 && File.Exists(old); i++) {
      try { File.Delete(old); } catch (Exception) { Thread.Sleep(300); }
    }
  }

  static bool SamePath(string a, string b) { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }

  static void CopyOver(string from, string to) {
    if (SamePath(from, to)) return;
    string tmp = to + ".new";
    File.Copy(from, tmp, true);
    if (File.Exists(to)) File.Delete(to);
    File.Move(tmp, to);
  }

  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool DeleteFile(string path);

  // The browser marks a download as coming from the internet, and Windows would warn on every shortcut launch of the
  // installed copy. The player already chose to run it, so the installed copy drops that mark (what Properties > Unblock does).
  static void Unblock(string file) { try { DeleteFile(file + ":Zone.Identifier"); } catch (Exception) { } }

  // ---------------------------------------------------------------------------------------------- what's on this PC

  public void Detect(bool verbose) {
    Ts = TeamSpeak.Find(Env);
    var running = TeamSpeak.Running();
    bool tsRunning = running.Length > 0;
    foreach (var p in running) p.Dispose();
    lock (Lock) {
      if (Ts == null) { St.Ts = "NOT INSTALLED"; St.TsDetail = "SET UP installs " + M.TsVersion; St.TsTone = Tone.Bad; }
      else { St.Ts = Ts.Version + (tsRunning ? "  RUNNING" : "  READY"); St.TsDetail = Ts.Mode == "AllUsers" ? "installed for all users" : "installed for you"; St.TsTone = Tone.Ok; }
    }
    if (verbose) {
      if (Ts == null) Say("teamspeak 3 client ................ NOT FOUND", Tone.Warn);
      else Say("teamspeak 3 client " + Ts.Version + " ............ OK  (" + Ts.Dir + ")", Tone.Ok);
      if (TeamSpeak.NewClientPresent(Env)) Say("note: the new TeamSpeak (5/6) is here too. Coalition VON needs TeamSpeak 3; both can stay.", Tone.Dim);
    }
    PluginStatus(verbose);
    UpdateStatus();
    Changed();
  }

  // Installed: the DLL in TeamSpeak's plugins folder matches the pinned hash, or, for a release the manifest gives no DLL
  // hash for (a unit following Coalition's releases), it's still the DLL this program installed for that version.
  public bool PluginCurrent() {
    if (Ts == null) return false;
    string h = TeamSpeak.InstalledPluginHash(Ts, M.PluginDll);
    if (h == null) return false;
    if (M.PluginDllSha != null) return h == M.PluginDllSha;
    return Settings.PluginVersion == M.PluginVersion && Settings.PluginDllSha == h;
  }

  void PluginStatus(bool verbose) {
    string h = Ts == null ? null : TeamSpeak.InstalledPluginHash(Ts, M.PluginDll);
    bool current = PluginCurrent();
    var info = new Bridge(Env.ProfileDir).Plugin();
    bool fresh = Bridge.Fresh(info, DateTime.UtcNow);
    lock (Lock) {
      if (h == null) { St.Plugin = "NOT INSTALLED"; St.PluginDetail = "SET UP installs " + M.PluginVersion; St.PluginTone = Tone.Bad; }
      else if (!current) { St.Plugin = "UPDATE " + M.PluginVersion; St.PluginDetail = "SET UP or LAUNCH installs it"; St.PluginTone = Tone.Warn; }
      else if (fresh) { St.Plugin = M.PluginVersion + "  LOADED"; St.PluginDetail = "talking to TeamSpeak"; St.PluginTone = Tone.Ok; }
      else { St.Plugin = M.PluginVersion + "  INSTALLED"; St.PluginDetail = "loads when TeamSpeak starts"; St.PluginTone = Tone.Ok; }
    }
    if (verbose) {
      if (h == null) Say("coalition radio plugin ............ NOT INSTALLED", Tone.Warn);
      else if (!current) Say("coalition radio plugin ............ OUTDATED (" + M.PluginVersion + " available)", Tone.Warn);
      else Say("coalition radio plugin " + M.PluginVersion + " ........ OK", Tone.Ok);
    }
  }

  void UpdateStatus() {
    var pending = new List<string>();
    if (M.LauncherVersion != null && Ver.Compare(M.LauncherVersion, Version) > 0) pending.Add("Bifrost Comms " + M.LauncherVersion);
    if (Ts != null && TeamSpeak.InstalledPluginHash(Ts, M.PluginDll) != null && !PluginCurrent()) pending.Add("plugin " + M.PluginVersion);
    lock (Lock) {
      if (pending.Count > 0) { St.Updates = pending.Count + " PENDING"; St.UpdatesDetail = string.Join(", ", pending.ToArray()); St.UpdatesTone = Tone.Warn; }
      else { St.Updates = "UP TO DATE"; St.UpdatesDetail = "Bifrost Comms " + Version; St.UpdatesTone = Tone.Ok; }
    }
  }

  // ---------------------------------------------------------------------------------------------- setup

  public bool EnsureCallsign(bool force) {
    if (!force && Settings.CallsignProblem(Settings.Callsign) == null) return true;
    string name = Out == null ? null : Out.Input("CALLSIGN (your name on TeamSpeak, e.g. PFC J. Doe)", Settings.Callsign ?? "", Settings.CallsignProblem, 30);
    if (name == null) return Settings.CallsignProblem(Settings.Callsign) == null;
    Settings.Callsign = name.Trim();
    Settings.Save();
    Say("callsign set: " + Settings.Callsign, Tone.Ok);
    return true;
  }

  // The unit's TeamSpeak address, asked when comms.json didn't bring one (force: the player pressed CHANGE). Saved to the
  // installed comms.json. A server the unit's manifest names isn't the player's to change.
  public bool EnsureServer(bool force) {
    if (M.ServerFromManifest || (!force && !string.IsNullOrEmpty(M.Host))) return !string.IsNullOrEmpty(M.Host);
    string current = Unit.Host == null ? "" : Unit.Host + (Unit.Port == UnitConfig.DefaultPort ? "" : ":" + Unit.Port);
    string text = Out == null ? null : Out.Input("TEAMSPEAK SERVER (the address your unit gave you, e.g. ts.example.com or 203.0.113.10:9987)", current, UnitConfig.AddressProblem, 100);
    if (text == null) return !string.IsNullOrEmpty(M.Host);
    string host; int port;
    UnitConfig.Split(text, out host, out port);
    if (!string.Equals(host, Unit.Host, StringComparison.OrdinalIgnoreCase)) { Unit.Name = null; Unit.Bookmark = null; Unit.Password = null; }
    Unit.Host = host; Unit.Port = port;
    Unit.File = Env.HomeUnit;
    Unit.Save();
    ApplyUnit();
    Changed();
    Say("teamspeak server set: " + host + ":" + port, Tone.Ok);
    return true;
  }

  // TeamSpeak 3 if it's missing, the plugin if it's missing or old, the shortcuts, and the unit's bookmark.
  public void Setup() {
    Say("=== SETUP ===", Tone.Accent);
    Detect(true);
    if (Ts == null && !InstallTeamSpeak()) return;
    if (!InstallPlugin(true)) return;
    WriteShortcuts();
    Detect(false);
    if (!EnsureServer(false)) Say("no TeamSpeak server set yet: press CHANGE beside TS SERVER when you have your unit's address", Tone.Warn);
    else {
      string answer = Out == null ? null : Out.Ask("Connect TeamSpeak to " + M.ServerName + " now (adds the bookmark)?", "CONNECT", "LATER");
      if (answer == "CONNECT" && EnsureCallsign(false)) ConnectTeamSpeak();
    }
    Say("setup complete. Use the desktop shortcut \"Reforger + Radio\" to play.", Tone.Ok);
  }

  bool InstallTeamSpeak() {
    Say("teamspeak 3 client " + M.TsVersion + ": downloading from teamspeak.com", Tone.Text);
    string file = Path.Combine(Env.Downloads, Path.GetFileName(new Uri(M.TsUrl).AbsolutePath));
    try {
      Fetch("TeamSpeak 3 Client " + M.TsVersion, M.TsUrl, file, M.TsSha, M.TsSize, true);
    } catch (Exception e) { Say("download failed: " + Short(e), Tone.Bad); return false; }
    Say("sha256 verified. Installing for this Windows user (no Overwolf) ...", Tone.Text);
    if (Env.ReadReg("HKLM", @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64", "Installed") != "1")
      Say("the Microsoft Visual C++ runtime comes with it: if Windows asks, allow \"Microsoft Visual C++ Redistributable\"", Tone.Warn);
    int code;
    try { code = TeamSpeak.RunInstaller(file, Env, Say, delegate { return Cancel; }); } catch (Exception e) { Say("installer failed: " + Short(e), Tone.Bad); return false; }
    Ts = TeamSpeak.Find(Env);
    if (Ts == null) { Say("the TeamSpeak installer ended (code " + code + ") but TeamSpeak isn't there. Install it from teamspeak.com, then SET UP again.", Tone.Bad); return false; }
    if (code == 3010) Say("teamspeak installed; Windows wants a restart for the Visual C++ runtime", Tone.Warn);
    else if (code != 0) Say("teamspeak installed, but the Visual C++ runtime may not have (code " + code + "). If TeamSpeak won't start, install it from aka.ms/vs/17/release/vc_redist.x64.exe", Tone.Warn);
    else Say("teamspeak 3 client " + Ts.Version + " installed ........ OK", Tone.Ok);
    try { File.Delete(file); } catch (Exception) { }
    Detect(false);
    return true;
  }

  // closeAsk: may ask to close a running TeamSpeak (it has the old plugin loaded); false skips the update for now.
  public bool InstallPlugin(bool closeAsk) {
    if (Ts == null) return false;
    if (PluginCurrent()) { Say("coalition radio plugin " + M.PluginVersion + " ........ OK", Tone.Ok); return true; }
    bool had = TeamSpeak.InstalledPluginHash(Ts, M.PluginDll) != null;
    if (TeamSpeak.Running().Length > 0 && had) {
      if (!closeAsk) { Say("plugin " + M.PluginVersion + " waits until TeamSpeak is closed", Tone.Warn); return true; }
      if (!CloseTeamSpeak()) { Say("plugin not updated: TeamSpeak is still open", Tone.Warn); return true; }
    }
    string file = Path.Combine(Env.Downloads, Path.GetFileName(new Uri(M.PluginUrl).AbsolutePath));
    try {
      Fetch(M.PluginName + " " + M.PluginVersion, M.PluginUrl, file, M.PluginSha, M.PluginSize, true);
      TeamSpeak.InstallPlugin(file, Ts, M.PluginDll, M.PluginDllSha);
      Settings.PluginVersion = M.PluginVersion;
      Settings.PluginDllSha = TeamSpeak.InstalledPluginHash(Ts, M.PluginDll);
      Settings.Save();
    } catch (Exception e) {
      Say("plugin install failed: " + Short(e), Tone.Bad);
      if (TeamSpeak.Running().Length > 0) Say("close TeamSpeak completely (tray icon > Quit) and try again", Tone.Warn);
      return false;
    }
    Say("coalition radio plugin " + M.PluginVersion + " installed to " + Ts.PluginsDir, Tone.Ok);
    if (M.PluginDllSha == null) Say("note: your unit's manifest follows Coalition's releases (the package is hash-checked, its DLL isn't pinned)", Tone.Dim);
    Detect(false);
    return true;
  }

  // question: asked first; null when the player already said yes (a RADIO CHECK fix button).
  bool CloseTeamSpeak(string question = "TeamSpeak is open and holds the old plugin. Close TeamSpeak to update it?") {
    if (TeamSpeak.Running().Length == 0) return true;
    if (question != null) {
      string a = Out == null ? null : Out.Ask(question, "CLOSE TEAMSPEAK", "NOT NOW");
      if (a != "CLOSE TEAMSPEAK") return false;
    }
    if (Out == null) return false;
    foreach (var p in TeamSpeak.Running()) { try { p.CloseMainWindow(); } catch (Exception) { } p.Dispose(); }
    for (int i = 0; i < 40; i++) { if (TeamSpeak.Running().Length == 0) return true; Thread.Sleep(250); }
    string b = Out.Ask("TeamSpeak is still running (it may be in the tray). Quit it from its tray icon, or force it closed?", "FORCE CLOSE", "I QUIT IT", "NOT NOW");
    if (b == "FORCE CLOSE") {
      foreach (var p in TeamSpeak.Running()) { try { p.Kill(); p.WaitForExit(5000); } catch (Exception) { } p.Dispose(); }
    } else if (b == "I QUIT IT") {
      for (int i = 0; i < 40 && TeamSpeak.Running().Length > 0; i++) Thread.Sleep(250);
    }
    return TeamSpeak.Running().Length == 0;
  }

  public void ConnectTeamSpeak() {
    if (Ts == null) { Say("TeamSpeak 3 isn't installed: run SET UP", Tone.Bad); return; }
    if (!EnsureServer(false)) { Say("no TeamSpeak server set: press CHANGE beside TS SERVER", Tone.Bad); return; }
    if (!EnsureCallsign(false)) return;
    string where = M.Host + ":" + M.Port;
    bool bookmark = !string.Equals(Settings.Bookmarked, where, StringComparison.OrdinalIgnoreCase);
    string uri = TeamSpeak.ConnectUri(M.Host, M.Port, Settings.Callsign, M.Password, bookmark ? M.Bookmark : null);
    try {
      TeamSpeak.Connect(Ts, uri);
      Say("teamspeak: connecting to " + where + " as " + Settings.Callsign + (bookmark ? " (bookmark added)" : ""), Tone.Ok);
      if (bookmark) { Settings.Bookmarked = where; Settings.Save(); }
    } catch (Exception e) { Say("couldn't start TeamSpeak: " + Short(e), Tone.Bad); }
  }

  // ---------------------------------------------------------------------------------------------- launch

  public void Launch() {
    Say("=== LAUNCH: REFORGER + RADIO ===", Tone.Accent);
    if (!EnsureCallsign(false)) { Say("launch needs a callsign", Tone.Bad); return; }
    Detect(false);
    if (Ts == null) { Say("TeamSpeak 3 isn't installed yet: setting up first", Tone.Warn); Setup(); if (Ts == null) return; }
    LinkProfileBeforeLaunch();
    bool tsRunning = TeamSpeak.Running().Length > 0;
    if (!PluginCurrent()) InstallPlugin(tsRunning);   // before TeamSpeak starts, an update needs no closing
    tsRunning = TeamSpeak.Running().Length > 0;
    bool haveServer = EnsureServer(false);

    bool connectSent = false;
    if (!tsRunning) {
      // A brand-new TeamSpeak shows its licence and welcome steps first; the connection waits behind them.
      if (!File.Exists(Path.Combine(Ts.ConfigDir, "settings.db")))
        Say("first start of TeamSpeak: accept TeamSpeak's licence and finish its welcome steps, then it connects", Tone.Warn);
      if (haveServer) { ConnectTeamSpeak(); connectSent = true; }
      else {
        try { TeamSpeak.Start(Ts); } catch (Exception e) { Say("couldn't start TeamSpeak: " + Short(e), Tone.Bad); }
        Say("teamspeak: started without a server (none set). Connect it to your unit's server yourself.", Tone.Warn);
      }
    } else if (haveServer) {
      var conns = TeamSpeak.Connections(Ts);
      if (conns == null) Say("teamspeak is open (can't see its connection: ClientQuery is off). Make sure it's on " + M.ServerName + ".", Tone.Dim);
      else if (ConnectedToUnit(conns) != null) Say("teamspeak: already connected to " + M.ServerName + " ............ OK", Tone.Ok);
      else { ConnectTeamSpeak(); connectSent = true; }
    }

    Say("waiting for the radio plugin to report in ...", Tone.Dim);
    bool fresh = false;
    for (int i = 0; i < 40 && !Cancel; i++) {
      if (Bridge.Fresh(new Bridge(Env.ProfileDir).Plugin(), DateTime.UtcNow)) { fresh = true; break; }
      Thread.Sleep(500);
    }
    var info = new Bridge(Env.ProfileDir).Plugin();
    if (fresh) Say("radio plugin " + info.Version + " online [" + info.Features + "] ........ OK", Tone.Ok);
    else Say("the radio plugin hasn't reported in: RADIO CHECK finds out why (it runs by itself if it stays quiet)", Tone.Warn);
    if (connectSent) WaitForConnection();
    Detect(false);

    if (GameRunning()) Say("arma reforger is already running", Tone.Dim);
    else if (NoGame) Say("(test run: not starting Steam)", Tone.Dim);
    else {
      try {
        Process.Start(new ProcessStartInfo("steam://rungameid/" + M.SteamAppId) { UseShellExecute = true });
        Say("arma reforger: launching through Steam ...", Tone.Ok);
      } catch (Exception e) { Say("couldn't open Steam (" + Short(e) + "): start Arma Reforger yourself; the radio works the same.", Tone.Warn); }
    }
    Say("join a server that runs Coalition VON: the radio links when you spawn in. Keep this window open to watch the link.", Tone.Text);
  }

  // Steam starting Reforger with -profile puts its radio files where the plugin never looks: offered before anything
  // starts, so the link needs no TeamSpeak restart. A game that's already running is the watch's to check (Tick).
  public void LinkProfileBeforeLaunch() {
    if (GameRunning()) return;
    string dir = SteamConfig.LaunchProfile(Env, M.SteamAppId);
    if (dir == null || Doctor.SameDir(dir, Env.ProfileDir)) return;
    var x = Doctor.LinkProfile("Steam starts Reforger with -profile, so it keeps its radio files in " + dir + ", but the Coalition plugin only looks in " + Env.ProfileDir + ".", dir);
    Say(x.Problem, Tone.Warn);
    Say("  " + x.Advice, Tone.Text);
    if (Out == null || Out.Ask(x.Question, x.FixLabel, "NOT NOW") != x.FixLabel) {
      declinedProfile = dir;
      Say("not linked: the radio can't link until it is (RADIO CHECK offers it again)", Tone.Warn);
      return;
    }
    try {
      Directory.CreateDirectory(dir);   // Reforger makes it on its first start with -profile; made now so the link has somewhere to point
      LinkProfileFolders(dir, false);   // LAUNCH starts and connects TeamSpeak next
    } catch (Exception e) { Say("couldn't link the folders: " + Short(e), Tone.Bad); }
  }

  // Up to 30 s for TeamSpeak to show up on the unit's server (ClientQuery); says what to do when it doesn't.
  void WaitForConnection() {
    List<TsConnection> conns = null;
    for (int i = 0; i < 30 && !Cancel; i++) {
      conns = TeamSpeak.Connections(Ts);
      if (ConnectedToUnit(conns) != null) { Say("teamspeak: connected to " + M.ServerName + " ............ OK", Tone.Ok); return; }
      Thread.Sleep(1000);
    }
    if (conns == null) Say("can't see TeamSpeak's connection (ClientQuery is off); check TeamSpeak shows " + M.ServerName, Tone.Dim);
    else Say("teamspeak isn't on " + M.ServerName + " yet: finish any TeamSpeak dialog (a server password?), then press CONNECT TEAMSPEAK", Tone.Warn);
  }

  // ClientQuery reports the server's IP; comms.json may give a name, whose addresses are looked up at most once a minute.
  public TsConnection ConnectedToUnit(List<TsConnection> conns) {
    if (conns == null || string.IsNullOrEmpty(M.Host)) return null;
    var ips = HostAddresses();
    foreach (var c in conns) {
      if (c.Port != 0 && c.Port != M.Port) continue;
      if (string.Equals(c.Ip, M.Host, StringComparison.OrdinalIgnoreCase) || Array.IndexOf(ips, c.Ip) >= 0) return c;
    }
    return null;
  }

  string[] HostAddresses() {
    if (hostIpsFor == M.Host && (DateTime.UtcNow - hostIpsAt).TotalSeconds < 60) return hostIps;
    var list = new string[0];
    IPAddress literal;
    if (!IPAddress.TryParse(M.Host, out literal))
      try { list = Array.ConvertAll(Dns.GetHostAddresses(M.Host), delegate(IPAddress a) { return a.ToString(); }); } catch (Exception) { }
    hostIps = list; hostIpsFor = M.Host; hostIpsAt = DateTime.UtcNow;
    return list;
  }

  public bool GameRunning() {
    foreach (var p in Process.GetProcesses()) {
      try {
        string n = p.ProcessName;
        if (n.StartsWith(M.GameProcess, StringComparison.OrdinalIgnoreCase)) return true;
      } catch (Exception) { } finally { p.Dispose(); }
    }
    return false;
  }

  // ---------------------------------------------------------------------------------------------- watching

  // Every couple of seconds while the window is open: TeamSpeak, the plugin, the game and the link between them. Only
  // changes go to the log.
  public void Tick() {
    var bridge = new Bridge(Env.ProfileDir);
    var now = DateTime.UtcNow;
    var running = TeamSpeak.Running();
    bool tsRunning = running.Length > 0;
    foreach (var p in running) p.Dispose();
    bool pluginFresh = Bridge.Fresh(bridge.Plugin(), now);
    bool game = GameRunning();
    var sd = bridge.Server();
    string elsewhere = game ? GameProfileElsewhere() : null;
    if (!game) { gameProfile = null; gameProfileRead = false; gameProfileTries = 0; }

    string channel = null;
    if (tsRunning && Ts != null) {
      if ((DateTime.Now - lastQuery).TotalSeconds >= 4) { lastConnections = TeamSpeak.Connections(Ts); lastQuery = DateTime.Now; }
      var unit = ConnectedToUnit(lastConnections);
      lock (Lock) {
        if (lastConnections == null) { St.TsLink = "OPEN"; St.TsLinkDetail = "connection not visible (ClientQuery off)"; St.TsLinkTone = Tone.Dim; }
        else if (unit != null) { St.TsLink = "CONNECTED"; St.TsLinkDetail = TeamSpeak.ChannelName(unit.ChannelPath) ?? M.ServerName; St.TsLinkTone = Tone.Ok; }
        else { St.TsLink = "NOT CONNECTED"; St.TsLinkDetail = string.IsNullOrEmpty(M.Host) ? "no server set" : "not on " + M.ServerName; St.TsLinkTone = Tone.Warn; }
      }
      if (unit != null) channel = unit.ChannelPath ?? "";
    } else {
      lastConnections = null;
      lock (Lock) { St.TsLink = "OFFLINE"; St.TsLinkDetail = "TeamSpeak isn't running"; St.TsLinkTone = Tone.Dim; }
    }

    double heartbeat = bridge.HeartbeatAge(now);
    bool offUnit = tsRunning && Ts != null && !string.IsNullOrEmpty(M.Host) && lastConnections != null && ConnectedToUnit(lastConnections) == null;
    var link = Bridge.Derive(game, tsRunning, pluginFresh, sd, heartbeat, channel, offUnit, elsewhere);
    lock (Lock) {
      St.Link = link;
      St.Game = game ? "RUNNING" : "NOT RUNNING"; St.GameTone = game ? Tone.Ok : Tone.Dim;
      if (Ts != null) { St.Ts = Ts.Version + (tsRunning ? "  RUNNING" : "  READY"); }
    }
    if (pluginFresh) pluginEverFresh = true;
    if (pluginFresh != lastPluginFresh && pluginEverFresh) Say(pluginFresh ? "radio plugin online" : "radio plugin went quiet (TeamSpeak closed?)", pluginFresh ? Tone.Ok : Tone.Warn);
    if (game != lastGame) Say(game ? "arma reforger started" : "arma reforger closed. Radio link down.", game ? Tone.Ok : Tone.Dim);
    string linkLine = "radio link: " + link.Text + " - " + link.Detail;
    if (linkLine != lastLinkLine && link.Link != Link.Off) Say(linkLine, link.Tone);
    lastLinkLine = linkLine; lastGame = game; lastPluginFresh = pluginFresh;
    WatchForStuck(link, tsRunning, pluginFresh, Doctor.HeartbeatFresh(heartbeat), elsewhere != null && Doctor.SameDir(elsewhere, declinedProfile), DateTime.Now);
    PluginStatus(false);
    Changed();
  }

  // The folder the running game keeps its radio files in when it isn't the plugin's (null: it is). The game's command
  // line is read when it starts (a few tries while Windows won't show it), then compared on every tick, so a link made
  // meanwhile counts at once.
  string GameProfileElsewhere() {
    if (!gameProfileRead) {
      bool running;
      string options;
      gameProfile = Doctor.GameProfile(Env, M, out running, out options);
      gameProfileRead = options != null || ++gameProfileTries >= 3;
    }
    return gameProfile != null && !Doctor.SameDir(gameProfile, Env.ProfileDir) ? gameProfile : null;
  }

  // When the link stays stuck, RADIO CHECK runs by itself, once for each time it gets stuck: the window picks it up
  // (TakePendingCheck) and runs it as a job, so its fix questions show like any other. Waits are long enough that normal
  // steps (loading in, the respawn screen, the plugin's move) don't trigger it; a moved profile folder can't fix itself,
  // so it doesn't wait (unless LAUNCH already asked). at: the time now (the tests pass their own).
  public void WatchForStuck(LinkState link, bool tsRunning, bool pluginFresh, bool onRadioServer, bool splitDeclined, DateTime at) {
    string now = null;
    int wait = 0;
    bool pluginInstalled = Ts != null && TeamSpeak.InstalledPluginHash(Ts, M.PluginDll) != null;
    if (link.Link == Link.Split) now = splitDeclined ? null : "Reforger keeps its radio files in another folder";
    else if (link.Link == Link.NoPlugin || (link.Link == Link.Off && tsRunning && pluginInstalled && !pluginFresh)) { now = "the radio plugin isn't loaded"; wait = 45; }
    else if (link.Link == Link.Standby && onRadioServer) { now = "you haven't spawned in"; wait = 150; }
    else if (link.Link == Link.Standby) { now = "no radio signal from the game"; wait = 180; }
    else if (link.Link == Link.Joining || link.Link == Link.OffServer) { now = "teamspeak hasn't linked the radio"; wait = 30; }
    if (now != stuck) { stuck = now; stuckSince = at; stuckChecked = false; return; }
    if (now == null || stuckChecked || (at - stuckSince).TotalSeconds < wait) return;
    stuckChecked = true;
    lock (Lock) pendingCheck = now;
  }

  // The automatic RADIO CHECK waiting to run (its reason), once.
  public string TakePendingCheck() {
    lock (Lock) { string p = pendingCheck; pendingCheck = null; return p; }
  }

  // ---------------------------------------------------------------------------------------------- radio check

  // Everything between the game and TeamSpeak on this PC (Doctor), with a fix button for each problem it can fix itself.
  // why: the automatic run's reason (prints only what's wrong); null for the RADIO CHECK button (prints all it saw too,
  // for COPY LOG).
  public void RadioCheck(string why) {
    Say(why == null ? "=== RADIO CHECK ===" : "=== RADIO CHECK: " + why + " ===", Tone.Accent);
    Detect(false);
    var facts = Doctor.Gather(this);
    if (why == null) foreach (var l in Doctor.Report(facts, M)) Say(l.Key, l.Value);
    var found = Doctor.Diagnose(facts, M);
    int problems = 0, fixedCount = 0;
    foreach (var x in found) {
      if (x.Tone == Tone.Bad) problems++;
      Say((x.Tone == Tone.Bad ? "PROBLEM: " : "") + x.Problem, x.Tone);
      if (!string.IsNullOrEmpty(x.Advice)) Say("  " + x.Advice, Tone.Text);
      if (x.Fix == Fix.None || Out == null) continue;
      string a = Out.Ask(x.Question ?? "Fix it now?", x.FixLabel, "NOT NOW");
      if (a != x.FixLabel) { Say("left as it is: press RADIO CHECK when you want the fix", Tone.Dim); continue; }
      if (ApplyFix(x)) fixedCount++;
    }
    if (found.Count == 0)
      Say(why == null ? "radio check: everything on this PC looks right. If the radio still fails, press COPY LOG and send it to your unit's admins."
                      : "radio check: nothing wrong on this PC", Tone.Ok);
    else if (problems > 0)
      Say("radio check: " + problems + (problems == 1 ? " problem, " : " problems, ") + fixedCount + " fixed" +
          (fixedCount < problems ? ". Stuck? Press COPY LOG and send it to your unit's admins." : ". Watch the RADIO LINK light."), fixedCount < problems ? Tone.Warn : Tone.Ok);
    Detect(false);
  }

  // True when the problem is gone; the guided ones (a download, a Windows page) say what to do and return false.
  bool ApplyFix(Finding x) {
    try {
      switch (x.Fix) {
        case Fix.LinkProfile:
          return LinkProfileFolders(x.Target, true);
        case Fix.PluginIntoRunningTs: {
          var other = TeamSpeak.AtDir(Env, x.Target);
          if (other == null) { Say("no TeamSpeak 3 in " + x.Target, Tone.Bad); return false; }
          if (!CloseTeamSpeak(null)) { Say("not installed: TeamSpeak is still open", Tone.Warn); return false; }
          Ts = other;
          if (!InstallPlugin(false)) return false;
          ConnectTeamSpeak();
          return true;
        }
        case Fix.UpdateTeamSpeak:
          if (!CloseTeamSpeak(null)) { Say("not updated: TeamSpeak is still open", Tone.Warn); return false; }
          if (!InstallTeamSpeak() || !InstallPlugin(false)) return false;
          ConnectTeamSpeak();
          return true;
        case Fix.RestartTeamSpeak:
          if (!CloseTeamSpeak(null)) { Say("TeamSpeak is still open: quit it from its tray icon, then LAUNCH", Tone.Warn); return false; }
          ConnectTeamSpeak();
          return true;
        case Fix.VcRuntime:
          Open(Doctor.VcRuntimeUrl);
          Say("your browser is downloading vc_redist.x64.exe from Microsoft: run it, then RESTART TEAMSPEAK (RADIO CHECK offers it)", Tone.Accent);
          return false;
        case Fix.FolderAccess:
          Open(Doctor.FolderAccessPage);
          Say("Windows Security is open: Allow an app through Controlled folder access, add ArmaReforgerSteam.exe and ts3client_win64.exe", Tone.Accent);
          return false;
        case Fix.Connect:
          ConnectTeamSpeak();
          return true;
      }
    } catch (Exception e) { Say("fix failed: " + Short(e), Tone.Bad); }
    return false;
  }

  // The plugin's folder opens the game's from now on. TeamSpeak closes first (its plugin writes in that folder);
  // reconnect starts it again on the unit's server.
  bool LinkProfileFolders(string target, bool reconnect) {
    bool tsOpen = TeamSpeak.Running().Length > 0;
    if (tsOpen && !CloseTeamSpeak(null)) { Say("not linked: TeamSpeak is still open", Tone.Warn); return false; }
    string aside = Doctor.LinkFolders(Env.ProfileDir, target);
    Say("linked: " + Env.ProfileDir + " now opens " + target + " ........ OK", Tone.Ok);
    if (aside != null) Say("the folder that was there is kept as " + aside, Tone.Dim);
    if (tsOpen && reconnect) ConnectTeamSpeak();
    return true;
  }

  static void Open(string target) { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }

  // ---------------------------------------------------------------------------------------------- shortcuts, removal

  public const string LaunchShortcutName = "Reforger + Radio.lnk";

  public void WriteShortcuts() {
    try {
      string icon = Env.HomeExe + ",0";
      Directory.CreateDirectory(Env.StartMenuDir);
      Shortcuts.Write(Path.Combine(Env.Desktop, LaunchShortcutName), Env.HomeExe, "--launch", Env.Home, icon, "TeamSpeak with the Coalition VON radio, then Arma Reforger");
      Shortcuts.Write(Path.Combine(Env.StartMenuDir, LaunchShortcutName), Env.HomeExe, "--launch", Env.Home, icon, "TeamSpeak with the Coalition VON radio, then Arma Reforger");
      Shortcuts.Write(Path.Combine(Env.StartMenuDir, "Bifrost Comms.lnk"), Env.HomeExe, "", Env.Home, icon, "TeamSpeak 3 and Coalition VON setup");
      Say("shortcuts: desktop + Start menu > Bifrost Comms ............ OK", Tone.Ok);
    } catch (Exception e) { Say("couldn't make the shortcuts: " + Short(e), Tone.Warn); }
  }

  // Shortcuts, settings, downloads and the program itself. TeamSpeak and the Coalition plugin stay: they're TeamSpeak's.
  public void Remove() {
    string a = Out == null ? null : Out.Ask("Remove Bifrost Comms (shortcuts, settings, this program)? TeamSpeak and the plugin stay.", "REMOVE", "CANCEL");
    if (a != "REMOVE") { Say("nothing removed", Tone.Dim); return; }
    foreach (var f in new[] { Path.Combine(Env.Desktop, LaunchShortcutName), Path.Combine(Env.StartMenuDir, LaunchShortcutName), Path.Combine(Env.StartMenuDir, "Bifrost Comms.lnk") })
      try { if (File.Exists(f)) File.Delete(f); } catch (Exception) { }
    try { if (Directory.Exists(Env.StartMenuDir) && Directory.GetFileSystemEntries(Env.StartMenuDir).Length == 0) Directory.Delete(Env.StartMenuDir); } catch (Exception) { }
    try { if (Directory.Exists(Env.Downloads)) Directory.Delete(Env.Downloads, true); } catch (Exception) { }
    try { if (File.Exists(Settings.File)) File.Delete(Settings.File); } catch (Exception) { }
    Say("removed. TeamSpeak 3 and the Coalition plugin are still installed (remove them in Windows Settings > Apps / TeamSpeak's addons).", Tone.Ok);
    // The program can't delete its own folder while it runs: a hidden cmd does it a moment after it closes.
    if (Directory.Exists(Env.Home) && Out != null)
      Out.Restart("cmd.exe", "/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"" + Env.Home + "\"");
  }

  // ---------------------------------------------------------------------------------------------- helpers

  void Fetch(string label, string url, string dest, string sha, long size, bool followRedirects) {
    bool downloaded = Net.Download(url, dest, sha, size, followRedirects,
      delegate(long got, long total, double rate) { if (Out != null) Out.Progress(label, got, total, rate); },
      delegate { return Cancel; });
    if (Out != null) Out.Progress(null, 0, 0, 0);
    Say(downloaded ? label + ": " + Net.Size(new FileInfo(dest).Length) + ", sha256 " + sha.Substring(0, 12) + "... verified" : label + ": already downloaded, sha256 verified", Tone.Dim);
  }

  public static string Short(Exception e) {
    if (e is OperationCanceledException) return "cancelled";
    string m = e.Message ?? e.GetType().Name;
    return m.Length > 160 ? m.Substring(0, 160) + "..." : m;
  }
}

static class Shortcuts {
  public static void Write(string lnk, string target, string args, string workDir, string icon, string description) {
    object shell = null, sc = null;
    try {
      shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
      sc = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
      Set(sc, "TargetPath", target); Set(sc, "Arguments", args); Set(sc, "WorkingDirectory", workDir);
      Set(sc, "IconLocation", icon); Set(sc, "Description", description);
      sc.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
    } finally {
      if (sc != null) Marshal.FinalReleaseComObject(sc);
      if (shell != null) Marshal.FinalReleaseComObject(shell);
    }
  }

  static void Set(object o, string name, string value) { o.GetType().InvokeMember(name, BindingFlags.SetProperty, null, o, new object[] { value }); }
}

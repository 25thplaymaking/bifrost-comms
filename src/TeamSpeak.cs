// TeamSpeak 3 on the player's PC: finding the client, installing it, installing the Coalition plugin, asking the running
// client what it's connected to (ClientQuery), and the ts3server:// link that connects it to the unit's server.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

class TsInstall {
  public string Dir, Exe, Mode, Version;
  public bool ConfigInInstallDir;   // the installer's "save configuration in the installation folder" choice (ConfigLocation 1)
  public string AppData;

  // Where the client keeps its settings and plugins; the plugin goes where TeamSpeak's own package installer puts it.
  public string ConfigDir { get { return ConfigInInstallDir ? Path.Combine(Dir, "config") : Path.Combine(AppData, "TS3Client"); } }
  public string PluginsDir { get { return Path.Combine(ConfigDir, "plugins"); } }
}

class TsConnection { public int Handler; public string Ip, ChannelPath; public int Port; }

static class TeamSpeak {
  public const string ProcessName = "ts3client_win64";
  const string RegKey = @"Software\TeamSpeak 3 Client";

  // HKCU first (a per-user install), then HKLM (for all users); then the two default folders in case the registry was
  // cleaned. reg(hive, value) reads RegKey's value from "HKCU" or "HKLM", or null.
  public static TsInstall Find(Env env) {
    foreach (var hive in new[] { "HKCU", "HKLM" }) {
      string dir = env.ReadReg(hive, RegKey, "");
      if (string.IsNullOrEmpty(dir)) continue;
      var t = At(env, dir.Trim().Trim('"'), env.ReadReg(hive, RegKey, "InstallMode"), env.ReadReg(hive, RegKey, "ConfigLocation"));
      if (t != null) return t;
    }
    foreach (var dir in new[] { Path.Combine(env.LocalAppData, "Programs", "TeamSpeak 3 Client"), Path.Combine(env.ProgramFiles, "TeamSpeak 3 Client") }) {
      var t = At(env, dir, null, null);
      if (t != null) return t;
    }
    return null;
  }

  static TsInstall At(Env env, string dir, string mode, string configLocation) {
    string exe = Path.Combine(dir, ProcessName + ".exe");
    if (!File.Exists(exe)) return null;
    var t = new TsInstall { Dir = dir, Exe = exe, Mode = mode ?? "", AppData = env.AppData, ConfigInInstallDir = configLocation == "1" };
    try {
      var v = FileVersionInfo.GetVersionInfo(exe);   // ProductVersion reads "3, 6, 2, 0"; the parts don't
      t.Version = v.FileMajorPart + "." + v.FileMinorPart + "." + v.FileBuildPart;
    } catch (Exception) { t.Version = "?"; }
    return t;
  }

  // The new TeamSpeak (5/6) can't run TeamSpeak 3 plugins, so Coalition radio needs the 3 client next to it.
  public static bool NewClientPresent(Env env) {
    return File.Exists(Path.Combine(env.LocalAppData, "Programs", "TeamSpeak", "TeamSpeak.exe"));
  }

  public static Process[] Running() { return Process.GetProcessesByName(ProcessName); }

  // ---------------------------------------------------------------------------------------------- installing the client

  // TeamSpeak's own installer, unattended, for this Windows user only (no administrator prompt for TeamSpeak itself). Its
  // NSIS script (3.6.2) asks install.overwolf.com whether to offer Overwolf and, in silent mode, starts
  // OverwolfInstallerDownloader.exe -silent whenever that request succeeds, which it does today. Two guards keep Overwolf
  // off the PC: a folder named %TEMP%\overwolfdummy, which is where the installer saves that answer, so saving fails and
  // the answer never counts; and any Overwolf installer process started while it runs is ended. The Visual C++ runtime the
  // installer runs may still ask Windows for permission when it's missing.
  public static int RunInstaller(string installer, Env env, Action<string, Tone> log, Func<bool> cancel) {
    string dummy = Path.Combine(env.Temp, "overwolfdummy");
    bool armed = OverwolfGuard.Arm(dummy);
    bool overwolfBefore = OverwolfGuard.Installed(env);
    var started = DateTime.Now;
    int code;
    try {
      var psi = new ProcessStartInfo(installer, "/S /CurrentUser") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(installer) };
      psi.EnvironmentVariables["__COMPAT_LAYER"] = "RunAsInvoker";   // run as this user: the installer asks for "highest available"
      using (var p = Process.Start(psi)) {
        DateTime? exited = null;
        // Watch until it ends, then a little longer: the installer starts the Overwolf downloader without waiting for it.
        while (exited == null || (DateTime.Now - exited.Value).TotalSeconds < 15) {
          foreach (var name in OverwolfGuard.Stopped(started)) log("BLOCKED " + name + " (TeamSpeak's installer bundles Overwolf)", Tone.Warn);
          if (exited == null && p.WaitForExit(150)) exited = DateTime.Now;
          else if (exited != null) Thread.Sleep(150);
          if (exited == null && (DateTime.Now - started).TotalMinutes > 15) throw new IOException("the TeamSpeak installer didn't finish in 15 minutes");
          if (exited == null && cancel != null && cancel()) { try { p.Kill(); } catch (Exception) { } throw new OperationCanceledException(); }
        }
        code = p.ExitCode;
      }
    } finally {
      OverwolfGuard.Disarm(dummy, armed);
    }
    if (!overwolfBefore && OverwolfGuard.Installed(env))
      log("Overwolf appeared on this PC during the install. Remove it in Windows Settings > Apps if you don't want it.", Tone.Bad);
    return code;
  }

  // ---------------------------------------------------------------------------------------------- the Coalition plugin

  // The status rail asks every couple of seconds: the hash is kept until the file's size or time changes.
  static string hashedFile, hashedValue;
  static long hashedLength;
  static DateTime hashedTime;

  public static string InstalledPluginHash(TsInstall ts, string dll) {
    string f = Path.Combine(ts.PluginsDir, dll);
    try {
      var info = new FileInfo(f);
      if (!info.Exists) return null;
      lock (typeof(TeamSpeak)) {
        if (f == hashedFile && info.Length == hashedLength && info.LastWriteTimeUtc == hashedTime) return hashedValue;
        string h = Net.Hash(f);
        hashedFile = f; hashedLength = info.Length; hashedTime = info.LastWriteTimeUtc; hashedValue = h;
        return h;
      }
    } catch (IOException) { return null; } catch (UnauthorizedAccessException) { return null; }
  }

  // Copies everything under plugins/ in the .ts3_plugin package into the client's plugins folder, as TeamSpeak's package
  // installer does. The main DLL must be in the package and, when the manifest knows its hash, match it. New files are
  // written beside the old ones first; then every old file moves aside and the new ones move in, and if any step fails
  // (a plugin TeamSpeak still has loaded) everything moves back, so the folder holds one whole version or the other.
  public static void InstallPlugin(string package, TsInstall ts, string dll, string dllSha) {
    string root = Path.GetFullPath(ts.PluginsDir);
    Directory.CreateDirectory(root);
    // An old copy TeamSpeak still had loaded last time moved aside but couldn't be deleted then.
    foreach (var f in Directory.GetFiles(root, "*.bifrost-old", SearchOption.AllDirectories)) try { File.Delete(f); } catch (Exception) { }
    var staged = new List<KeyValuePair<string, string>>();
    try {
      using (var zip = ZipFile.OpenRead(package)) {
        bool found = false;
        foreach (var e in zip.Entries) {
          string name = e.FullName.Replace('\\', '/');
          if (!name.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase) || name.EndsWith("/")) continue;
          string rel = SafeRelative(name.Substring("plugins/".Length));
          if (rel == null) throw new IOException("the plugin package has an unsafe path: " + name);
          string dest = Path.GetFullPath(Path.Combine(root, rel));
          if (!dest.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("the plugin package has an unsafe path: " + name);
          Directory.CreateDirectory(Path.GetDirectoryName(dest));
          string tmp = dest + ".bifrost-new";
          e.ExtractToFile(tmp, true);
          staged.Add(new KeyValuePair<string, string>(tmp, dest));
          if (string.Equals(rel, dll, StringComparison.OrdinalIgnoreCase)) {
            found = true;
            if (dllSha != null && Net.Hash(tmp) != dllSha) throw new IOException(dll + " in the package doesn't match its pinned SHA-256");
          }
        }
        if (!found) throw new IOException("the plugin package has no plugins/" + dll);
      }
      Swap(staged);
    } finally {
      foreach (var kv in staged) if (File.Exists(kv.Key)) try { File.Delete(kv.Key); } catch (IOException) { }
    }
  }

  // staged: new file -> its place. All or nothing.
  static void Swap(List<KeyValuePair<string, string>> staged) {
    var asideDone = new List<string>();
    var movedIn = new List<string>();
    try {
      foreach (var kv in staged) {
        if (!File.Exists(kv.Value)) continue;
        string aside = kv.Value + ".bifrost-old";
        if (File.Exists(aside)) File.Delete(aside);
        File.Move(kv.Value, aside);   // fails while TeamSpeak has it loaded
        asideDone.Add(kv.Value);
      }
      foreach (var kv in staged) { File.Move(kv.Key, kv.Value); movedIn.Add(kv.Value); }
    } catch (Exception) {
      foreach (var f in movedIn) try { File.Delete(f); } catch (Exception) { }
      foreach (var f in asideDone) try { File.Move(f + ".bifrost-old", f); } catch (Exception) { }
      throw;
    }
    foreach (var f in asideDone) try { File.Delete(f + ".bifrost-old"); } catch (Exception) { }
  }

  // "sub/file.dll" stays; "../x", "/x", "C:x" and empty parts are refused (null).
  public static string SafeRelative(string rel) {
    if (string.IsNullOrEmpty(rel) || rel.StartsWith("/") || rel.IndexOf(':') >= 0) return null;
    var parts = rel.Split('/');
    foreach (var p in parts) if (p.Length == 0 || p == "." || p == ".." || p.Trim() != p) return null;
    return string.Join(Path.DirectorySeparatorChar.ToString(), parts);
  }

  // ---------------------------------------------------------------------------------------------- connecting

  // ts3server://ts.example.com?port=9987&nickname=PFC%20J.%20Doe&password=...&addbookmark=Example%20Unit
  public static string ConnectUri(string host, int port, string nickname, string password, string bookmark) {
    var sb = new StringBuilder("ts3server://").Append(host).Append("?port=").Append(port);
    if (!string.IsNullOrEmpty(nickname)) sb.Append("&nickname=").Append(Uri.EscapeDataString(nickname));
    if (!string.IsNullOrEmpty(password)) sb.Append("&password=").Append(Uri.EscapeDataString(password));
    if (!string.IsNullOrEmpty(bookmark)) sb.Append("&addbookmark=").Append(Uri.EscapeDataString(bookmark));
    return sb.ToString();
  }

  // Starts the client (or hands the link to the one already running) connected to the unit's server.
  public static void Connect(TsInstall ts, string uri) {
    Process.Start(new ProcessStartInfo(ts.Exe, "\"" + uri + "\"") { UseShellExecute = true, WorkingDirectory = ts.Dir });
  }

  public static void Start(TsInstall ts) {
    Process.Start(new ProcessStartInfo(ts.Exe) { UseShellExecute = true, WorkingDirectory = ts.Dir });
  }

  // ---------------------------------------------------------------------------------------------- ClientQuery

  // What the running client is connected to, through its ClientQuery plugin (127.0.0.1:25639, the API key in the client's
  // own clientquery.ini). Null when the client isn't running, ClientQuery is off, or it doesn't answer.
  public static List<TsConnection> Connections(TsInstall ts, int port = 25639) {
    string key = ApiKey(Path.Combine(ts.ConfigDir, "clientquery.ini"));
    if (key == null) return null;
    try {
      using (var tcp = new TcpClient()) {
        var ar = tcp.BeginConnect(IPAddress.Loopback, port, null, null);
        if (!ar.AsyncWaitHandle.WaitOne(800) || !tcp.Connected) return null;
        tcp.EndConnect(ar);
        tcp.ReceiveTimeout = 1500; tcp.SendTimeout = 1500;
        var q = new QueryLines(tcp.GetStream());
        q.SkipBanner();
        if (q.Command("auth apikey=" + key) == null) return null;
        var handlers = q.Command("serverconnectionhandlerlist");
        if (handlers == null) return null;
        var list = new List<TsConnection>();
        foreach (var h in handlers) {
          int id;
          if (!int.TryParse(Get(h, "schandlerid"), out id)) continue;
          if (q.Command("use schandlerid=" + id) == null) continue;
          var info = q.Command("serverconnectinfo");   // error 1794 "not connected" on an empty tab
          if (info == null || info.Count == 0) continue;
          int serverPort; int.TryParse(Get(info[0], "port"), out serverPort);
          var c = new TsConnection { Handler = id, Ip = Get(info[0], "ip"), Port = serverPort };
          var chan = q.Command("channelconnectinfo");
          if (chan != null && chan.Count > 0) c.ChannelPath = Get(chan[0], "path");
          list.Add(c);
        }
        q.Send("quit");
        return list;
      }
    } catch (Exception) { return null; }
  }

  public static string ApiKey(string ini) {
    try {
      if (!File.Exists(ini)) return null;
      foreach (var line in File.ReadAllLines(ini, Encoding.UTF8)) {
        string l = line.Trim();
        if (l.StartsWith("api_key=", StringComparison.OrdinalIgnoreCase)) {
          string k = l.Substring(8).Trim();
          return k.Length > 0 && k.IndexOfAny(new[] { ' ', '\r', '\n' }) < 0 ? k : null;
        }
      }
    } catch (IOException) { }
    return null;
  }

  static string Get(Dictionary<string, string> d, string k) { string v; return d.TryGetValue(k, out v) ? v : null; }

  // The last part of a channel path ("Unit/Reforger In-Game (VON)" -> "Reforger In-Game (VON)").
  public static string ChannelName(string path) {
    if (string.IsNullOrEmpty(path)) return path;
    int i = path.LastIndexOf('/');
    return i >= 0 ? path.Substring(i + 1) : path;
  }
}

// ServerQuery/ClientQuery text protocol: lines end in "\n\r", rows split on '|', key=value pairs on ' ', and an
// "error id=0 msg=ok" line ends every answer.
class QueryLines {
  readonly Stream s;
  readonly List<byte> pending = new List<byte>();

  public QueryLines(Stream s) { this.s = s; }

  public void Send(string cmd) { var b = Encoding.UTF8.GetBytes(cmd + "\n"); s.Write(b, 0, b.Length); }

  public string ReadLine() {
    var buf = new byte[1];
    while (true) {
      int i = pending.IndexOf((byte)'\n');
      if (i >= 0) {
        string line = Encoding.UTF8.GetString(pending.GetRange(0, i).ToArray()).Trim('\r');
        pending.RemoveRange(0, i + 1);
        if (pending.Count > 0 && pending[0] == '\r') pending.RemoveAt(0);
        return line;
      }
      int n = s.Read(buf, 0, 1);
      if (n <= 0) throw new IOException("closed");
      pending.Add(buf[0]);
      if (pending.Count > 65536) throw new IOException("line too long");
    }
  }

  // The banner: "TS3 Client", the welcome text, maybe an auth hint, then "selected schandlerid=N".
  public void SkipBanner() {
    for (int i = 0; i < 6; i++) { string l = ReadLine(); if (l.StartsWith("selected ")) return; }
  }

  // The rows of the answer, or null when it ends in an error.
  public List<Dictionary<string, string>> Command(string cmd) {
    Send(cmd);
    var rows = new List<Dictionary<string, string>>();
    for (int guard = 0; guard < 200; guard++) {
      string line = ReadLine();
      if (line.Length == 0) continue;
      if (line.StartsWith("error ")) {
        var e = Rows(line.Substring(6));
        string id; e[0].TryGetValue("id", out id);
        return id == "0" ? rows : null;
      }
      if (line.StartsWith("notify")) continue;
      rows.AddRange(Rows(line));
    }
    return null;
  }

  public static List<Dictionary<string, string>> Rows(string line) {
    var list = new List<Dictionary<string, string>>();
    foreach (var row in line.Split('|')) {
      var d = new Dictionary<string, string>();
      foreach (var pair in row.Split(' ')) {
        if (pair.Length == 0) continue;
        int eq = pair.IndexOf('=');
        if (eq < 0) d[pair] = "";
        else d[pair.Substring(0, eq)] = Unescape(pair.Substring(eq + 1));
      }
      list.Add(d);
    }
    return list;
  }

  public static string Unescape(string v) {
    var sb = new StringBuilder(v.Length);
    for (int i = 0; i < v.Length; i++) {
      char c = v[i];
      if (c != '\\' || i + 1 >= v.Length) { sb.Append(c); continue; }
      char n = v[++i];
      switch (n) {
        case 's': sb.Append(' '); break;
        case 'p': sb.Append('|'); break;
        case '/': sb.Append('/'); break;
        case '\\': sb.Append('\\'); break;
        case 'n': sb.Append('\n'); break;
        case 'r': sb.Append('\r'); break;
        case 't': sb.Append('\t'); break;
        default: sb.Append(n); break;
      }
    }
    return sb.ToString();
  }
}

static class OverwolfGuard {
  static readonly string[] Names = { "OverwolfInstallerDownloader", "OverwolfTeamSpeakInstaller", "OverwolfSetup", "OverwolfInstaller" };

  // Makes path a folder. Returns true when this call made it (so Disarm removes it again).
  public static bool Arm(string path) {
    try {
      if (File.Exists(path)) File.Delete(path);
      if (Directory.Exists(path)) return false;
      Directory.CreateDirectory(path);
      return true;
    } catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; }
  }

  public static void Disarm(string path, bool armed) {
    if (!armed) return;
    try { if (Directory.Exists(path) && Directory.GetFileSystemEntries(path).Length == 0) Directory.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
  }

  // Ends Overwolf installer processes that started after since, and names them. A player's own Overwolf app, already
  // running before the install, isn't an installer and isn't touched.
  public static List<string> Stopped(DateTime since) {
    var stopped = new List<string>();
    foreach (var name in Names)
      foreach (var p in Process.GetProcessesByName(name)) {
        try {
          if (p.StartTime >= since.AddSeconds(-2)) { p.Kill(); stopped.Add(name + ".exe"); }
        } catch (Exception) { } finally { p.Dispose(); }
      }
    return stopped;
  }

  public static bool Installed(Env env) {
    return Directory.Exists(Path.Combine(env.LocalAppData, "Overwolf")) || Directory.Exists(Path.Combine(env.ProgramFilesX86, "Overwolf"));
  }
}

// Bifrost Comms' checks: everything that doesn't need TeamSpeak or Steam, against a sandbox folder, a fake registry, a
// local HTTP server and a fake ClientQuery. Run build\CommsTests.exe (Windows) or mono build/CommsTests.exe; it exits 1
// when a check fails. It also paints the window in several states into build/renders/ (skipped where there's no display).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

static class CommsTests {
  static int passed, failed;
  static string root;

  static int Main(string[] args) {
    root = Path.Combine(Path.GetTempPath(), "bifrost-comms-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
    Directory.CreateDirectory(root);
    try {
      Versions(); ManifestParsing(); SafePaths(); PluginInstall(); FindTeamSpeak(); Uris(); QueryText(); ClientQueryFake();
      BridgeFiles(); LinkStates(); SettingsFile(); UnitFile(); ServerPrompt(); ServerMatch(); Builds(); Downloads(); Overwolf(); Wrapping();
      GameOptions(); SteamOptions(); Diagnoses(); RadioCheckRun(); StuckWatch(); PcChecks(); FolderLink(); LaunchLink();
      string renders = args.Length > 0 ? args[0] : Path.Combine("build", "renders");
      Renders(renders);
    } finally {
      try { RemoveLinks(root); Directory.Delete(root, true); } catch (Exception e) { Console.WriteLine("  (temp folder not fully removed: " + e.GetType().Name + ": " + e.Message + ")"); }
    }
    Console.WriteLine();
    Console.WriteLine(passed + " passed, " + failed + " failed");
    return failed == 0 ? 0 : 1;
  }

  // The folder links the checks make go first, on their own: a recursive delete over a junction is refused.
  static void RemoveLinks(string dir) {
    foreach (var d in Directory.GetDirectories(dir)) {
      if (Doctor.IsLink(d)) Directory.Delete(d, false);
      else RemoveLinks(d);
    }
  }

  static void Check(string name, bool ok) {
    if (ok) passed++; else failed++;
    Console.WriteLine((ok ? "  ok   " : "  FAIL ") + name);
  }

  static void Throws<T>(string name, Action a) where T : Exception {
    try { a(); Check(name + " (no exception)", false); } catch (T) { Check(name, true); } catch (Exception e) { Check(name + " (got " + e.GetType().Name + ": " + e.Message + ")", false); }
  }

  static Env Sandbox(string name, Dictionary<string, string> reg) {
    string r = Path.Combine(root, name);
    var env = new Env {
      AppData = Path.Combine(r, "AppData", "Roaming"), LocalAppData = Path.Combine(r, "AppData", "Local"), Documents = Path.Combine(r, "Documents"),
      Temp = Path.Combine(r, "Temp"), Desktop = Path.Combine(r, "Desktop"), StartMenu = Path.Combine(r, "StartMenu"),
      ProgramFiles = Path.Combine(r, "Program Files"), ProgramFilesX86 = Path.Combine(r, "Program Files (x86)"),
      Reg = delegate(string hive, string key, string value) { string v; return reg != null && reg.TryGetValue(hive + "|" + key + "|" + value, out v) ? v : null; },
    };
    foreach (var d in new[] { env.AppData, env.LocalAppData, env.Documents, env.Temp, env.Desktop, env.StartMenu, env.ProgramFiles }) Directory.CreateDirectory(d);
    return env;
  }

  // Answers the program's questions from a queue (null = the player cancelled), as the window would.
  class FakeConsole : IConsole {
    public readonly Queue<string> Answers = new Queue<string>();
    public readonly List<string> Prompts = new List<string>(), Lines = new List<string>();
    public void Line(string text, Tone tone) { lock (Lines) Lines.Add(text); }
    public void Progress(string label, long got, long total, double rate) { }
    public string Ask(string question, params string[] choices) { Prompts.Add(question); return Answers.Count > 0 ? Answers.Dequeue() : null; }
    public string Input(string prompt, string current, Func<string, string> problem, int maxLength) {
      Prompts.Add(prompt);
      string a = Answers.Count > 0 ? Answers.Dequeue() : null;
      return a != null && a.Length <= maxLength && (problem == null || problem(a) == null) ? a : null;
    }
    public void Restart(string exe, string args) { }
    public void Changed() { }
  }

  // ---------------------------------------------------------------------------------------------- versions, manifest

  static void Versions() {
    Check("version 1.0.10 > 1.0.9", Ver.Compare("1.0.10", "1.0.9") > 0);
    Check("version 3.0 == 3.0.0", Ver.Compare("3.0", "3.0.0") == 0);
    Check("version 3.0.1 < 3.1", Ver.Compare("3.0.1", "3.1") < 0);
    Check("version 'Release' is invalid", !Ver.Valid("Release") && !Ver.Valid("") && !Ver.Valid("1..2") && !Ver.Valid("1.0-beta"));
  }

  const string Sample = @"{""schema"":1,
    ""launcher"":{""version"":""1.2.0"",""sha256"":""AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"",""size"":123,""url"":""/releases/BifrostComms-1.2.0.exe"",""notes"":""faster""},
    ""teamspeak"":{""version"":""3.6.3"",""url"":""https://files.teamspeak-services.com/x.exe"",""sha256"":""bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"",""size"":5},
    ""plugin"":{""name"":""Coalition TeamSpeak Plugin"",""version"":""3.0.2"",""url"":""https://github.com/x.ts3_plugin"",""sha256"":""cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"",""size"":7,
                ""dll"":""CRF_Teamspeak_Plugin_Main.dll"",""dll_sha256"":null},
    ""server"":{""name"":""Example Unit"",""host"":""10.1.2.3"",""port"":9988,""bookmark"":""EXU"",""password"":""pw"",""status"":{""online"":true,""clients"":4,""slots"":32}},
    ""game"":{""name"":""Arma Reforger"",""steam_app_id"":1874880,""process"":""ArmaReforgerSteam""}}";

  static void ManifestParsing() {
    var b = Manifest.BuiltIn();
    Check("built-in pins carry SHA-256s", Manifest.Sha(b.TsSha) && Manifest.Sha(b.PluginSha) && Manifest.Sha(b.PluginDllSha));
    Check("built-in pins name no server", b.Host == null && !b.ServerFromManifest && !b.FromManifest);

    var m = Manifest.Parse(Sample, "https://unit.example/comms/manifest.json");
    Check("launcher parsed, url read against the manifest's", m.LauncherVersion == "1.2.0" && m.LauncherUrl == "https://unit.example/releases/BifrostComms-1.2.0.exe" && m.LauncherSha == new string('a', 64) && m.LauncherNotes == "faster");
    Check("teamspeak parsed", m.TsVersion == "3.6.3" && m.TsSize == 5);
    Check("plugin without a DLL hash", m.PluginVersion == "3.0.2" && m.PluginDllSha == null);
    Check("server parsed", m.ServerFromManifest && m.Host == "10.1.2.3" && m.Port == 9988 && m.ServerName == "Example Unit" && m.Bookmark == "EXU" && m.Password == "pw" && m.StatusKnown && m.ServerOnline && m.Clients == 4 && m.Slots == 32);
    Check("came from the manifest", m.FromManifest);

    var bad = Manifest.Parse(@"{""launcher"":{""version"":""1.0"",""sha256"":""" + new string('a', 64) + @""",""size"":1},""teamspeak"":{""version"":""3.6.3"",""url"":""http://plain.example/x.exe"",""sha256"":""" + new string('b', 64) + @"""},
      ""plugin"":{""version"":""9.9.9"",""url"":""https://x/y"",""sha256"":""" + new string('c', 64) + @""",""dll"":""..\\evil.dll""},""server"":{""host"":""evil.example/x?y"",""port"":70000},""game"":{""process"":""a\\b""}}", "https://unit.example/m.json");
    Check("launcher entry without a url ignored", bad.LauncherVersion == null);
    Check("plain-http teamspeak url ignored (built-in kept)", bad.TsVersion == b.TsVersion && bad.TsUrl == b.TsUrl);
    Check("plugin with an unsafe dll name ignored", bad.PluginVersion == b.PluginVersion && bad.PluginDll == b.PluginDll);
    Check("server with a bad host ignored", bad.Host == null && !bad.ServerFromManifest && bad.Port == b.Port);
    Check("unsafe process name ignored", bad.GameProcess == b.GameProcess);

    var empty = Manifest.Parse("{}", "https://x.example/m.json");
    Check("empty manifest = built-in values", empty.PluginSha == b.PluginSha && empty.TsSha == b.TsSha && empty.LauncherVersion == null && !empty.StatusKnown && empty.Host == null);

    Check("resolve: relative file", Manifest.Resolve("https://unit.example/comms/manifest.json", "BifrostComms.exe") == "https://unit.example/comms/BifrostComms.exe");
    Check("resolve: https elsewhere", Manifest.Resolve("https://unit.example/m.json", "https://github.com/o/r/releases/download/v1/x.exe") == "https://github.com/o/r/releases/download/v1/x.exe");
    Check("resolve: plain http elsewhere refused", Manifest.Resolve("https://unit.example/m.json", "http://other.example/x.exe") == null);
    Check("resolve: a local test manifest's own http", Manifest.Resolve("http://127.0.0.1:5000/m.json", "/x.exe") == "http://127.0.0.1:5000/x.exe");
    Check("resolve: nothing", Manifest.Resolve("https://unit.example/m.json", null) == null);
  }

  // ---------------------------------------------------------------------------------------------- the plugin

  static void SafePaths() {
    Check("plain name kept", TeamSpeak.SafeRelative("CRF_Teamspeak_Plugin_Main.dll") == "CRF_Teamspeak_Plugin_Main.dll");
    Check("sub folder kept", TeamSpeak.SafeRelative("icons/1.png") == Path.Combine("icons", "1.png"));
    foreach (var bad in new[] { "../x.dll", "/x.dll", "C:x.dll", "a//b", "a/./b", "", " x.dll" }) Check("refused: '" + bad + "'", TeamSpeak.SafeRelative(bad) == null);
  }

  static string MakePackage(string name, Dictionary<string, byte[]> entries) {
    string f = Path.Combine(root, name);
    using (var zip = ZipFile.Open(f, ZipArchiveMode.Create))
      foreach (var kv in entries) {
        var e = zip.CreateEntry(kv.Key);
        using (var s = e.Open()) s.Write(kv.Value, 0, kv.Value.Length);
      }
    return f;
  }

  static void PluginInstall() {
    var env = Sandbox("plugin", null);
    var ts = new TsInstall { Dir = Path.Combine(env.ProgramFiles, "TeamSpeak 3 Client"), AppData = env.AppData };
    Check("plugins dir in AppData by default", ts.PluginsDir == Path.Combine(env.AppData, "TS3Client", "plugins"));
    var portable = new TsInstall { Dir = ts.Dir, AppData = env.AppData, ConfigInInstallDir = true };
    Check("plugins dir in the install folder for ConfigLocation 1", portable.PluginsDir == Path.Combine(ts.Dir, "config", "plugins"));

    var dll = Encoding.ASCII.GetBytes("MZ fake coalition plugin v2");
    string sha = Net.Hash(dll);
    string pkg = MakePackage("good.ts3_plugin", new Dictionary<string, byte[]> {
      { "package.ini", Encoding.ASCII.GetBytes("Name = Coalition TeamSpeak Plugin\r\nType = Plugin\r\n") },
      { "plugins/CRF_Teamspeak_Plugin_Main.dll", dll }, { "plugins/coalition/icon.png", new byte[] { 1, 2, 3 } } });
    Directory.CreateDirectory(ts.PluginsDir);
    File.WriteAllText(Path.Combine(ts.PluginsDir, "CRF_Teamspeak_Plugin_Main.dll"), "old version");
    File.WriteAllText(Path.Combine(ts.PluginsDir, "acre2_win64.dll"), "someone else's plugin");
    TeamSpeak.InstallPlugin(pkg, ts, "CRF_Teamspeak_Plugin_Main.dll", sha);
    Check("plugin DLL replaced", TeamSpeak.InstalledPluginHash(ts, "CRF_Teamspeak_Plugin_Main.dll") == sha);
    Check("plugin sub folder copied", File.Exists(Path.Combine(ts.PluginsDir, "coalition", "icon.png")));
    Check("package.ini not copied", !File.Exists(Path.Combine(ts.PluginsDir, "package.ini")));
    Check("other plugins untouched", File.ReadAllText(Path.Combine(ts.PluginsDir, "acre2_win64.dll")) == "someone else's plugin");
    Check("no staging files left", Directory.GetFiles(ts.PluginsDir, "*.bifrost-new", SearchOption.AllDirectories).Length == 0);

    File.WriteAllText(Path.Combine(ts.PluginsDir, "CRF_Teamspeak_Plugin_Main.dll"), "old version");
    Throws<IOException>("a DLL that doesn't match the pinned hash is refused", delegate { TeamSpeak.InstallPlugin(pkg, ts, "CRF_Teamspeak_Plugin_Main.dll", new string('0', 64)); });
    Check("refused install leaves the old DLL", File.ReadAllText(Path.Combine(ts.PluginsDir, "CRF_Teamspeak_Plugin_Main.dll")) == "old version");
    Check("refused install leaves no staging files", Directory.GetFiles(ts.PluginsDir, "*.bifrost-new", SearchOption.AllDirectories).Length == 0);

    string evil = MakePackage("evil.ts3_plugin", new Dictionary<string, byte[]> { { "plugins/../../evil.dll", dll }, { "plugins/CRF_Teamspeak_Plugin_Main.dll", dll } });
    Throws<IOException>("a package path leaving the plugins folder is refused", delegate { TeamSpeak.InstallPlugin(evil, ts, "CRF_Teamspeak_Plugin_Main.dll", sha); });
    Check("nothing written outside the plugins folder", !File.Exists(Path.Combine(env.AppData, "evil.dll")) && !File.Exists(Path.Combine(env.AppData, "TS3Client", "evil.dll")));
    string none = MakePackage("none.ts3_plugin", new Dictionary<string, byte[]> { { "plugins/other.dll", dll } });
    Throws<IOException>("a package without the plugin DLL is refused", delegate { TeamSpeak.InstallPlugin(none, ts, "CRF_Teamspeak_Plugin_Main.dll", null); });
    TeamSpeak.InstallPlugin(pkg, ts, "CRF_Teamspeak_Plugin_Main.dll", null);
    Check("a release without a DLL hash installs", TeamSpeak.InstalledPluginHash(ts, "CRF_Teamspeak_Plugin_Main.dll") == sha);

    // all or nothing: the second file can't go in (a folder sits at its place), so the first goes back to the old copy
    string two = MakePackage("two.ts3_plugin", new Dictionary<string, byte[]> {
      { "plugins/CRF_Teamspeak_Plugin_Main.dll", Encoding.ASCII.GetBytes("MZ v3") }, { "plugins/zz_helper.dll", new byte[] { 9 } } });
    File.WriteAllText(Path.Combine(ts.PluginsDir, "CRF_Teamspeak_Plugin_Main.dll"), "old version");
    Directory.CreateDirectory(Path.Combine(ts.PluginsDir, "zz_helper.dll", "in-the-way"));
    Throws<IOException>("a swap that fails partway is refused", delegate { TeamSpeak.InstallPlugin(two, ts, "CRF_Teamspeak_Plugin_Main.dll", null); });
    Check("a failed swap puts the old copy back", File.ReadAllText(Path.Combine(ts.PluginsDir, "CRF_Teamspeak_Plugin_Main.dll")) == "old version");
    Check("a failed swap leaves nothing aside", Directory.GetFiles(ts.PluginsDir, "*.bifrost-*", SearchOption.AllDirectories).Length == 0);
    Directory.Delete(Path.Combine(ts.PluginsDir, "zz_helper.dll"), true);
    File.WriteAllText(Path.Combine(ts.PluginsDir, "leftover.dll.bifrost-old"), "x");
    TeamSpeak.InstallPlugin(two, ts, "CRF_Teamspeak_Plugin_Main.dll", null);
    Check("the next install clears copies left aside", !File.Exists(Path.Combine(ts.PluginsDir, "leftover.dll.bifrost-old")) && File.Exists(Path.Combine(ts.PluginsDir, "zz_helper.dll")));

    // a release without a pinned DLL hash is "current" only while it's the DLL this program installed for it
    var c = new Comms(env, env.HomeExe, "1.0.0") { Ts = ts };
    c.M.PluginDllSha = null; c.M.PluginVersion = "3.0.2";
    string now = TeamSpeak.InstalledPluginHash(ts, "CRF_Teamspeak_Plugin_Main.dll");
    Check("unpinned release: not current before it's recorded", !c.PluginCurrent());
    c.Settings.PluginVersion = "3.0.2"; c.Settings.PluginDllSha = now;
    Check("unpinned release: current once recorded", c.PluginCurrent());
    File.WriteAllText(Path.Combine(ts.PluginsDir, "CRF_Teamspeak_Plugin_Main.dll"), "someone swapped it");
    Check("unpinned release: a changed DLL isn't current", !c.PluginCurrent());
    c.M.PluginVersion = "3.0.3";
    Check("unpinned release: a newer version isn't current", !c.PluginCurrent());
  }

  static void FindTeamSpeak() {
    var reg = new Dictionary<string, string>();
    var env = Sandbox("find", reg);
    Check("nothing installed: not found", TeamSpeak.Find(env) == null);

    string dir = Path.Combine(env.ProgramFiles, "Custom TS");
    Directory.CreateDirectory(dir);
    File.WriteAllBytes(Path.Combine(dir, "ts3client_win64.exe"), new byte[] { 0x4d, 0x5a });
    reg[@"HKLM|Software\TeamSpeak 3 Client|"] = dir;
    reg[@"HKLM|Software\TeamSpeak 3 Client|InstallMode"] = "AllUsers";
    reg[@"HKLM|Software\TeamSpeak 3 Client|ConfigLocation"] = "0";
    var t = TeamSpeak.Find(env);
    Check("found through HKLM", t != null && t.Dir == dir && t.Mode == "AllUsers" && !t.ConfigInInstallDir);

    reg[@"HKCU|Software\TeamSpeak 3 Client|"] = Path.Combine(env.LocalAppData, "gone");
    t = TeamSpeak.Find(env);
    Check("a stale HKCU entry falls through to HKLM", t != null && t.Dir == dir);

    reg.Clear();
    string user = Path.Combine(env.LocalAppData, "Programs", "TeamSpeak 3 Client");
    Directory.CreateDirectory(user);
    File.WriteAllBytes(Path.Combine(user, "ts3client_win64.exe"), new byte[] { 0x4d, 0x5a });
    t = TeamSpeak.Find(env);
    Check("found in the per-user folder without registry", t != null && t.Dir == user);
  }

  static void Uris() {
    Check("connect link", TeamSpeak.ConnectUri("ts.example.com", 9987, "PFC J. Doe", null, "Example Unit") ==
      "ts3server://ts.example.com?port=9987&nickname=PFC%20J.%20Doe&addbookmark=Example%20Unit");
    Check("connect link with a password, without a bookmark", TeamSpeak.ConnectUri("203.0.113.10", 9988, "A&B=C", "p w&x", null) ==
      "ts3server://203.0.113.10?port=9988&nickname=A%26B%3DC&password=p%20w%26x");
  }

  // ---------------------------------------------------------------------------------------------- ClientQuery

  static void QueryText() {
    var rows = QueryLines.Rows(@"ip=203.0.113.10 port=9987 password=");
    Check("row parsed", rows.Count == 1 && rows[0]["ip"] == "203.0.113.10" && rows[0]["port"] == "9987" && rows[0]["password"] == "");
    Check("escapes undone", QueryLines.Unescape(@"EXU\/Reforger\sIn-Game\s(VON)\p\\") == @"EXU/Reforger In-Game (VON)|\");
    Check("several rows", QueryLines.Rows("schandlerid=1|schandlerid=2").Count == 2);
    Check("channel name is the last part", TeamSpeak.ChannelName("EXU/Reforger In-Game (VON)") == "Reforger In-Game (VON)");
    var env = Sandbox("ini", null);
    string ini = Path.Combine(env.AppData, "clientquery.ini");
    File.WriteAllText(ini, "[General]\r\nopen_remote=false\r\napi_key=ABCD-EFGH-IJKL\r\n");
    Check("api key read", TeamSpeak.ApiKey(ini) == "ABCD-EFGH-IJKL");
    Check("no ini, no key", TeamSpeak.ApiKey(Path.Combine(env.AppData, "nope.ini")) == null);
  }

  static void ClientQueryFake() {
    var env = Sandbox("cq", null);
    var ts = new TsInstall { Dir = env.ProgramFiles, AppData = env.AppData };
    Directory.CreateDirectory(ts.ConfigDir);
    File.WriteAllText(Path.Combine(ts.ConfigDir, "clientquery.ini"), "[General]\napi_key=KEY-1\n");
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var seen = new List<string>();
    var server = new Thread(delegate() {
      try {
        using (var c = listener.AcceptTcpClient()) using (var s = c.GetStream()) {
          Action<string> send = delegate(string line) { var b = Encoding.UTF8.GetBytes(line + "\n\r"); s.Write(b, 0, b.Length); };
          send("TS3 Client"); send("Welcome to the TeamSpeak 3 ClientQuery interface."); send("selected schandlerid=1");
          var q = new QueryLines(s);
          int handler = 1;
          while (true) {
            string cmd = q.ReadLine();
            lock (seen) seen.Add(cmd);
            if (cmd == "quit") break;
            if (cmd.StartsWith("auth ")) send(cmd == "auth apikey=KEY-1" ? "error id=0 msg=ok" : "error id=256 msg=invalid");
            else if (cmd == "serverconnectionhandlerlist") { send("schandlerid=1|schandlerid=2"); send("error id=0 msg=ok"); }
            else if (cmd == "currentschandlerid") { send("schandlerid=2"); send("error id=0 msg=ok"); }
            else if (cmd.StartsWith("use ")) { handler = int.Parse(cmd.Substring(16)); send("error id=0 msg=ok"); }
            else if (cmd == "serverconnectinfo") {
              if (handler == 1) send("error id=1794 msg=not\\sconnected");
              else { send("ip=203.0.113.10 port=9987 password="); send("error id=0 msg=ok"); }
            } else if (cmd == "channelconnectinfo") { send("path=EXU\\/Reforger\\sIn-Game\\s(VON) password="); send("error id=0 msg=ok"); }
            else send("error id=256 msg=command\\snot\\sfound");
          }
        }
      } catch (Exception) { }
    }) { IsBackground = true };
    server.Start();
    var conns = TeamSpeak.Connections(ts, port);
    listener.Stop();
    Check("ClientQuery: one connected tab of two", conns != null && conns.Count == 1 && conns[0].Handler == 2 && conns[0].Ip == "203.0.113.10" && conns[0].Port == 9987);
    Check("ClientQuery: current channel", conns != null && conns.Count == 1 && conns[0].ChannelPath == "EXU/Reforger In-Game (VON)");
    Check("ClientQuery: the selected tab is marked", conns != null && conns.Count == 1 && conns[0].Active);
    lock (seen) Check("ClientQuery: authenticated with the ini's key", seen.Count > 0 && seen[0] == "auth apikey=KEY-1");
    Check("ClientQuery: nothing listening = null", TeamSpeak.Connections(ts, port) == null);
  }

  // ---------------------------------------------------------------------------------------------- the radio link

  static void BridgeFiles() {
    var env = Sandbox("bridge", null);
    Directory.CreateDirectory(env.ProfileDir);
    var bridge = new Bridge(env.ProfileDir);
    var now = DateTime.UtcNow;
    long unix = (long)(now - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
    Check("no files: nothing", bridge.Plugin() == null && bridge.Server() == null && bridge.HeartbeatAge(now) < 0);
    File.WriteAllText(Path.Combine(env.ProfileDir, "VONPluginInfo.json"), "{\"Version\":\"3.0.1\",\"Features\":\"bin1|talking1|heartbeat1|comms1\",\"Time\":" + unix + "}");
    var p = bridge.Plugin();
    Check("plugin info read", p != null && p.Version == "3.0.1" && p.Features.Contains("heartbeat1"));
    Check("fresh plugin info", Bridge.Fresh(p, now));
    Check("20 s old plugin info is stale", !Bridge.Fresh(new PluginInfo { Time = unix - 20 }, now));
    File.WriteAllText(Path.Combine(env.ProfileDir, "VONServerData.json"),
      "{\n\t\"ServerData\":\t{\n\t\t\"TSServerIp\":\t\"203.0.113.10\",\n\t\t\"TSServerPassword\":\t\"\",\n\t\t\"InGameName\":\t\"J. Doe\",\n\t\t\"InGame\":\ttrue,\n\t\t\"TSClientID\":\t7,\n\t\t\"TSPluginVersion\":\t\"3.0.1\",\n\t\t\"VONChannelName\":\t\"Reforger In-Game (VON)\",\n\t\t\"VONChannelPassword\":\t\"x\"\n\t}\n}");
    var sd = bridge.Server();
    Check("server data read", sd != null && sd.InGame && sd.TsServerIp == "203.0.113.10" && sd.ChannelName == "Reforger In-Game (VON)" && sd.ClientId == 7);
    File.WriteAllText(Path.Combine(env.ProfileDir, "VONHeartbeat.json"), "{}");
    Check("heartbeat age", bridge.HeartbeatAge(DateTime.UtcNow) < 5);
    File.WriteAllText(Path.Combine(env.ProfileDir, "VONServerData.json"), "{\"ServerData\": {\"InGa");
    Check("half-written file = no answer", bridge.Server() == null);
  }

  static void LinkStates() {
    var sd = new ServerData { InGame = true, ChannelName = "Reforger In-Game (VON)", ClientId = 7 };
    Check("link: game off", Bridge.Derive(false, true, true, sd, 0, null).Link == Link.Off);
    Check("link: no TeamSpeak", Bridge.Derive(true, false, false, sd, 0, null).Link == Link.NoTeamSpeak);
    Check("link: no plugin", Bridge.Derive(true, true, false, sd, 0, null).Link == Link.NoPlugin);
    var menus = Bridge.Derive(true, true, true, new ServerData { InGame = false }, -1, null);
    Check("link: standby in menus", menus.Link == Link.Standby && menus.Detail.Contains("join a Coalition radio server"));
    var waiting = Bridge.Derive(true, true, true, new ServerData { InGame = false }, 0.5, null);
    Check("link: on a radio server, not spawned: says spawn in", waiting.Link == Link.Standby && waiting.Detail.Contains("spawn"));
    var off = Bridge.Derive(true, true, true, sd, 1, null, true);
    Check("link: TeamSpeak off the unit's server", off.Link == Link.OffServer && off.Tone == Tone.Warn && off.Detail.Contains("your unit's server"));
    var noId = Bridge.Derive(true, true, true, new ServerData { InGame = true, ChannelName = "Reforger In-Game (VON)" }, 1, "EXU/Reforger In-Game (VON)");
    Check("link: no TeamSpeak id from the plugin is not linked", noId.Link == Link.Joining && noId.Detail.Contains("plugin"));
    Check("link: heartbeat stopped", Bridge.Derive(true, true, true, sd, 12, "x").Link == Link.NoHeartbeat);
    Check("link: joining", Bridge.Derive(true, true, true, sd, 1, "EXU/Lobby").Link == Link.Joining);
    Check("link: linked in the VON channel", Bridge.Derive(true, true, true, sd, 1, "EXU/Reforger In-Game (VON)").Link == Link.Linked);
    Check("link: linked when the channel can't be seen", Bridge.Derive(true, true, true, sd, 1, null).Link == Link.Linked);
    var split = Bridge.Derive(true, false, false, null, -1, null, false, "G:\\Arma\\profile");
    Check("link: the game's radio files in another folder is down, whatever else", split.Link == Link.Split && split.Tone == Tone.Bad && split.Detail.Contains("G:\\Arma\\profile"));
    Check("link: game off wins over the other folder", Bridge.Derive(false, true, true, sd, 0, null, false, "G:\\Arma\\profile").Link == Link.Off);
  }

  // ---------------------------------------------------------------------------------------------- settings, comms.json

  static void SettingsFile() {
    var env = Sandbox("settings", null);
    var s = Settings.Load(Path.Combine(env.Home, "BifrostComms.settings.json"));
    Check("no settings yet", s.Callsign == null && s.Bookmarked == null);
    s.Callsign = "SGT Ünïcødé Smith"; s.Bookmarked = "ts.example.com:9987"; s.Save();
    var t = Settings.Load(s.File);
    Check("settings round trip (UTF-8)", t.Callsign == "SGT Ünïcødé Smith" && t.Bookmarked == "ts.example.com:9987");
    Check("callsign rules", Settings.CallsignProblem("ab") != null && Settings.CallsignProblem(new string('x', 31)) != null && Settings.CallsignProblem("PFC J. Doe") == null && Settings.CallsignProblem("a\tb c") != null);
    File.WriteAllText(s.File, "not json");
    Check("broken settings file = defaults", Settings.Load(s.File).Callsign == null);
  }

  static void UnitFile() {
    var env = Sandbox("unit", null);
    Directory.CreateDirectory(env.Home);
    string f = env.HomeUnit;
    File.WriteAllText(f, "{\"name\":\"Example Unit\",\"host\":\"ts.example.com\",\"port\":9988,\"password\":\"\",\"bookmark\":\" \",\"manifest\":\"\"}");
    var u = UnitConfig.Load(f);
    Check("comms.json read, blanks are unset", u.Name == "Example Unit" && u.Host == "ts.example.com" && u.Port == 9988 && u.Password == null && u.Bookmark == null && u.Manifest == null);
    File.WriteAllText(f, "{\"host\":\"http://ts.example.com/x\",\"port\":70000}");
    u = UnitConfig.Load(f);
    Check("a bad host and port in comms.json are ignored", u.Host == null && u.Port == 9987);
    File.WriteAllText(f, "not json");
    Check("a broken comms.json = no server", UnitConfig.Load(f).Host == null);
    new UnitConfig { File = f, Name = "Ünït", Host = "203.0.113.10", Port = 9990, Password = "pw" }.Save();
    var r = UnitConfig.Load(f);
    Check("comms.json round trip (UTF-8)", r.Name == "Ünït" && r.Host == "203.0.113.10" && r.Port == 9990 && r.Password == "pw");

    string h; int p;
    Check("address: a name", UnitConfig.Split("ts.example.com", out h, out p) == null && h == "ts.example.com" && p == 9987);
    Check("address: ip:port, spaces trimmed", UnitConfig.Split(" 203.0.113.10:9988 ", out h, out p) == null && h == "203.0.113.10" && p == 9988);
    Check("address: a pasted ts3server link", UnitConfig.Split("ts3server://ts.example.com?port=9990&nickname=x", out h, out p) == null && h == "ts.example.com" && p == 9990);
    Check("address: a ts3server link without a port", UnitConfig.Split("ts3server://ts.example.com/", out h, out p) == null && h == "ts.example.com" && p == 9987);
    foreach (var bad in new[] { "", "http://ts.example.com", "ts.example.com/path", "ts example", "ts.example.com:0", "ts.example.com:99999", "::1", "a..b", "-x.example", "ts.example.com:" })
      Check("address refused: '" + bad + "'", UnitConfig.AddressProblem(bad) != null);

    var m = new UnitConfig();
    m.Manifest = "https://unit.example/comms/manifest.json"; Check("manifest: https allowed", m.ManifestUrl == "https://unit.example/comms/manifest.json");
    m.Manifest = "http://unit.example/m.json"; Check("manifest: plain http refused", m.ManifestUrl == null);
    m.Manifest = "http://127.0.0.1:8765/m.json"; Check("manifest: a test server on this PC allowed", m.ManifestUrl == "http://127.0.0.1:8765/m.json");
    m.Manifest = "http://127.0.0.1.evil.example/m.json"; Check("manifest: a host that only starts with 127.0.0.1 refused", m.ManifestUrl == null);
    m.Manifest = "https://user@unit.example/m.json"; Check("manifest: user-info refused", m.ManifestUrl == null);
    m.Manifest = null; Check("manifest: none", m.ManifestUrl == null);

    // comms.json beside a downloaded copy beats the installed one
    string share = Path.Combine(env.Desktop, "share");
    Directory.CreateDirectory(share);
    File.WriteAllText(Path.Combine(share, "comms.json"), "{\"name\":\"New Unit\",\"host\":\"ts.new.example\"}");
    var c = new Comms(env, Path.Combine(share, "BifrostComms.exe"), "1.0.0");
    Check("comms.json beside the download wins", c.Unit.Host == "ts.new.example" && c.M.Host == "ts.new.example" && c.M.ServerName == "New Unit" && c.M.Bookmark == "New Unit" && c.St.Server == "ts.new.example:9987");
    var c2 = new Comms(env, Path.Combine(env.Desktop, "BifrostComms.exe"), "1.0.0");
    Check("no comms.json beside the download: the installed one", c2.Unit.Host == "203.0.113.10" && c2.M.Port == 9990 && c2.M.Password == "pw");
  }

  static void ServerPrompt() {
    var env = Sandbox("prompt", null);
    var c = new Comms(env, env.HomeExe, "1.0.0");
    Check("no comms.json: no server", c.M.Host == null && c.St.Server == "NOT SET");
    var fake = new FakeConsole();
    c.Out = fake;
    fake.Answers.Enqueue("ts.example.com:9988");
    Check("asked for the address, saved to comms.json", c.EnsureServer(false) && c.M.Host == "ts.example.com" && c.M.Port == 9988 && UnitConfig.Load(env.HomeUnit).Host == "ts.example.com");
    Check("status shows the server", c.St.Server == "ts.example.com:9988" && c.M.ServerName == "ts.example.com");
    Check("not asked again", c.EnsureServer(false) && fake.Prompts.Count == 1);
    fake.Answers.Enqueue(null);
    Check("CHANGE cancelled keeps the server", c.EnsureServer(true) && c.M.Host == "ts.example.com" && fake.Prompts.Count == 2);
    fake.Answers.Enqueue("not an address!");
    Check("a bad answer changes nothing", c.EnsureServer(true) && c.M.Host == "ts.example.com");
    fake.Answers.Enqueue("203.0.113.10");
    Check("CHANGE to another server", c.EnsureServer(true) && c.M.Host == "203.0.113.10" && c.M.Port == 9987 && UnitConfig.Load(env.HomeUnit).Port == 9987);
    var c2 = new Comms(env, env.HomeExe, "1.0.0");
    Check("the next start remembers it", c2.M.Host == "203.0.113.10");
    c2.Out = fake;
    c2.M.ServerFromManifest = true;
    int asked = fake.Prompts.Count;
    Check("a manifest's server isn't asked or changed", c2.EnsureServer(true) && fake.Prompts.Count == asked);

    var none = new Comms(Sandbox("prompt-none", null), Path.Combine(root, "x", "BifrostComms.exe"), "1.0.0") { Out = new FakeConsole() };
    Check("no address given: EnsureServer says so", !none.EnsureServer(false) && none.M.Host == null);
  }

  static void ServerMatch() {
    var c = new Comms(Sandbox("match", null), Path.Combine(root, "y", "BifrostComms.exe"), "1.0.0");
    var conns = new List<TsConnection> { new TsConnection { Handler = 1, Ip = "198.51.100.7", Port = 9987 }, new TsConnection { Handler = 2, Ip = "203.0.113.10", Port = 9987 } };
    Check("no server set: never connected", c.ConnectedToUnit(conns) == null);
    c.M.Host = "203.0.113.10"; c.M.Port = 9987;
    var hit = c.ConnectedToUnit(conns);
    Check("connected: matched by IP", hit != null && hit.Handler == 2);
    c.M.Port = 9988;
    Check("connected: another port isn't the unit's", c.ConnectedToUnit(conns) == null);
    c.M.Host = "localhost"; c.M.Port = 9987;
    Check("connected: a name is looked up", c.ConnectedToUnit(new List<TsConnection> { new TsConnection { Ip = "127.0.0.1", Port = 9987 } }) != null);
    Check("connected: ClientQuery off = unknown", c.ConnectedToUnit(null) == null);
  }

  static void Builds() {
    var env = Sandbox("builds", null);
    Directory.CreateDirectory(env.Home);
    string me = System.Reflection.Assembly.GetExecutingAssembly().Location;
    string mine = Comms.ExeVersion(me);
    Check("this build's version is readable", mine != null && Ver.Valid(mine));
    bool ok = true;
    try { Comms.CheckBuild(me, mine); } catch (Exception) { ok = false; }
    Check("a build matching the manifest's version passes", ok);
    Throws<IOException>("a build that isn't the advertised version is refused", delegate { Comms.CheckBuild(me, "99.0.0"); });
    string fake = Path.Combine(root, "fake.exe");
    File.WriteAllText(fake, "MZ not really");
    Throws<IOException>("a file that isn't a program is refused", delegate { Comms.CheckBuild(fake, "1.0.0"); });
    Check("running from home", new Comms(env, env.HomeExe, "1.0.0").RunningFromHome);
    Check("not running from home", !new Comms(env, Path.Combine(env.Desktop, "BifrostComms.exe"), "1.0.0").RunningFromHome);
  }

  // ---------------------------------------------------------------------------------------------- downloads

  // A one-request-at-a-time HTTP server: path -> (status, body, location).
  static int Serve(Dictionary<string, Tuple<int, byte[], string>> routes, List<string> hits, out TcpListener listener) {
    var l = new TcpListener(IPAddress.Loopback, 0);
    l.Start();
    listener = l;
    var t = new Thread(delegate() {
      while (true) {
        TcpClient c;
        try { c = l.AcceptTcpClient(); } catch (Exception) { return; }
        try {
          using (c) using (var s = c.GetStream()) {
            var r = new StreamReader(s, Encoding.ASCII);
            string first = r.ReadLine() ?? "";
            while (!string.IsNullOrEmpty(r.ReadLine())) { }   // the request headers
            string path = first.Split(' ').Length > 1 ? first.Split(' ')[1] : "/";
            lock (hits) hits.Add(path);
            Tuple<int, byte[], string> route;
            if (!routes.TryGetValue(path, out route)) route = Tuple.Create(404, Encoding.ASCII.GetBytes("nope"), (string)null);
            var head = "HTTP/1.1 " + route.Item1 + " X\r\nContent-Length: " + route.Item2.Length + "\r\nConnection: close\r\n" +
                       (route.Item3 != null ? "Location: " + route.Item3 + "\r\n" : "") + "\r\n";
            var hb = Encoding.ASCII.GetBytes(head);
            s.Write(hb, 0, hb.Length); s.Write(route.Item2, 0, route.Item2.Length);
          }
        } catch (Exception) { }
      }
    }) { IsBackground = true };
    t.Start();
    return ((IPEndPoint)l.LocalEndpoint).Port;
  }

  static void Downloads() {
    var body = new byte[300000];
    new Random(4).NextBytes(body);
    string sha = Net.Hash(body);
    var routes = new Dictionary<string, Tuple<int, byte[], string>> {
      { "/file", Tuple.Create(200, body, (string)null) },
      { "/moved", Tuple.Create(302, new byte[0], "/file") },
      { "/manifest", Tuple.Create(200, Encoding.UTF8.GetBytes(Sample), (string)null) },
      { "/manifest-moved", Tuple.Create(301, new byte[0], "/manifest") },
    };
    var hits = new List<string>();
    TcpListener l;
    int port = Serve(routes, hits, out l);
    string site = "http://127.0.0.1:" + port;
    var dir = Path.Combine(root, "dl");
    string dest = Path.Combine(dir, "file.bin");
    long lastGot = -1;
    bool fresh = Net.Download(site + "/file", dest, sha, body.Length, false, delegate(long got, long total, double rate) { lastGot = got; }, null);
    Check("download: fetched and verified", fresh && File.Exists(dest) && Net.Hash(dest) == sha && lastGot == body.Length);
    int before; lock (hits) before = hits.Count;
    bool again = Net.Download(site + "/file", dest, sha, body.Length, false, null, null);
    int after; lock (hits) after = hits.Count;
    Check("download: a verified copy isn't fetched again", !again && after == before);
    string bad = Path.Combine(dir, "bad.bin");
    Throws<DownloadError>("download: wrong SHA-256 refused", delegate { Net.Download(site + "/file", bad, new string('0', 64), body.Length, false, null, null); });
    Check("download: refused file removed", !File.Exists(bad) && !File.Exists(bad + ".part"));
    Throws<DownloadError>("download: wrong size refused", delegate { Net.Download(site + "/file", bad, sha, body.Length + 1, false, null, null); });
    Throws<DownloadError>("download: no hash, no download", delegate { Net.Download(site + "/file", bad, null, 0, false, null, null); });
    Throws<DownloadError>("download: redirect refused where not allowed", delegate { Net.Download(site + "/moved", bad, sha, body.Length, false, null, null); });
    Check("download: followed redirect where allowed", Net.Download(site + "/moved", Path.Combine(dir, "moved.bin"), sha, body.Length, true, null, null));
    Throws<OperationCanceledException>("download: cancel stops it", delegate { Net.Download(site + "/file", Path.Combine(dir, "c.bin"), sha, body.Length, false, null, delegate { return true; }); });
    Check("download: cancelled file removed", !File.Exists(Path.Combine(dir, "c.bin")) && !File.Exists(Path.Combine(dir, "c.bin.part")));

    var m = Manifest.Parse(Net.GetText(site + "/manifest", 3000, false), site + "/manifest");
    Check("manifest over HTTP", m.LauncherVersion == "1.2.0" && m.LauncherUrl == site + "/releases/BifrostComms-1.2.0.exe");
    Throws<DownloadError>("manifest 404 is an error", delegate { Net.GetText(site + "/missing", 3000, false); });

    // the whole path through comms.json
    var env = Sandbox("manifest-fetch", null);
    Directory.CreateDirectory(env.Home);
    File.WriteAllText(env.HomeUnit, "{\"host\":\"ts.example.com\",\"password\":\"mine\",\"manifest\":\"" + site + "/manifest\"}");
    var c = new Comms(env, env.HomeExe, "1.0.0");
    c.FetchManifest();
    Check("comms.json's manifest fetched; its server wins", c.M.FromManifest && c.M.ServerFromManifest && c.M.Host == "10.1.2.3" && c.St.Uplink == "ONLINE" && c.St.Server == "10.1.2.3:9988" && c.St.ServerDetail.StartsWith("ONLINE"));
    File.WriteAllText(env.HomeUnit, "{\"host\":\"ts.example.com\",\"password\":\"mine\",\"manifest\":\"" + site + "/manifest-moved\"}");
    c = new Comms(env, env.HomeExe, "1.0.0");
    c.FetchManifest();
    Check("a manifest behind a redirect isn't used", !c.M.FromManifest && c.St.Uplink == "OFFLINE" && c.M.Host == "ts.example.com" && c.M.Password == "mine");
    File.WriteAllText(env.HomeUnit, "{\"host\":\"ts.example.com\",\"manifest\":\"" + site + "/missing\"}");
    c = new Comms(env, env.HomeExe, "1.0.0");
    c.FetchManifest();
    Check("manifest unreachable: built-in pins, comms.json's server", !c.M.FromManifest && c.St.Uplink == "OFFLINE" && c.M.Host == "ts.example.com");
    File.WriteAllText(env.HomeUnit, "{\"host\":\"ts.example.com\"}");
    c = new Comms(env, env.HomeExe, "1.0.0");
    c.FetchManifest();
    Check("no manifest: built-in pins, nothing fetched", !c.M.FromManifest && c.St.Uplink == "LOCAL" && c.M.Host == "ts.example.com" && c.M.TsSha == Manifest.BuiltIn().TsSha);
    l.Stop();
  }

  static void Overwolf() {
    var env = Sandbox("overwolf", null);
    string dummy = Path.Combine(env.Temp, "overwolfdummy");
    File.WriteAllText(dummy, "left from an earlier install");
    bool armed = OverwolfGuard.Arm(dummy);
    Check("guard: the answer file becomes a folder", armed && Directory.Exists(dummy) && !File.Exists(dummy));
    bool writable = true;
    try { File.WriteAllText(dummy, "ok"); } catch (Exception) { writable = false; }
    Check("guard: the installer can't save its answer there", !writable);
    OverwolfGuard.Disarm(dummy, armed);
    Check("guard: removed afterwards", !Directory.Exists(dummy));
    Directory.CreateDirectory(dummy);
    armed = OverwolfGuard.Arm(dummy);
    OverwolfGuard.Disarm(dummy, armed);
    Check("guard: a folder that was already there stays", !armed && Directory.Exists(dummy));
    Check("guard: no Overwolf in the sandbox", !OverwolfGuard.Installed(env));
    Directory.CreateDirectory(Path.Combine(env.LocalAppData, "Overwolf"));
    Check("guard: Overwolf folder noticed", OverwolfGuard.Installed(env));
  }

  static void Wrapping() {
    Check("short line not wrapped", TerminalForm.Wrap("hello", 10).Count == 1);
    var w = TerminalForm.Wrap("one two three four five six", 10);
    Check("wrapped at words, continuation indented", w.Count >= 3 && w[0] == "one two" && w[1].StartsWith("  "));
    Check("long word cut", TerminalForm.Wrap(new string('x', 25), 10).Count == 3);
    bool fits = true;
    foreach (var l in TerminalForm.Wrap("the quick brown fox jumps over the lazy dog again and again", 12)) fits &= l.Length <= 12;
    Check("no wrapped line longer than the width", fits);
  }

  // ---------------------------------------------------------------------------------------------- radio check

  static void GameOptions() {
    string docs = Path.Combine(root, "docs-x");
    Check("options after a quoted program", Doctor.AfterProgram("\"C:\\Steam\\steamapps\\common\\Arma Reforger\\ArmaReforgerSteam.exe\" -window -profile x") == "-window -profile x");
    Check("options after a plain program", Doctor.AfterProgram("ArmaReforgerSteam.exe -window") == "-window");
    Check("no options", Doctor.AfterProgram("\"C:\\x\\ArmaReforgerSteam.exe\"") == "");
    Check("-profile name: a folder in Documents\\My Games", Doctor.ProfileArg("-window -profile Alt", docs) == Path.Combine(docs, "My Games", "Alt", "profile"));
    Check("-profile path: used as it is, plus profile", Doctor.ProfileArg("-profile \"G:\\Arma Profile\" -window", docs) == Path.Combine("G:\\Arma Profile", "profile"));
    Check("-PROFILE is the same option", Doctor.ProfileArg("-PROFILE G:\\P", docs) == Path.Combine("G:\\P", "profile"));
    Check("no -profile", Doctor.ProfileArg("-window -noSplash", docs) == null && Doctor.ProfileArg(null, docs) == null && Doctor.ProfileArg("-profile", docs) == null);
    Check("-profile followed by another option is ignored", Doctor.ProfileArg("-profile -window", docs) == null);
    Check("-profile with forward slashes", Doctor.ProfileArg("-profile G:/Arma/Moved", docs) == Path.Combine("G:\\Arma\\Moved", "profile"));
    Check("-profile on a network share", Doctor.ProfileArg("-profile \\\\nas\\arma", docs) == Path.Combine("\\\\nas\\arma", "profile"));
    Check("-profile relative: from the game's folder", Doctor.ProfileArg("-profile ..\\Profiles", docs, "D:\\Steam\\steamapps\\common\\Arma Reforger") == Path.Combine("D:\\Steam\\steamapps\\common\\Profiles", "profile"));
    Check("-profile relative, game folder unknown: can't tell", Doctor.ProfileArg("-profile ..\\Profiles", docs) == null && Doctor.ProfileArg("-profile Arma\\P", docs) == null);
    Check("-profile with %command% (Steam's form)", Doctor.ProfileArg("%command% -profile \"G:\\Arma Reforger\"", docs) == Path.Combine("G:\\Arma Reforger", "profile"));
    var a = Doctor.SplitArgs("-a \"b c\" d  \"\"");
    Check("arguments split with quotes", a.Count == 4 && a[1] == "b c" && a[2] == "d" && a[3] == "");
  }

  static void SteamOptions() {
    const string vdf = "\"UserLocalConfigStore\"\n{\n\t\"Software\"\n\t{\n\t\t\"Valve\"\n\t\t{\n\t\t\t\"Steam\"\n\t\t\t{\n\t\t\t\t\"apps\"\n\t\t\t\t{\n" +
      "\t\t\t\t\t\"1874880\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"LastPlayed\"\t\t\"1792800000\"\n\t\t\t\t\t\t\"cloud\"\n\t\t\t\t\t\t{\n\t\t\t\t\t\t\t\"LaunchOptions\"\t\t\"not this one\"\n\t\t\t\t\t\t}\n" +
      "\t\t\t\t\t\t\"LaunchOptions\"\t\t\"-profile \\\"G:\\\\Arma {Profile}\\\" -window\"\n\t\t\t\t\t}\n" +
      "\t\t\t\t\t\"107410\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"LaunchOptions\"\t\t\"-arma3\"\n\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}\n\t\t}\n\t}\n\t// a comment \"1874880\" {\n}\n";
    var o = SteamConfig.LaunchOptions(vdf, 1874880);
    Check("steam: Reforger's launch options read, escapes undone", o.Count == 1 && o[0] == "-profile \"G:\\Arma {Profile}\" -window");
    Check("steam: another game's options aren't Reforger's", SteamConfig.LaunchOptions(vdf, 107410).Count == 1 && SteamConfig.LaunchOptions(vdf, 107410)[0] == "-arma3");
    Check("steam: the -profile in them is found", Doctor.ProfileArg(o[0], "D:\\Docs") == Path.Combine("G:\\Arma {Profile}", "profile"));
    Check("steam: nothing for a game without options", SteamConfig.LaunchOptions(vdf, 42).Count == 0 && SteamConfig.LaunchOptions("", 1874880).Count == 0);

    var reg = new Dictionary<string, string>();
    var env = Sandbox("steam", reg);
    string steam = Path.Combine(root, "steam", "Steam");
    Directory.CreateDirectory(Path.Combine(steam, "userdata", "123", "config"));
    Directory.CreateDirectory(Path.Combine(steam, "userdata", "456"));
    File.WriteAllText(Path.Combine(steam, "userdata", "123", "config", "localconfig.vdf"), vdf);
    reg[@"HKCU|Software\Valve\Steam|SteamPath"] = steam.Replace('\\', '/');
    var pc = SteamConfig.LaunchOptionsOnPc(env, 1874880);
    Check("steam: found through the registry, accounts without a config skipped", pc.Count == 1 && pc[0].StartsWith("-profile"));
    string moved = Path.Combine("G:\\Arma {Profile}", "profile");
    Check("steam: Steam not running, one account: its -profile", SteamConfig.LaunchProfile(env, 1874880) == moved);
    reg[@"HKCU|Software\Valve\Steam\ActiveProcess|ActiveUser"] = "456";
    Check("steam: the signed-in account has no options: the default folder", SteamConfig.LaunchProfile(env, 1874880) == null);
    reg[@"HKCU|Software\Valve\Steam\ActiveProcess|ActiveUser"] = "123";
    Check("steam: the signed-in account's -profile", SteamConfig.LaunchProfile(env, 1874880) == moved);
    reg[@"HKCU|Software\Valve\Steam\ActiveProcess|ActiveUser"] = "0";
    Directory.CreateDirectory(Path.Combine(steam, "userdata", "456", "config"));
    File.WriteAllText(Path.Combine(steam, "userdata", "456", "config", "localconfig.vdf"), vdf.Replace("Arma {Profile}", "Other"));
    Check("steam: Steam signed out, accounts disagree: can't tell", SteamConfig.LaunchProfile(env, 1874880) == null);
    File.WriteAllText(Path.Combine(steam, "userdata", "456", "config", "localconfig.vdf"), vdf);
    Check("steam: Steam signed out, accounts agree", SteamConfig.LaunchProfile(env, 1874880) == moved);
  }

  // A window that turns every offer down ("NOT NOW"), so a check can run RADIO CHECK start to finish without a fix ever
  // running: no installer, no closing TeamSpeak. A question without that choice goes unanswered (null: window closed).
  // Never answer a RADIO CHECK question with its first choice: that is UPDATE TEAMSPEAK or INSTALL IT THERE.
  class NoThanks : IConsole {
    public readonly List<string> Lines = new List<string>(), Questions = new List<string>();
    public void Line(string text, Tone tone) { Lines.Add(text); }
    public void Progress(string label, long got, long total, double rate) { }
    public string Ask(string question, params string[] choices) { Questions.Add(question); return Array.IndexOf(choices, "NOT NOW") >= 0 ? "NOT NOW" : null; }
    public string Input(string prompt, string current, Func<string, string> problem, int maxLength) { return null; }
    public void Restart(string exe, string args) { }
    public void Changed() { }
  }

  // LAUNCH, before it starts anything: Steam's -profile gets linked.
  static void LaunchLink() {
    if (Environment.OSVersion.Platform != PlatformID.Win32NT) { Console.WriteLine("  (launch link checks skipped: not Windows)"); return; }
    if (TeamSpeak.Running().Length > 0) { Console.WriteLine("  (launch link checks skipped: TeamSpeak is running, and a link closes it)"); return; }

    var reg = new Dictionary<string, string>();
    var env = Sandbox("launch", reg);
    string steam = Path.Combine(root, "launch", "Steam");
    string moved = Path.Combine(root, "launch", "G", "Arma Moved");
    Directory.CreateDirectory(Path.Combine(steam, "userdata", "77", "config"));
    File.WriteAllText(Path.Combine(steam, "userdata", "77", "config", "localconfig.vdf"),
      "\"UserLocalConfigStore\" { \"apps\" { \"1874880\" { \"LaunchOptions\" \"-profile \\\"" + moved.Replace("\\", "\\\\") + "\\\" -window\" } } }");
    reg[@"HKCU|Software\Valve\Steam|SteamPath"] = steam;
    reg[@"HKCU|Software\Valve\Steam\ActiveProcess|ActiveUser"] = "77";
    Directory.CreateDirectory(env.ProfileDir);
    File.WriteAllText(Path.Combine(env.ProfileDir, "VONPluginInfo.json"), "old plugin file");

    // the only answers given are NOT NOW, then the exact link choice
    var no = new FakeConsole();
    no.Answers.Enqueue("NOT NOW");
    var c = new Comms(env, env.HomeExe, "1.0.0") { Out = no };
    c.M.GameProcess = "BifrostCommsTestsNoGame";   // a Reforger running on this PC (a test instance, say) isn't this check's game
    c.LinkProfileBeforeLaunch();
    Check("launch: asks about Steam's -profile", no.Prompts.Count == 1 && no.Lines.Exists(l => l.Contains("-profile") && l.Contains("Arma Moved")));
    Check("launch: NOT NOW links nothing", !Doctor.IsLink(env.ProfileDir) && !Directory.Exists(moved));

    var yes = new FakeConsole();
    yes.Answers.Enqueue("LINK THE FOLDERS");
    c.Out = yes;
    c.LinkProfileBeforeLaunch();
    string game = Path.Combine(moved, "profile");
    Check("launch: LINK makes the folder Reforger will use and links the plugin's to it", Doctor.IsLink(env.ProfileDir) && Doctor.SameDir(env.ProfileDir, game));
    Check("launch: the plugin's old folder is kept", File.ReadAllText(Path.Combine(env.ProfileDir + ".bifrost-old", "VONPluginInfo.json")) == "old plugin file");
    File.WriteAllText(Path.Combine(env.ProfileDir, "VONPluginInfo.json"), "{}");
    Check("launch: what the plugin writes lands where the game reads", File.Exists(Path.Combine(game, "VONPluginInfo.json")));
    c.LinkProfileBeforeLaunch();
    Check("launch: linked already: no question", yes.Prompts.Count == 1);

    var plain = Sandbox("launch-plain", null);
    var none = new FakeConsole();
    var c2 = new Comms(plain, plain.HomeExe, "1.0.0") { Out = none };
    c2.M.GameProcess = "BifrostCommsTestsNoGame";
    c2.LinkProfileBeforeLaunch();
    Check("launch: no -profile: no question", none.Prompts.Count == 0 && none.Lines.Count == 0);
  }

  // a unit with a server, as comms.json gives it
  static Manifest UnitManifest() {
    var m = Manifest.BuiltIn();
    m.Host = "203.0.113.10"; m.Port = 9987; m.ServerName = "Example Unit";
    return m;
  }

  static Facts Healthy(string profile) {
    var tabs = new List<TsConnection> { new TsConnection { Handler = 1, Ip = "203.0.113.10", Port = 9987, ChannelPath = "EXU/Reforger In-Game (VON Test)", Active = true } };
    return new Facts { ProfileDir = profile, TsInstalled = true, TsRunning = true, PluginInstalled = true, PluginFresh = true, TsVersion = "3.6.2",
      TsDir = Path.Combine(root, "ts"), RunningTsDir = Path.Combine(root, "ts"), GameRunning = true, HeartbeatAge = 0.4, Tabs = tabs,
      Sd = new ServerData { InGame = true, ClientId = 22, ChannelName = "Reforger In-Game (VON Test)" } };
  }

  static Fix Only(List<Finding> found) { return found.Count == 1 ? found[0].Fix : (Fix)(-1); }

  static void Diagnoses() {
    var m = UnitManifest();
    string profile = Path.Combine(root, "diag", "Documents", "My Games", "ArmaReforger", "profile");
    Directory.CreateDirectory(profile);
    Check("diagnose: all good = nothing to report", Doctor.Diagnose(Healthy(profile), m).Count == 0);

    var f = Healthy(profile); f.TsInstalled = false;
    Check("diagnose: no TeamSpeak", Doctor.Diagnose(f, m).Count == 1 && Doctor.Diagnose(f, m)[0].Problem.Contains("isn't installed"));
    f = Healthy(profile); f.TsVersion = "3.5.6"; f.PluginFresh = false;
    var found = Doctor.Diagnose(f, m);
    Check("diagnose: TeamSpeak 3.5 is too old (API 26 needs 3.6)", found.Count >= 1 && found[0].Fix == Fix.UpdateTeamSpeak && !found.Exists(x => x.Fix == Fix.RestartTeamSpeak));
    f = Healthy(profile); f.PluginInstalled = false; f.PluginFresh = false;
    Check("diagnose: plugin not installed: SET UP", Doctor.Diagnose(f, m).Count == 1 && Doctor.Diagnose(f, m)[0].Advice.Contains("SET UP") && Doctor.Diagnose(f, m)[0].Fix == Fix.None);
    f = Healthy(profile); f.RunningTsDir = Path.Combine(root, "portable-ts"); f.PluginFresh = false;
    found = Doctor.Diagnose(f, m);
    Check("diagnose: a different TeamSpeak is running", found.Count >= 1 && found[0].Fix == Fix.PluginIntoRunningTs && found[0].Target == f.RunningTsDir);
    f = Healthy(profile); f.PluginFresh = false; f.RuntimeMissing = true;
    Check("diagnose: plugin not loaded, Visual C++ runtime missing", Doctor.Diagnose(f, m).Count >= 1 && Doctor.Diagnose(f, m)[0].Fix == Fix.VcRuntime);
    f = Healthy(profile); f.PluginFresh = false; f.GameRunning = false;
    Check("diagnose: plugin not loaded, runtime there: restart TeamSpeak", Only(Doctor.Diagnose(f, m)) == Fix.RestartTeamSpeak);

    // the moved-profile case: the plugin works, the game keeps its radio files elsewhere
    f = Healthy(profile); f.HeartbeatAge = -1; f.Sd = null; f.GameProfileDir = Path.Combine(root, "G", "ArmaProfile", "profile");
    found = Doctor.Diagnose(f, m);
    Check("diagnose: -profile elsewhere: link the folders", Only(found) == Fix.LinkProfile && found[0].Target == f.GameProfileDir && found[0].Problem.Contains("-profile"));
    f = Healthy(profile); f.HeartbeatAge = -1; f.Sd = null; f.GameProfileDir = profile;
    Check("diagnose: -profile at the plugin's own folder is fine", Doctor.Diagnose(f, m).Count == 1 && Doctor.Diagnose(f, m)[0].Tone == Tone.Accent);
    f = Healthy(profile); f.HeartbeatAge = 600; f.ElsewhereDir = Path.Combine(root, "OneDrive", "My Games", "ArmaReforger", "profile");
    found = Doctor.Diagnose(f, m);
    Check("diagnose: the game's heartbeat in another folder: link to it", Only(found) == Fix.LinkProfile && found[0].Target == f.ElsewhereDir);
    f = Healthy(profile); f.HeartbeatAge = -1; f.Sd = null; f.FolderAccessOn = true;
    Check("diagnose: no signal with Controlled folder access on", Only(Doctor.Diagnose(f, m)) == Fix.FolderAccess);
    f = Healthy(profile); f.HeartbeatAge = -1; f.Sd = null;
    found = Doctor.Diagnose(f, m);
    Check("diagnose: no signal, nothing wrong: a hint to join and spawn", found.Count == 1 && found[0].Tone == Tone.Accent && found[0].Fix == Fix.None && found[0].Advice.Contains("Coalition radio server"));
    f = Healthy(profile); f.Sd = new ServerData { InGame = false };
    found = Doctor.Diagnose(f, m);
    Check("diagnose: on a radio server, not spawned: a hint", found.Count == 1 && found[0].Tone == Tone.Accent && found[0].Problem.Contains("spawned"));

    // in game: TeamSpeak's side
    f = Healthy(profile); f.Tabs = new List<TsConnection> { new TsConnection { Handler = 1, Ip = "10.0.0.5", Port = 9987, Active = true } };
    found = Doctor.Diagnose(f, m);
    Check("diagnose: TeamSpeak not on the unit's server: connect", Only(found) == Fix.Connect && found[0].Problem.Contains("Example Unit"));
    f = Healthy(profile); f.Tabs[0].Active = false; f.Tabs.Add(new TsConnection { Handler = 2, Ip = "10.0.0.5", Port = 9987, Active = true });
    found = Doctor.Diagnose(f, m);
    Check("diagnose: the unit's tab isn't the selected one", found.Count == 1 && found[0].Problem.Contains("selected tab") && found[0].Fix == Fix.None);
    f = Healthy(profile); f.Tabs = null; f.Sd.ClientId = 0;
    Check("diagnose: the plugin sees no TeamSpeak connection", Doctor.Diagnose(f, m).Count == 1 && Doctor.Diagnose(f, m)[0].Problem.Contains("Not Detected"));
    f = Healthy(profile); f.Tabs[0].ChannelPath = "EXU/Lobby";
    Check("diagnose: not moved into the radio channel", Doctor.Diagnose(f, m).Count == 1 && Doctor.Diagnose(f, m)[0].Problem.Contains("hasn't moved you")
      && Doctor.Diagnose(f, m)[0].Advice.Contains("your unit's admins"));
    f = Healthy(profile); f.Tabs.Add(new TsConnection { Handler = 2, Ip = "10.0.0.5", Port = 9987 });
    Check("diagnose: a second tab is fine while the unit's is selected", Doctor.Diagnose(f, m).Count == 0);

    // comms.json may name the server instead of giving its address: the program's own lookup decides which tab is the unit's
    f = Healthy(profile); f.Tabs[0].Ip = "198.51.100.9"; f.IsUnitServer = delegate(TsConnection t) { return t.Ip == "198.51.100.9"; };
    Check("diagnose: a tab the program says is the unit's counts, whatever its address", Doctor.Diagnose(f, m).Count == 0);
    f = Healthy(profile); f.IsUnitServer = delegate(TsConnection t) { return false; };
    Check("diagnose: no tab is the unit's: connect", Only(Doctor.Diagnose(f, m)) == Fix.Connect);

    // no server set at all (no comms.json, nothing typed in): there is no "the unit's tab" to get wrong
    var unset = UnitManifest(); unset.Host = null; unset.ServerName = null;
    f = Healthy(profile); f.Tabs = new List<TsConnection> { new TsConnection { Handler = 1, Ip = "10.0.0.5", Port = 9987, Active = true } };
    Check("diagnose: no server set: the TeamSpeak tab can't be the wrong one", Doctor.Diagnose(f, unset).Count == 0);
    f = Healthy(profile); f.Sd.ClientId = 0;
    found = Doctor.Diagnose(f, unset);
    Check("diagnose: no server set: the plugin seeing no connection is still said", found.Count == 1 && found[0].Problem.Contains("Not Detected") && !found[0].Advice.Contains("Example"));
    Check("report: no server set is said", Doctor.Report(f, unset).Exists(x => x.Key.Contains("not set")));

    var every = Healthy(profile); every.GameOptions = "-window"; every.LaunchOptions.Add("-window");
    var report = Doctor.Report(every, m);
    Check("report: lists the folder, the game and the tabs", report.Exists(x => x.Key.StartsWith("radio files folder")) && report.Exists(x => x.Key.Contains("options '-window'")) && report.Exists(x => x.Key.Contains("<- selected")));
    Check("report: the unit's tab is named", report.Exists(x => x.Key.Contains("(Example Unit)")));
  }

  // RADIO CHECK start to finish on a sandbox PC, with a window that declines every offer.
  static void RadioCheckRun() {
    var env = Sandbox("rc", null);
    var no = new NoThanks();
    var c = new Comms(env, env.HomeExe, "1.0.0") { Out = no };
    c.M.GameProcess = "BifrostCommsTestsNoGame";
    c.RadioCheck(null);
    Check("radio check: the button prints what it looked at", no.Lines.Contains("=== RADIO CHECK ===") && no.Lines.Exists(l => l.StartsWith("radio files folder: " + env.ProfileDir)));
    Check("radio check: no TeamSpeak is a problem with nothing to press", no.Lines.Exists(l => l.StartsWith("PROBLEM: TeamSpeak 3 isn't installed")) && no.Questions.Count == 0);
    Check("radio check: it sums up, pointing at the unit's admins", no.Lines.Exists(l => l.StartsWith("radio check: 1 problem, 0 fixed") && l.Contains("your unit's admins")));

    var auto = new NoThanks();
    c.Out = auto;
    c.RadioCheck("no radio signal from the game");
    Check("radio check: the automatic run says why and prints only what's wrong", auto.Lines.Contains("=== RADIO CHECK: no radio signal from the game ===")
      && !auto.Lines.Exists(l => l.StartsWith("radio files folder")) && auto.Lines.Exists(l => l.StartsWith("PROBLEM:")));

    if (Environment.OSVersion.Platform != PlatformID.Win32NT) return;
    if (TeamSpeak.Running().Length > 0) { Console.WriteLine("  (radio check with an old TeamSpeak skipped: TeamSpeak is running)"); return; }
    // an old TeamSpeak 3 (a file that isn't a program reads as version 0): the offer to update comes up and NOT NOW leaves it be
    string tsDir = Path.Combine(env.ProgramFiles, "TeamSpeak 3 Client");
    Directory.CreateDirectory(tsDir);
    File.WriteAllBytes(Path.Combine(tsDir, "ts3client_win64.exe"), new byte[] { 0x4d, 0x5a });
    var offer = new NoThanks();
    c.Out = offer;
    c.RadioCheck(null);
    Check("radio check: an old TeamSpeak is offered an update", offer.Questions.Count == 1 && offer.Questions[0].StartsWith("Update TeamSpeak to"));
    Check("radio check: NOT NOW leaves it as it is", offer.Lines.Exists(l => l.StartsWith("left as it is")) && !Directory.Exists(env.Downloads)
      && offer.Lines.Exists(l => l.StartsWith("radio check: 2 problems, 0 fixed")));
  }

  // The automatic RADIO CHECK: when the link stays stuck, once each time, with times the check hands in.
  static void StuckWatch() {
    var env = Sandbox("stuck", null);
    var c = new Comms(env, env.HomeExe, "1.0.0");
    var t0 = new DateTime(2026, 10, 28, 20, 0, 0);
    Func<Link, LinkState> state = delegate(Link l) { return new LinkState { Link = l }; };

    c.WatchForStuck(state(Link.NoPlugin), true, false, false, false, t0);
    c.WatchForStuck(state(Link.NoPlugin), true, false, false, false, t0.AddSeconds(40));
    Check("stuck: plugin not loaded for 40 s: not yet", c.TakePendingCheck() == null);
    c.WatchForStuck(state(Link.NoPlugin), true, false, false, false, t0.AddSeconds(50));
    Check("stuck: plugin not loaded for 50 s: RADIO CHECK is due", c.TakePendingCheck() == "the radio plugin isn't loaded");
    c.WatchForStuck(state(Link.NoPlugin), true, false, false, false, t0.AddSeconds(500));
    Check("stuck: it runs once for one stuck spell", c.TakePendingCheck() == null);
    c.WatchForStuck(state(Link.Linked), true, true, true, false, t0.AddSeconds(510));
    c.WatchForStuck(state(Link.Joining), true, true, true, false, t0.AddSeconds(520));
    c.WatchForStuck(state(Link.Joining), true, true, true, false, t0.AddSeconds(555));
    Check("stuck: stuck again after a good spell: it runs again", c.TakePendingCheck() == "teamspeak hasn't linked the radio");

    c.WatchForStuck(state(Link.Off), false, false, false, false, t0.AddSeconds(600));
    c.WatchForStuck(state(Link.Standby), true, true, true, false, t0.AddSeconds(610));
    c.WatchForStuck(state(Link.Standby), true, true, true, false, t0.AddSeconds(610 + 140));
    Check("stuck: on a radio server, not spawned for 140 s: not yet", c.TakePendingCheck() == null);
    c.WatchForStuck(state(Link.Standby), true, true, true, false, t0.AddSeconds(610 + 155));
    Check("stuck: on a radio server, not spawned for 155 s: due", c.TakePendingCheck() == "you haven't spawned in");
    c.WatchForStuck(state(Link.Standby), true, true, false, false, t0.AddSeconds(900));
    c.WatchForStuck(state(Link.Standby), true, true, false, false, t0.AddSeconds(900 + 170));
    Check("stuck: no game signal for 170 s: not yet", c.TakePendingCheck() == null);
    c.WatchForStuck(state(Link.Standby), true, true, false, false, t0.AddSeconds(900 + 185));
    Check("stuck: no game signal for 185 s: due", c.TakePendingCheck() == "no radio signal from the game");

    c.WatchForStuck(state(Link.Off), false, false, false, false, t0.AddSeconds(1200));
    c.WatchForStuck(state(Link.Split), true, true, false, false, t0.AddSeconds(1210));
    c.WatchForStuck(state(Link.Split), true, true, false, false, t0.AddSeconds(1212));
    Check("stuck: a moved profile folder doesn't wait", c.TakePendingCheck() == "Reforger keeps its radio files in another folder");
    c.WatchForStuck(state(Link.Off), false, false, false, false, t0.AddSeconds(1300));
    c.WatchForStuck(state(Link.Split), true, true, false, true, t0.AddSeconds(1310));
    c.WatchForStuck(state(Link.Split), true, true, false, true, t0.AddSeconds(1320));
    Check("stuck: a moved profile folder LAUNCH already asked about stays quiet", c.TakePendingCheck() == null);
    c.WatchForStuck(state(Link.Off), false, false, false, false, t0.AddSeconds(1400));
    c.WatchForStuck(state(Link.Off), false, false, false, false, t0.AddSeconds(5000));
    Check("stuck: the game being off is never stuck", c.TakePendingCheck() == null);
  }

  static void PcChecks() {
    var env = Sandbox("pc", null);
    env.SystemDir = Path.Combine(root, "pc", "Windows", "System32");
    Directory.CreateDirectory(env.SystemDir);
    string tsDir = Path.Combine(env.ProgramFiles, "TeamSpeak 3 Client");
    Directory.CreateDirectory(tsDir);
    Check("runtime: missing from Windows and TeamSpeak", Doctor.RuntimeMissing(env, tsDir));
    File.WriteAllText(Path.Combine(tsDir, "vcruntime140.dll"), "MZ");
    Check("runtime: beside TeamSpeak counts", !Doctor.RuntimeMissing(env, tsDir));
    File.WriteAllText(Path.Combine(env.SystemDir, "vcruntime140.dll"), "MZ");
    Check("runtime: installed in Windows", !Doctor.RuntimeMissing(env, null));
    env.SystemDir = null;
    Check("runtime: unknown Windows folder isn't reported missing", !Doctor.RuntimeMissing(env, null));

    // the game's heartbeat in a folder the plugin doesn't read
    Directory.CreateDirectory(env.ProfileDir);
    File.WriteAllText(Path.Combine(env.ProfileDir, "VONHeartbeat.json"), "{}");
    Check("elsewhere: the plugin's own folder doesn't count", Doctor.HeartbeatElsewhere(env, null) == null);
    string other = Path.Combine(root, "pc", "OneDrive", "Documents");
    string otherProfile = Path.Combine(other, "My Games", "ArmaReforger", "profile");
    Directory.CreateDirectory(otherProfile);
    File.WriteAllText(Path.Combine(otherProfile, "VONHeartbeat.json"), "{}");
    File.SetLastWriteTimeUtc(Path.Combine(otherProfile, "VONHeartbeat.json"), DateTime.UtcNow.AddMinutes(-5));
    env.OtherDocuments = new[] { other };
    string workbench = Path.Combine(env.Documents, "My Games", "ArmaReforgerWorkbench", "profile");
    Directory.CreateDirectory(workbench);
    File.WriteAllText(Path.Combine(workbench, "VONHeartbeat.json"), "{}");
    Check("elsewhere: a Workbench test run isn't the game", Doctor.HeartbeatElsewhere(env, null) == null);
    Check("elsewhere: an old heartbeat isn't the game", Doctor.HeartbeatElsewhere(env, null) == null);
    File.SetLastWriteTimeUtc(Path.Combine(otherProfile, "VONHeartbeat.json"), DateTime.UtcNow);
    Check("elsewhere: a fresh heartbeat in OneDrive's Documents", Doctor.HeartbeatElsewhere(env, null) == otherProfile);
    string custom = Path.Combine(root, "pc", "G", "ArmaProfile", "profile");
    Directory.CreateDirectory(custom);
    File.WriteAllText(Path.Combine(custom, "VONHeartbeat.json"), "{}");
    Check("elsewhere: the -profile folder comes first", Doctor.HeartbeatElsewhere(env, custom) == custom);

    // the running TeamSpeak's own install: registry, or a portable config folder
    var reg = new Dictionary<string, string>();
    var env2 = Sandbox("atdir", reg);
    string portable = Path.Combine(root, "atdir", "TS3Portable");
    Directory.CreateDirectory(Path.Combine(portable, "config"));
    File.WriteAllBytes(Path.Combine(portable, "ts3client_win64.exe"), new byte[] { 0x4d, 0x5a });
    File.WriteAllText(Path.Combine(portable, "config", "settings.db"), "");
    var t = TeamSpeak.AtDir(env2, portable);
    Check("AtDir: a portable copy keeps its plugins beside it", t != null && t.ConfigInInstallDir && t.PluginsDir == Path.Combine(portable, "config", "plugins"));
    reg[@"HKCU|Software\TeamSpeak 3 Client|"] = portable;
    reg[@"HKCU|Software\TeamSpeak 3 Client|ConfigLocation"] = "0";
    t = TeamSpeak.AtDir(env2, portable + "\\");
    Check("AtDir: the registry's choice wins for its own folder", t != null && !t.ConfigInInstallDir);
    Check("AtDir: no client there", TeamSpeak.AtDir(env2, Path.Combine(root, "atdir", "nothing")) == null);
  }

  static void FolderLink() {
    if (Environment.OSVersion.Platform != PlatformID.Win32NT) { Console.WriteLine("  (folder link checks skipped: not Windows)"); return; }
    string pc = Path.Combine(root, "link");
    string plugin = Path.Combine(pc, "Documents", "My Games", "ArmaReforger", "profile");
    string game = Path.Combine(pc, "G", "Arma Profile", "profile");
    Directory.CreateDirectory(plugin);
    Directory.CreateDirectory(game);
    File.WriteAllText(Path.Combine(plugin, "VONPluginInfo.json"), "old plugin file");
    File.WriteAllText(Path.Combine(game, "VONHeartbeat.json"), "{}");
    Check("link: different folders before", !Doctor.SameDir(plugin, game));
    Throws<IOException>("link: a missing game folder is refused", delegate { Doctor.LinkFolders(plugin, Path.Combine(pc, "nowhere")); });
    Check("link: a refused link moves nothing", File.Exists(Path.Combine(plugin, "VONPluginInfo.json")) && !Doctor.IsLink(plugin));
    string aside = Doctor.LinkFolders(plugin, game);
    Check("link: the plugin's folder now opens the game's", Doctor.IsLink(plugin) && Doctor.SameDir(plugin, game) && File.Exists(Path.Combine(plugin, "VONHeartbeat.json")));
    Check("link: the old folder is kept beside it", aside == plugin + ".bifrost-old" && File.ReadAllText(Path.Combine(aside, "VONPluginInfo.json")) == "old plugin file");
    File.WriteAllText(Path.Combine(plugin, "VONServerData.json"), "{}");
    Check("link: what the plugin writes lands in the game's folder", File.Exists(Path.Combine(game, "VONServerData.json")));
    Check("link: the plugin reads the game's heartbeat through it", new Bridge(plugin).HeartbeatAge(DateTime.UtcNow) >= 0);
    Check("link: linking again changes nothing", Doctor.LinkFolders(plugin, game) == null && Doctor.SameDir(plugin, game));
    string game2 = Path.Combine(pc, "H", "profile");
    Directory.CreateDirectory(game2);
    string aside2 = Doctor.LinkFolders(plugin, game2);
    Check("link: an old link moves aside as a link, the game's files stay", aside2 == plugin + ".bifrost-old-2" && Doctor.SameDir(plugin, game2) && File.Exists(Path.Combine(game, "VONHeartbeat.json")));
    Directory.Delete(aside2, false);   // the junction only
    Check("link: removing a link leaves the folder it opened", File.Exists(Path.Combine(game, "VONServerData.json")));
  }

  // ---------------------------------------------------------------------------------------------- pictures

  // the pictures show this build's version
  static readonly string Shown = Comms.ExeVersion(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "1.0.0";

  static void Renders(string dir) {
    TerminalForm form;
    Comms c;
    TerminalForm.Machine = "player-pc";
    try {
      var env = Sandbox("renders0", null);
      c = new Comms(env, env.HomeExe, Shown);
      form = new TerminalForm(c, false) { UiScale = 1f };
    } catch (Exception e) {
      Console.WriteLine("  (renders skipped: " + e.GetType().Name + ": " + e.Message + ")");
      return;
    }
    Directory.CreateDirectory(dir);
    var size = new Size(1040, 680);
    Action<string> shot = delegate(string name) {
      form.ShowAllForPicture();
      using (var bmp = new Bitmap(size.Width, size.Height)) {
        using (var g = Graphics.FromImage(bmp)) form.RenderTo(g, size);
        bmp.Save(Path.Combine(dir, name + ".png"), ImageFormat.Png);
      }
    };

    // a player who got the program without a comms.json
    lock (c.Lock) {
      c.St.Uplink = "LOCAL"; c.St.UplinkDetail = "built-in pins (no unit manifest)"; c.St.UplinkTone = Tone.Ok;
      c.St.Ts = "3.6.2  READY"; c.St.TsDetail = "installed for you"; c.St.TsTone = Tone.Ok;
      c.St.Plugin = "3.0.1  INSTALLED"; c.St.PluginDetail = "loads when TeamSpeak starts"; c.St.PluginTone = Tone.Ok;
      c.St.Updates = "UP TO DATE"; c.St.UpdatesDetail = "Bifrost Comms " + Shown; c.St.UpdatesTone = Tone.Ok;
    }
    form.Line("teamspeak 3 client 3.6.2 ............ OK", Tone.Ok);
    form.Line("coalition radio plugin 3.0.1 ........ OK", Tone.Ok);
    form.Line("no TeamSpeak server yet: SET UP asks for the address your unit gave you", Tone.Accent);
    var typing = new Thread(delegate() { form.Input("TEAMSPEAK SERVER (the address your unit gave you, e.g. ts.example.com or 203.0.113.10:9987)", "ts.example.com:99", UnitConfig.AddressProblem, 100); }) { IsBackground = true };
    typing.Start();
    Thread.Sleep(200);
    shot("0-server-address");
    form.Close();   // ends the input
    typing.Join(2000);

    var env1 = Sandbox("renders", null);
    Directory.CreateDirectory(env1.Home);
    File.WriteAllText(env1.HomeUnit, "{\"name\":\"Example Unit\",\"host\":\"ts.example.com\",\"port\":9987}");
    c = new Comms(env1, env1.HomeExe, Shown);
    form = new TerminalForm(c, false) { UiScale = 1f };
    c.Settings.Callsign = "PFC J. Doe";
    lock (c.Lock) {
      c.St.Uplink = "LOCAL"; c.St.UplinkDetail = "built-in pins (no unit manifest)"; c.St.UplinkTone = Tone.Ok;
      c.St.Ts = "NOT INSTALLED"; c.St.TsDetail = "SET UP installs 3.6.2"; c.St.TsTone = Tone.Bad;
      c.St.Plugin = "NOT INSTALLED"; c.St.PluginDetail = "SET UP installs 3.0.1"; c.St.PluginTone = Tone.Bad;
      c.St.Updates = "UP TO DATE"; c.St.UpdatesDetail = "Bifrost Comms " + Shown; c.St.UpdatesTone = Tone.Ok;
    }
    form.Line("teamspeak 3 client ................ NOT FOUND", Tone.Warn);
    form.Line("coalition radio plugin ............ NOT INSTALLED", Tone.Warn);
    form.Line("teamspeak server ts.example.com:9987 (Example Unit)", Tone.Dim);
    form.Line("press SET UP to install what's missing", Tone.Accent);
    shot("1-first-run");

    form.Line("=== SETUP ===", Tone.Accent);
    form.Line("teamspeak 3 client 3.6.2: downloading from teamspeak.com", Tone.Text);
    form.Progress("TeamSpeak 3 Client 3.6.2", 69000000, 113041880, 9800000);
    c.Busy = true;
    shot("2-downloading");
    c.Busy = false;
    form.Progress(null, 0, 0, 0);

    form.Line("TeamSpeak 3 Client 3.6.2: 107.8 MB, sha256 eab9e0c1a713... verified", Tone.Dim);
    form.Line("sha256 verified. Installing for this Windows user (no Overwolf) ...", Tone.Text);
    form.Line("BLOCKED OverwolfInstallerDownloader.exe (TeamSpeak's installer bundles Overwolf)", Tone.Warn);
    form.Line("teamspeak 3 client 3.6.2 installed ........ OK", Tone.Ok);
    form.Line("coalition radio plugin 3.0.1 installed to C:\\Users\\player\\AppData\\Roaming\\TS3Client\\plugins", Tone.Ok);
    form.Line("shortcuts: desktop + Start menu > Bifrost Comms ............ OK", Tone.Ok);
    lock (c.Lock) {
      c.St.Ts = "3.6.2  READY"; c.St.TsDetail = "installed for you"; c.St.TsTone = Tone.Ok;
      c.St.Plugin = "3.0.1  INSTALLED"; c.St.PluginDetail = "loads when TeamSpeak starts"; c.St.PluginTone = Tone.Ok;
    }
    var asking = new Thread(delegate() { form.Ask("Connect TeamSpeak to Example Unit now (adds the bookmark)?", "CONNECT", "LATER"); }) { IsBackground = true };
    asking.Start();
    Thread.Sleep(200);
    shot("3-question");
    form.Close();   // answers the question
    asking.Join(2000);

    var env2 = Sandbox("renders2", null);
    Directory.CreateDirectory(env2.Home);
    File.WriteAllText(env2.HomeUnit, "{\"name\":\"Example Unit\",\"host\":\"ts.example.com\",\"port\":9987}");
    var c2 = new Comms(env2, env2.HomeExe, Shown);
    var f2 = new TerminalForm(c2, false) { UiScale = 1f };
    c2.Settings.Callsign = "PFC J. Doe";
    lock (c2.Lock) {
      c2.St.Uplink = "LOCAL"; c2.St.UplinkDetail = "built-in pins (no unit manifest)"; c2.St.UplinkTone = Tone.Ok;
      c2.St.Ts = "3.6.2  RUNNING"; c2.St.TsDetail = "installed for all users"; c2.St.TsTone = Tone.Ok;
      c2.St.Plugin = "3.0.1  LOADED"; c2.St.PluginDetail = "talking to TeamSpeak"; c2.St.PluginTone = Tone.Ok;
      c2.St.TsLink = "CONNECTED"; c2.St.TsLinkDetail = "Reforger In-Game (VON)"; c2.St.TsLinkTone = Tone.Ok;
      c2.St.Game = "RUNNING"; c2.St.GameTone = Tone.Ok;
      c2.St.Link = Bridge.Derive(true, true, true, new ServerData { InGame = true, ChannelName = "Reforger In-Game (VON)", ClientId = 7 }, 0.5, "EXU/Reforger In-Game (VON)");
      c2.St.Updates = "UP TO DATE"; c2.St.UpdatesDetail = "Bifrost Comms " + Shown; c2.St.UpdatesTone = Tone.Ok;
    }
    f2.Line("=== LAUNCH: REFORGER + RADIO ===", Tone.Accent);
    f2.Line("teamspeak: connecting to ts.example.com:9987 as PFC J. Doe", Tone.Ok);
    f2.Line("radio plugin 3.0.1 online [bin1|talking1|heartbeat1|comms1] ........ OK", Tone.Ok);
    f2.Line("teamspeak: connected to Example Unit ............ OK", Tone.Ok);
    f2.Line("arma reforger: launching through Steam ...", Tone.Ok);
    f2.Line("arma reforger started", Tone.Ok);
    f2.Line("radio link: JOINING - moving you to Reforger In-Game (VON)", Tone.Warn);
    f2.Line("radio link: LINKED - in game, in Reforger In-Game (VON)", Tone.Ok);
    form = f2; c = c2;
    shot("4-linked");
    f2.Close();

    // a moved profile folder: the game is down for the radio, RADIO CHECK finds the -profile folder and offers to link it
    var env3 = Sandbox("renders3", null);
    Directory.CreateDirectory(env3.Home);
    File.WriteAllText(env3.HomeUnit, "{\"name\":\"Example Unit\",\"host\":\"ts.example.com\",\"port\":9987}");
    var c3 = new Comms(env3, env3.HomeExe, Shown);
    var f3 = new TerminalForm(c3, false) { UiScale = 1f };
    c3.Settings.Callsign = "PFC J. Doe";
    lock (c3.Lock) {
      c3.St.Uplink = "LOCAL"; c3.St.UplinkDetail = "built-in pins (no unit manifest)"; c3.St.UplinkTone = Tone.Ok;
      c3.St.Ts = "3.6.2  RUNNING"; c3.St.TsDetail = "installed for you"; c3.St.TsTone = Tone.Ok;
      c3.St.Plugin = "3.0.1  LOADED"; c3.St.PluginDetail = "talking to TeamSpeak"; c3.St.PluginTone = Tone.Ok;
      c3.St.TsLink = "CONNECTED"; c3.St.TsLinkDetail = "Lobby"; c3.St.TsLinkTone = Tone.Ok;
      c3.St.Game = "RUNNING"; c3.St.GameTone = Tone.Ok;
      c3.St.Link = Bridge.Derive(true, true, true, null, -1, "EXU/Lobby", false, "G:\\Arma\\profile");
      c3.St.Updates = "UP TO DATE"; c3.St.UpdatesDetail = "Bifrost Comms " + Shown; c3.St.UpdatesTone = Tone.Ok;
    }
    f3.Line("arma reforger started", Tone.Ok);
    f3.Line("radio link: " + c3.St.Link.Text + " - " + c3.St.Link.Detail, Tone.Bad);
    f3.Line("=== RADIO CHECK: Reforger keeps its radio files in another folder ===", Tone.Accent);
    f3.Line("PROBLEM: Reforger is started with -profile, so it keeps its radio files in G:\\Arma\\profile, but the Coalition plugin only looks in C:\\Users\\player\\Documents\\My Games\\ArmaReforger\\profile.", Tone.Bad);
    f3.Line("  LINK THE FOLDERS makes the plugin's folder open Reforger's (your profile, settings and mods stay where they are).", Tone.Text);
    var asking3 = new Thread(delegate() { f3.Ask("Link the plugin's folder to Reforger's profile folder?", "LINK THE FOLDERS", "NOT NOW"); }) { IsBackground = true };
    asking3.Start();
    Thread.Sleep(200);
    form = f3; c = c3;
    shot("5-radio-check");
    f3.Close();   // answers the question
    asking3.Join(2000);

    f3 = new TerminalForm(c3, false) { UiScale = 1f };
    lock (c3.Lock) {
      c3.St.TsLinkDetail = "Reforger In-Game (VON)";
      c3.St.Link = Bridge.Derive(true, true, true, new ServerData { InGame = true, ChannelName = "Reforger In-Game (VON)", ClientId = 22 }, 0.4, "EXU/Reforger In-Game (VON)");
    }
    f3.Line("> LINK THE FOLDERS", Tone.Dim);
    f3.Line("linked: C:\\Users\\player\\Documents\\My Games\\ArmaReforger\\profile now opens G:\\Arma\\profile ........ OK", Tone.Ok);
    f3.Line("the folder that was there is kept as C:\\Users\\player\\Documents\\My Games\\ArmaReforger\\profile.bifrost-old", Tone.Dim);
    f3.Line("teamspeak: connecting to ts.example.com:9987 as PFC J. Doe", Tone.Ok);
    f3.Line("radio check: 1 problem, 1 fixed. Watch the RADIO LINK light.", Tone.Ok);
    f3.Line("radio link: LINKED - in game, in Reforger In-Game (VON)", Tone.Ok);
    form = f3; c = c3;
    shot("6-fixed");
    f3.Close();
    Check("renders written", File.Exists(Path.Combine(dir, "4-linked.png")) && File.Exists(Path.Combine(dir, "0-server-address.png"))
      && File.Exists(Path.Combine(dir, "5-radio-check.png")) && File.Exists(Path.Combine(dir, "6-fixed.png")));
  }
}

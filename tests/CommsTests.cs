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
      string renders = args.Length > 0 ? args[0] : Path.Combine("build", "renders");
      Renders(renders);
    } finally {
      try { Directory.Delete(root, true); } catch (Exception) { }
    }
    Console.WriteLine();
    Console.WriteLine(passed + " passed, " + failed + " failed");
    return failed == 0 ? 0 : 1;
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
    var sd = new ServerData { InGame = true, ChannelName = "Reforger In-Game (VON)" };
    Check("link: game off", Bridge.Derive(false, true, true, sd, 0, null).Link == Link.Off);
    Check("link: no TeamSpeak", Bridge.Derive(true, false, false, sd, 0, null).Link == Link.NoTeamSpeak);
    Check("link: no plugin", Bridge.Derive(true, true, false, sd, 0, null).Link == Link.NoPlugin);
    Check("link: standby in menus", Bridge.Derive(true, true, true, new ServerData { InGame = false }, -1, null).Link == Link.Standby);
    Check("link: heartbeat stopped", Bridge.Derive(true, true, true, sd, 12, "x").Link == Link.NoHeartbeat);
    Check("link: joining", Bridge.Derive(true, true, true, sd, 1, "EXU/Lobby").Link == Link.Joining);
    Check("link: linked in the VON channel", Bridge.Derive(true, true, true, sd, 1, "EXU/Reforger In-Game (VON)").Link == Link.Linked);
    Check("link: linked when the channel can't be seen", Bridge.Derive(true, true, true, sd, 1, null).Link == Link.Linked);
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
      c2.St.Link = Bridge.Derive(true, true, true, new ServerData { InGame = true, ChannelName = "Reforger In-Game (VON)" }, 0.5, "EXU/Reforger In-Game (VON)");
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
    Check("renders written", File.Exists(Path.Combine(dir, "4-linked.png")) && File.Exists(Path.Combine(dir, "0-server-address.png")));
  }
}

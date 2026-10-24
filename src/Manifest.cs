// What Bifrost Comms installs, pinned by SHA-256: the TeamSpeak 3 client and the Coalition TeamSpeak plugin. The pins
// built in here are what every release ships with. A unit can host its own manifest (a JSON file, "manifest" in
// comms.json) to move its players to a newer plugin, a newer Bifrost Comms or another server without a new download;
// any field it leaves out or gets wrong keeps the built-in value.
using System;
using System.Collections.Generic;
using System.Globalization;

class Manifest {
  public string LauncherVersion, LauncherUrl, LauncherSha, LauncherNotes;
  public long LauncherSize;
  public string TsVersion, TsUrl, TsSha;
  public long TsSize;
  public string PluginName, PluginVersion, PluginUrl, PluginSha, PluginDll, PluginDllSha;
  public long PluginSize;
  public string ServerName, Host, Bookmark, Password;   // Host null: no server yet (comms.json or the player gives it)
  public int Port = UnitConfig.DefaultPort;
  public bool ServerFromManifest;
  public bool StatusKnown, ServerOnline;
  public int Clients, Slots;
  public int SteamAppId;
  public string GameProcess;
  public bool FromManifest;   // false: the built-in pins (no manifest, or it didn't answer)

  public static Manifest BuiltIn() {
    return new Manifest {
      TsVersion = "3.6.2",
      TsUrl = "https://files.teamspeak-services.com/releases/client/3.6.2/TeamSpeak3-Client-win64-3.6.2.exe",
      TsSha = "eab9e0c1a7134643e5f7116b7e0e58faffb20d6db528f8b333d2c2b5d1ab68ae",
      TsSize = 113041880,
      PluginName = "Coalition TeamSpeak Plugin",
      PluginVersion = "3.0.1",
      PluginUrl = "https://github.com/CoalitionArma/Coalition-VON/releases/download/3.0.1/Coalition_TeamSpeak_Plugin_3.0.1.ts3_plugin",
      PluginSha = "911175464ec47ceae176961d01fa7f4a7b948843dfefae1244f6c81812227adf",
      PluginSize = 53109,
      PluginDll = "CRF_Teamspeak_Plugin_Main.dll",
      PluginDllSha = "e0ea673bbbc99c45122e2423f0d7d0bc98757d3e06535305e91048a1fb901cbe",
      SteamAppId = 1874880, GameProcess = "ArmaReforgerSteam",
    };
  }

  // The manifest over the built-in pins. A missing or malformed field keeps the built-in value, so a half-written
  // manifest can't leave the program without a download address or a hash. Relative addresses are read against url.
  public static Manifest Parse(string json, string url) {
    var m = BuiltIn();
    var d = Json.Parse(json);
    m.FromManifest = true;

    var l = Obj(d, "launcher");
    string launcherUrl = l == null ? null : Resolve(url, Json.Str(l, "url"));
    if (l != null && launcherUrl != null && Ver.Valid(Json.Str(l, "version")) && Sha(Json.Str(l, "sha256")) && Json.Long(l, "size") > 0) {
      m.LauncherVersion = Json.Str(l, "version");
      m.LauncherSha = Json.Str(l, "sha256").ToLowerInvariant();
      m.LauncherSize = Json.Long(l, "size");
      m.LauncherNotes = Json.Str(l, "notes");
      m.LauncherUrl = launcherUrl;
    }

    var t = Obj(d, "teamspeak");
    if (t != null && Ver.Valid(Json.Str(t, "version")) && Sha(Json.Str(t, "sha256")) && Https(Json.Str(t, "url"))) {
      m.TsVersion = Json.Str(t, "version"); m.TsSha = Json.Str(t, "sha256").ToLowerInvariant(); m.TsUrl = Json.Str(t, "url");
      m.TsSize = Json.Long(t, "size");
    }

    var p = Obj(d, "plugin");
    if (p != null && Ver.Valid(Json.Str(p, "version")) && Sha(Json.Str(p, "sha256")) && Https(Json.Str(p, "url")) && SafeName(Json.Str(p, "dll"))) {
      m.PluginVersion = Json.Str(p, "version"); m.PluginSha = Json.Str(p, "sha256").ToLowerInvariant(); m.PluginUrl = Json.Str(p, "url");
      m.PluginSize = Json.Long(p, "size"); m.PluginDll = Json.Str(p, "dll");
      string dllSha = Json.Str(p, "dll_sha256");
      m.PluginDllSha = Sha(dllSha) ? dllSha.ToLowerInvariant() : null;   // null: the package hash only (a unit following Coalition's releases)
      m.PluginName = Json.Str(p, "name") ?? m.PluginName;
    }

    var s = Obj(d, "server");
    if (s != null && Json.Str(s, "host") != null && UnitConfig.HostProblem(Json.Str(s, "host")) == null) {
      m.ServerFromManifest = true;
      m.Host = Json.Str(s, "host");
      long port = Json.Long(s, "port");
      if (port > 0 && port < 65536) m.Port = (int)port;
      m.ServerName = Json.Str(s, "name") ?? m.Host;
      m.Bookmark = Json.Str(s, "bookmark") ?? m.ServerName;
      m.Password = Json.Str(s, "password");
      var st = Obj(s, "status");
      if (st != null) {
        m.StatusKnown = true; m.ServerOnline = Json.Bool(st, "online");
        m.Clients = (int)Json.Long(st, "clients"); m.Slots = (int)Json.Long(st, "slots");
      }
    }

    var g = Obj(d, "game");
    if (g != null) {
      if (Json.Long(g, "steam_app_id") > 0) m.SteamAppId = (int)Json.Long(g, "steam_app_id");
      if (SafeName(Json.Str(g, "process"))) m.GameProcess = Json.Str(g, "process");
    }
    return m;
  }

  static Dictionary<string, object> Obj(Dictionary<string, object> d, string key) {
    object v;
    return d != null && d.TryGetValue(key, out v) ? v as Dictionary<string, object> : null;
  }

  public static bool Sha(string s) {
    if (s == null || s.Length != 64) return false;
    foreach (char c in s) if (!Uri.IsHexDigit(c)) return false;
    return true;
  }

  static bool Https(string url) { return url != null && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase); }

  static bool SafeName(string name) {
    return !string.IsNullOrEmpty(name) && name.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }) < 0 && name != "." && name != "..";
  }

  // A download address in the manifest, read against the manifest's own address: https anywhere, or plain http only on
  // the manifest's own (test) host. Null when it's missing or refused.
  public static string Resolve(string manifestUrl, string url) {
    Uri b, r;
    if (string.IsNullOrEmpty(url) || !Uri.TryCreate(manifestUrl, UriKind.Absolute, out b) || !Uri.TryCreate(b, url, out r)) return null;
    if (r.UserInfo.Length > 0) return null;
    if (r.Scheme == Uri.UriSchemeHttps) return r.AbsoluteUri;
    if (r.Scheme == b.Scheme && r.Host == b.Host && r.Port == b.Port) return r.AbsoluteUri;
    return null;
  }
}

// Dotted version numbers ("3.0.1", "1.0.10"), compared part by part.
static class Ver {
  public static bool Valid(string v) {
    if (string.IsNullOrEmpty(v)) return false;
    foreach (var part in v.Split('.')) { int n; if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out n)) return false; }
    return true;
  }

  // < 0 when a is older than b, 0 the same, > 0 newer. Invalid versions count as 0.0.
  public static int Compare(string a, string b) {
    var x = Parts(a); var y = Parts(b);
    for (int i = 0; i < Math.Max(x.Length, y.Length); i++) {
      int p = i < x.Length ? x[i] : 0, q = i < y.Length ? y[i] : 0;
      if (p != q) return p < q ? -1 : 1;
    }
    return 0;
  }

  static int[] Parts(string v) {
    if (!Valid(v)) return new int[0];
    var s = v.Split('.'); var r = new int[s.Length];
    for (int i = 0; i < s.Length; i++) r[i] = int.Parse(s[i], CultureInfo.InvariantCulture);
    return r;
  }
}

// The unit's comms.json: which TeamSpeak 3 server its players join, and optionally where the unit's manifest lives. A unit
// leader puts it beside BifrostComms.exe before sharing the two. When there's none, or it has no address, SET UP asks the
// player for the address and writes one. The installed copy keeps it in %LOCALAPPDATA%\Bifrost Comms.
//   { "name": "Example Unit", "host": "ts.example.com", "port": 9987, "password": "", "bookmark": "Example Unit", "manifest": "" }
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

class UnitConfig {
  public const int DefaultPort = 9987;
  public string Name, Host, Password, Bookmark, Manifest;
  public int Port = DefaultPort;
  public string File;

  public static UnitConfig Load(string file) {
    var u = new UnitConfig { File = file };
    try {
      if (!System.IO.File.Exists(file)) return u;
      var d = Json.Parse(System.IO.File.ReadAllText(file, Encoding.UTF8));
      u.Name = Blank(Json.Str(d, "name")); u.Password = Blank(Json.Str(d, "password"));
      u.Bookmark = Blank(Json.Str(d, "bookmark")); u.Manifest = Blank(Json.Str(d, "manifest"));
      string host = Blank(Json.Str(d, "host"));
      if (host != null && HostProblem(host) == null) u.Host = host;
      long port = Json.Long(d, "port");
      if (port > 0 && port < 65536) u.Port = (int)port;
    } catch (Exception) { }
    return u;
  }

  public void Save() {
    var d = new Dictionary<string, object>();
    d["name"] = Name ?? ""; d["host"] = Host ?? ""; d["port"] = Port; d["password"] = Password ?? "";
    d["bookmark"] = Bookmark ?? ""; d["manifest"] = Manifest ?? "";
    Directory.CreateDirectory(Path.GetDirectoryName(File));
    string tmp = File + ".tmp";
    System.IO.File.WriteAllText(tmp, Json.Write(d), new UTF8Encoding(false));
    if (System.IO.File.Exists(File)) System.IO.File.Delete(File);
    System.IO.File.Move(tmp, File);
  }

  static string Blank(string s) { return string.IsNullOrEmpty(s) || s.Trim().Length == 0 ? null : s.Trim(); }

  // The manifest's address: https, or a test server on this PC (http://127.0.0.1:<port>/...). Null when unset or refused.
  public string ManifestUrl {
    get {
      Uri u;
      if (Manifest == null || !Uri.TryCreate(Manifest, UriKind.Absolute, out u) || u.UserInfo.Length > 0) return null;
      if (u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && u.Host == "127.0.0.1")) return u.AbsoluteUri;
      return null;
    }
  }

  // What a player types or pastes: "ts.example.com", "203.0.113.10:9988", or a ts3server:// link. Null when it's fine,
  // otherwise what's wrong (shown under the input line).
  public static string AddressProblem(string text) {
    string host; int port;
    return Split(text, out host, out port);
  }

  public static string Split(string text, out string host, out int port) {
    host = null; port = DefaultPort;
    string s = (text ?? "").Trim();
    if (s.StartsWith("ts3server://", StringComparison.OrdinalIgnoreCase)) {
      s = s.Substring("ts3server://".Length);
      string query = null;
      int q = s.IndexOf('?');
      if (q >= 0) { query = s.Substring(q + 1); s = s.Substring(0, q); }
      s = s.TrimEnd('/');
      if (query != null)
        foreach (var pair in query.Split('&'))
          if (pair.StartsWith("port=", StringComparison.OrdinalIgnoreCase) && s.IndexOf(':') < 0) s += ":" + pair.Substring(5);
    }
    int colon = s.LastIndexOf(':');
    if (colon >= 0) {
      if (s.IndexOf(':') != colon) return "IPv6 isn't supported: use the IPv4 address or the server's name";
      int p;
      if (!int.TryParse(s.Substring(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out p) || p < 1 || p > 65535)
        return "the port after ':' must be a number from 1 to 65535";
      port = p;
      s = s.Substring(0, colon);
    }
    string problem = HostProblem(s);
    if (problem != null) return problem;
    host = s;
    return null;
  }

  public static string HostProblem(string host) {
    if (string.IsNullOrEmpty(host)) return "type the server's address";
    if (host.Length > 253) return "that address is too long";
    foreach (char c in host)
      if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '-'))
        return "letters, digits, '.' and '-' only (no http://, spaces or paths)";
    if (host.StartsWith(".") || host.StartsWith("-") || host.EndsWith("-") || host.Contains("..")) return "that isn't a server address";
    return null;
  }
}

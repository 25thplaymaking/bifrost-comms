// The radio link between Arma Reforger and TeamSpeak. The Coalition VON mod (in the game) and the Coalition plugin (in
// TeamSpeak) talk through small files in the game's profile folder, Documents\My Games\ArmaReforger\profile (the plugin
// hard-codes that path): VONPluginInfo.json (the plugin, every 5 s while TeamSpeak runs), VONServerData.json (both: in
// game or not, the TeamSpeak server and VON channel the game server named), VONHeartbeat.json (the game, every second
// in game). Bifrost Comms only reads them, to show whether each end is alive.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

class PluginInfo { public string Version, Features; public long Time; }

class ServerData {
  public bool InGame;
  public string TsServerIp, ChannelName, InGameName, PluginVersion;
  public long ClientId;
}

enum Link { Off, Split, NoTeamSpeak, NoPlugin, Standby, Joining, Linked, NoHeartbeat, OffServer }

class LinkState {
  public Link Link;
  public string Text, Detail;
  public Tone Tone;
}

class Bridge {
  public readonly string Dir;
  public Bridge(string dir) { Dir = dir; }

  public PluginInfo Plugin() {
    var d = Read("VONPluginInfo.json");
    if (d == null) return null;
    return new PluginInfo { Version = Json.Str(d, "Version"), Features = Json.Str(d, "Features"), Time = Json.Long(d, "Time") };
  }

  public ServerData Server() {
    var d = Read("VONServerData.json");
    object inner;
    if (d == null || !d.TryGetValue("ServerData", out inner) || !(inner is Dictionary<string, object>)) return null;
    var s = (Dictionary<string, object>)inner;
    return new ServerData {
      InGame = Json.Bool(s, "InGame"), TsServerIp = Json.Str(s, "TSServerIp"), ChannelName = Json.Str(s, "VONChannelName"),
      InGameName = Json.Str(s, "InGameName"), ClientId = Json.Long(s, "TSClientID"), PluginVersion = Json.Str(s, "TSPluginVersion"),
    };
  }

  // Seconds since the game last wrote its heartbeat; -1 when it never has.
  public double HeartbeatAge(DateTime nowUtc) {
    string f = Path.Combine(Dir, "VONHeartbeat.json");
    try { return File.Exists(f) ? Math.Max(0, (nowUtc - File.GetLastWriteTimeUtc(f)).TotalSeconds) : -1; } catch (IOException) { return -1; }
  }

  // The plugin rewrites its info every 5 s, so anything older than 15 s means TeamSpeak (or the plugin) isn't running.
  public static bool Fresh(PluginInfo p, DateTime nowUtc) {
    if (p == null || p.Time <= 0) return false;
    double age = (nowUtc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(p.Time)).TotalSeconds;
    return age <= 15 && age >= -60;
  }

  // The game and the plugin write these files while we read them: share everything, and a half-written file is just
  // "no answer this time".
  Dictionary<string, object> Read(string name) {
    string f = Path.Combine(Dir, name);
    try {
      if (!File.Exists(f)) return null;
      using (var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
      using (var r = new StreamReader(s, Encoding.UTF8)) {
        string text = r.ReadToEnd();
        return text.Trim().Length == 0 ? null : Json.Parse(text);
      }
    } catch (Exception) { return null; }
  }

  // One line for the status rail from everything we can see.
  //   heartbeatAge: the game beats every second while it's on a radio server, spawned or not (-1: never)
  //   channel: the channel TeamSpeak says we're in on the unit's server (ClientQuery), null when unknown
  //   offUnit: ClientQuery answered and TeamSpeak isn't on the unit's server at all
  //   gameProfile: the folder the game keeps its radio files in when it isn't the plugin's (-profile), null when it is
  public static LinkState Derive(bool gameRunning, bool tsRunning, bool pluginFresh, ServerData sd, double heartbeatAge, string channel, bool offUnit = false, string gameProfile = null) {
    if (!gameRunning) return new LinkState { Link = Link.Off, Text = "IDLE", Detail = "Reforger isn't running", Tone = Tone.Dim };
    // Nothing the plugin reads can come from the game then, so this goes first: RADIO CHECK links the two folders.
    if (gameProfile != null) return new LinkState { Link = Link.Split, Text = "DOWN", Detail = "Reforger keeps its radio files in " + gameProfile + ", not where the plugin looks", Tone = Tone.Bad };
    if (!tsRunning) return new LinkState { Link = Link.NoTeamSpeak, Text = "DOWN", Detail = "TeamSpeak isn't running", Tone = Tone.Bad };
    if (!pluginFresh) return new LinkState { Link = Link.NoPlugin, Text = "DOWN", Detail = "the Coalition plugin isn't loaded in TeamSpeak", Tone = Tone.Bad };
    if (sd == null || !sd.InGame) {
      if (heartbeatAge >= 0 && heartbeatAge <= 5) return new LinkState { Link = Link.Standby, Text = "STANDBY", Detail = "on a radio server: the radio links when you spawn", Tone = Tone.Accent };
      return new LinkState { Link = Link.Standby, Text = "STANDBY", Detail = "waiting for you to join a Coalition radio server", Tone = Tone.Accent };
    }
    if (heartbeatAge > 5) return new LinkState { Link = Link.NoHeartbeat, Text = "NO SIGNAL", Detail = "the game stopped its heartbeat (frozen or crashed?)", Tone = Tone.Warn };
    if (offUnit) return new LinkState { Link = Link.OffServer, Text = "NO TS SERVER", Detail = "TeamSpeak isn't on your unit's server: the plugin asks to connect", Tone = Tone.Warn };
    // The plugin writes its TeamSpeak client id a moment after the game says it's in; still 0 means it can't see a
    // connection on TeamSpeak's selected tab (the game shows "Teamspeak Plugin Not Detected").
    if (sd.ClientId == 0) return new LinkState { Link = Link.Joining, Text = "JOINING", Detail = "waiting for the plugin to see your TeamSpeak connection", Tone = Tone.Warn };
    if (channel != null && !string.IsNullOrEmpty(sd.ChannelName) && TeamSpeak.ChannelName(channel) == sd.ChannelName)
      return new LinkState { Link = Link.Linked, Text = "LINKED", Detail = "in game, in " + sd.ChannelName, Tone = Tone.Ok };
    if (channel == null) return new LinkState { Link = Link.Linked, Text = "LINKED", Detail = "in game, radio running", Tone = Tone.Ok };
    return new LinkState { Link = Link.Joining, Text = "JOINING", Detail = "moving you to " + (sd.ChannelName ?? "the radio channel"), Tone = Tone.Warn };
  }
}

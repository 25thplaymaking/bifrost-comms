// Bifrost Comms: sets a player's PC up for their unit's TeamSpeak 3 server and the Coalition VON radio, and starts Arma
// Reforger with the radio link up. Units share BifrostComms.exe with a comms.json beside it naming their server.
//   BifrostComms.exe            the console: set up / repair, status, launch
//   BifrostComms.exe --launch   what the "Reforger + Radio" shortcut runs: TeamSpeak and the plugin, then the game
//   BifrostComms.exe --setup    runs SET UP straight away
//   --no-game                   never starts Steam (testing)
using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

static class Program {
  [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

  public static readonly string Version = ReadVersion();

  [STAThread]
  static int Main(string[] args) {
    try { SetProcessDPIAware(); } catch (Exception) { }
    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;   // GitHub and teamspeak.com need TLS 1.2
    Application.EnableVisualStyles();
    Application.SetCompatibleTextRenderingDefault(false);
    Net.UserAgent = "BifrostComms/" + Version;

    bool launch = Array.IndexOf(args, "--launch") >= 0;
    string exe = Application.ExecutablePath;
    Comms.CleanUpAfterUpdate(Path.GetDirectoryName(exe));
    if (!Instance.Claim(launch)) return 0;

    var core = new Comms(Env.Real(), exe, Version) { NoGame = Array.IndexOf(args, "--no-game") >= 0, Args = Quote(args) };
    Application.Run(new TerminalForm(core, launch, Array.IndexOf(args, "--setup") >= 0));
    Instance.Release();
    return 0;
  }

  static string Quote(string[] args) {
    var parts = new string[args.Length];
    for (int i = 0; i < args.Length; i++) parts[i] = args[i].IndexOfAny(new[] { ' ', '"' }) >= 0 ? "\"" + args[i].Replace("\"", "") + "\"" : args[i];
    return string.Join(" ", parts);
  }

  static string ReadVersion() {
    var a = Assembly.GetExecutingAssembly();
    var attrs = a.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false);
    if (attrs.Length > 0) return ((AssemblyInformationalVersionAttribute)attrs[0]).InformationalVersion;
    var v = a.GetName().Version;
    return v.Major + "." + v.Minor + "." + v.Build;
  }
}

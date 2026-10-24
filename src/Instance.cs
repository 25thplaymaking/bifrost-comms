// One Bifrost Comms at a time. A second start (the desktop shortcut while the window is open) posts LaunchMessage to the
// open window instead, which comes to the front and, for --launch, starts the launch.
using System;
using System.Runtime.InteropServices;
using System.Threading;

static class Instance {
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int RegisterWindowMessage(string name);
  [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);

  public static int LaunchMessage;
  static Mutex mutex;

  // True when this is the only copy. Otherwise the open one has been told (launch: start the launch too).
  public static bool Claim(bool launch) {
    try { LaunchMessage = RegisterWindowMessage("BifrostComms.Launch"); } catch (Exception) { }
    bool first;
    mutex = new Mutex(true, @"Local\BifrostComms", out first);
    if (first) return true;
    try { AllowSetForegroundWindow(-1); PostMessage((IntPtr)0xffff, LaunchMessage, (IntPtr)(launch ? 1 : 0), IntPtr.Zero); } catch (Exception) { }
    return false;
  }

  // Before handing over to another copy (an update, the installed copy), so it doesn't find this one still holding on.
  public static void Release() {
    var m = mutex;
    mutex = null;
    if (m == null) return;
    try { m.ReleaseMutex(); } catch (Exception) { }
    m.Dispose();
  }
}

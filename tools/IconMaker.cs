// Draws Bifrost Comms' icon for the build: <dir>\comms.ico for the program, plus a 256 px and a 1024 px .png to look at
// (Badge in src/Look.cs).
using System.Drawing.Imaging;
using System.IO;

static class IconMaker {
  static int Main(string[] args) {
    string dir = args.Length > 0 ? args[0] : "icons";
    Directory.CreateDirectory(dir);
    Badge.WriteIco(Path.Combine(dir, "comms.ico"));
    using (var bmp = Badge.Render(256)) bmp.Save(Path.Combine(dir, "comms.png"), ImageFormat.Png);
    using (var bmp = Badge.Render(1024)) bmp.Save(Path.Combine(dir, "comms-1024.png"), ImageFormat.Png);
    return 0;
  }
}

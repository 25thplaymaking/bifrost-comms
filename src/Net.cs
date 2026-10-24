// Downloads for Bifrost Comms. Every file is checked against the SHA-256 the manifest gives before it's used; a download
// that doesn't match is deleted. The unit's manifest is never fetched through a redirect (a bounce elsewhere is refused),
// while downloads follow them (GitHub hands release files out from another host), which the hash makes safe.
using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;

// got, total (0 = unknown), bytes per second
delegate void ProgressFn(long got, long total, double rate);

class DownloadError : Exception { public DownloadError(string message) : base(message) { } }

static class Net {
  public static string UserAgent = "BifrostComms";   // Program sets "BifrostComms/<version>"

  public static string GetText(string url, int timeoutMs, bool followRedirects) {
    var req = Request(url, timeoutMs, followRedirects);
    try {
      using (var resp = (HttpWebResponse)req.GetResponse()) {
        if ((int)resp.StatusCode != 200) throw new DownloadError(url + " answered " + (int)resp.StatusCode);
        using (var s = resp.GetResponseStream()) using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
      }
    } catch (WebException e) { throw new DownloadError(Describe(url, e)); }
  }

  // Downloads url to dest, checking size and SHA-256. A file already at dest with the right hash is kept (no download).
  // Returns false when that cached copy was used. Cancel stops it (the partial file is removed).
  public static bool Download(string url, string dest, string sha256, long size, bool followRedirects, ProgressFn progress, Func<bool> cancel) {
    if (!Manifest.Sha(sha256)) throw new DownloadError("no SHA-256 for " + Path.GetFileName(dest) + ": refusing to download it");
    if (File.Exists(dest) && (size <= 0 || new FileInfo(dest).Length == size) && Hash(dest) == sha256.ToLowerInvariant()) return false;
    Directory.CreateDirectory(Path.GetDirectoryName(dest));
    string part = dest + ".part";
    try {
      var req = Request(url, 30000, followRedirects);
      using (var resp = (HttpWebResponse)req.GetResponse()) {
        if ((int)resp.StatusCode != 200) throw new DownloadError(url + " answered " + (int)resp.StatusCode);
        long total = resp.ContentLength > 0 ? resp.ContentLength : size;
        if (size > 0 && resp.ContentLength > 0 && resp.ContentLength != size)
          throw new DownloadError(Path.GetFileName(dest) + " is " + resp.ContentLength + " bytes, expected " + size);
        using (var sha = SHA256.Create())
        using (var input = resp.GetResponseStream())
        using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None)) {
          var buf = new byte[81920];
          long got = 0, mark = 0;
          var clock = System.Diagnostics.Stopwatch.StartNew();
          long lastTick = 0;
          int n;
          while ((n = input.Read(buf, 0, buf.Length)) > 0) {
            if (cancel != null && cancel()) throw new OperationCanceledException();
            output.Write(buf, 0, n);
            sha.TransformBlock(buf, 0, n, null, 0);
            got += n;
            if (size > 0 && got > size) throw new DownloadError(Path.GetFileName(dest) + " is bigger than expected");
            long ms = clock.ElapsedMilliseconds;
            if (progress != null && ms - lastTick >= 100) {
              double rate = (got - mark) * 1000.0 / Math.Max(1, ms - lastTick);
              progress(got, total, rate);
              lastTick = ms; mark = got;
            }
          }
          sha.TransformFinalBlock(buf, 0, 0);
          if (progress != null) progress(got, total, 0);
          string hex = Hex(sha.Hash);
          if (size > 0 && got != size) throw new DownloadError(Path.GetFileName(dest) + " stopped at " + got + " of " + size + " bytes");
          if (hex != sha256.ToLowerInvariant()) throw new DownloadError(Path.GetFileName(dest) + " failed its SHA-256 check (got " + hex.Substring(0, 12) + "...): not used");
        }
      }
      if (File.Exists(dest)) File.Delete(dest);
      File.Move(part, dest);
      return true;
    } catch (WebException e) {
      throw new DownloadError(Describe(url, e));
    } finally {
      if (File.Exists(part)) try { File.Delete(part); } catch (IOException) { }
    }
  }

  static HttpWebRequest Request(string url, int timeoutMs, bool followRedirects) {
    var req = (HttpWebRequest)WebRequest.Create(url);
    req.UserAgent = UserAgent;
    req.AllowAutoRedirect = followRedirects;
    req.Timeout = timeoutMs;
    req.ReadWriteTimeout = Math.Max(timeoutMs, 30000);
    req.AutomaticDecompression = DecompressionMethods.None;
    return req;
  }

  static string Describe(string url, WebException e) {
    var r = e.Response as HttpWebResponse;
    string host = url;
    try { host = new Uri(url).Host; } catch (UriFormatException) { }
    if (r != null) {
      int code = (int)r.StatusCode;
      if (code >= 300 && code < 400) return host + " redirected the request (refused)";
      return host + " answered " + code;
    }
    if (e.Status == WebExceptionStatus.Timeout) return host + " timed out";
    if (e.Status == WebExceptionStatus.NameResolutionFailure) return host + " not found (DNS)";
    return host + ": " + e.Message;
  }

  public static string Hash(string file) {
    using (var sha = SHA256.Create()) using (var s = File.OpenRead(file)) return Hex(sha.ComputeHash(s));
  }

  public static string Hash(byte[] data) { using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(data)); }

  public static string Hex(byte[] b) {
    var sb = new StringBuilder(b.Length * 2);
    foreach (var x in b) sb.Append(x.ToString("x2"));
    return sb.ToString();
  }

  // "68.9 MB", "512 KB"
  public static string Size(long bytes) {
    if (bytes >= 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024 * 1024)).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " GB";
    if (bytes >= 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB";
    if (bytes >= 1024) return (bytes / 1024.0).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " KB";
    return bytes + " B";
  }
}

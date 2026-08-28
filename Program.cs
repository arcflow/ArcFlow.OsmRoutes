using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Itinero;
using Itinero.IO.Osm;
using Itinero.Profiles;
using Vehicle = Itinero.Osm.Vehicles.Vehicle;

namespace ArcFlow.OsmRoutes
{
  /// <summary>
  /// Standalone builder: download the latest Geofabrik OSM .pbf and build a pedestrian
  /// Itinero RouterDb. Does exactly one thing — no DB, no per-building processing, no maps.
  ///
  /// Ports two pieces from the net48 ArcFlow.Tool.RouteCache: the conditional/progress
  /// download (PbfDownloader) and LoadOsmData -> optional AddContracted -> Serialize
  /// (ItineroRouteProvider.LoadOrBuildRouterDb), with throttled Itinero/OsmSharp progress.
  ///
  /// OSM data (c) OpenStreetMap contributors, ODbL 1.0. The produced .routerdb is a
  /// Derivative Database and inherits ODbL share-alike + attribution.
  /// </summary>
  internal static class Program
  {
    private const string DefaultPbfUrl =
      "https://download.geofabrik.de/europe/switzerland-latest.osm.pbf";
    // Fallback mirror: Geofabrik's bulk download server intermittently 502s. openstreetmap.fr
    // serves an equivalent Switzerland extract (a different daily cut, so its content does NOT
    // match Geofabrik's published .md5 — integrity is only verified for the Geofabrik source).
    private const string MirrorPbfUrl =
      "https://download.openstreetmap.fr/extracts/europe/switzerland-latest.osm.pbf";
    private const string DefaultPbfPath = "switzerland-latest.osm.pbf";
    private const string DefaultRouterDbPath = "switzerland.pedestrian.routerdb";

    private static int Main(string[] args)
    {
      var pbfUrl = Arg(args, "--pbf-url") ?? Env("OSMROUTES_PBF_URL") ?? DefaultPbfUrl;
      var pbfPath = Arg(args, "--pbf") ?? Env("OSMROUTES_PBF") ?? DefaultPbfPath;
      var routerDbPath = Arg(args, "--routerdb") ?? Env("OSMROUTES_ROUTERDB") ?? DefaultRouterDbPath;
      var contract = ParseBool(Arg(args, "--contract") ?? Env("OSMROUTES_CONTRACT"), true);
      // Custom pedestrian profile: treat foot=private as walkable (residents' courtyards).
      // Default on; --foot-private false builds with the stock Itinero pedestrian profile.
      var footPrivate = ParseBool(Arg(args, "--foot-private") ?? Env("OSMROUTES_FOOT_PRIVATE"), true);

      Log($"ArcFlow.OsmRoutes — pedestrian RouterDb builder");
      Log($"  pbf-url   : {pbfUrl}");
      Log($"  pbf       : {pbfPath}");
      Log($"  routerdb  : {routerDbPath}");
      Log($"  contract  : {contract}");
      Log($"  profile   : {(footPrivate ? "custom (foot=private walkable)" : "stock Itinero pedestrian")}");

      try
      {
        if (!EnsurePbf(pbfUrl, pbfPath))
          return 2;

        BuildRouterDb(pbfPath, routerDbPath, contract, footPrivate);
        return 0;
      }
      catch (Exception ex)
      {
        Log($"FAILED: {ex}");
        return 1;
      }
    }

    // ---- download (ported from PbfDownloader) ---------------------------------------------

    /// <summary>
    /// Ensure a fresh local pbf. Fetches Geofabrik's published <c>.md5</c> and skips the
    /// download when the local pbf already matches it (conditional GET etiquette). Streams
    /// to a .part temp file and renames on success.
    /// </summary>
    private static bool EnsurePbf(string url, string path)
    {
      var remoteMd5 = TryGetRemoteMd5(url);

      if (File.Exists(path))
      {
        if (remoteMd5 != null && string.Equals(remoteMd5, Md5Of(path), StringComparison.OrdinalIgnoreCase))
        {
          Log($"OSM pbf present and matches remote md5 — skipping download: {path}");
          return true;
        }
        Log(remoteMd5 == null
          ? $"OSM pbf present (remote md5 unavailable, keeping local): {path}"
          : $"OSM pbf present but md5 differs — re-downloading: {path}");
        if (remoteMd5 == null) return true;
      }

      ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
      var dir = Path.GetDirectoryName(Path.GetFullPath(path));
      if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
      var tmp = path + ".part";

      // Retry the (flaky) Geofabrik bulk download with backoff, then fall back to the mirror.
      // Integrity is verified against Geofabrik's published md5 only for the Geofabrik source.
      var sources = string.Equals(url, MirrorPbfUrl, StringComparison.OrdinalIgnoreCase)
        ? new[] { url }
        : new[] { url, MirrorPbfUrl };
      var backoff = new[] { TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60) };
      const int attemptsPerSource = 3;

      for (var si = 0; si < sources.Length; si++)
      {
        var src = sources[si];
        var isPrimary = si == 0;
        for (var attempt = 1; attempt <= attemptsPerSource; attempt++)
        {
          try
          {
            Log($"Downloading OSM pbf from {src} (attempt {attempt}/{attemptsPerSource})");
            DownloadTo(src, tmp);

            if (isPrimary && remoteMd5 != null &&
                !string.Equals(remoteMd5, Md5Of(tmp), StringComparison.OrdinalIgnoreCase))
              throw new InvalidDataException("Downloaded pbf md5 does not match Geofabrik's published md5 — corrupt download.");

            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            Log(isPrimary
              ? $"Downloaded OSM pbf -> {path}"
              : $"Downloaded OSM pbf from mirror -> {path} (not verified against Geofabrik md5).");
            return true;
          }
          catch (Exception ex)
          {
            TryDeleteFile(tmp);
            var willRetry = attempt < attemptsPerSource || si < sources.Length - 1;
            Log($"pbf download failed from {src}: {ex.Message}{(willRetry ? " — retrying" : "")}");
            if (attempt < attemptsPerSource)
              System.Threading.Thread.Sleep(backoff[Math.Min(attempt - 1, backoff.Length - 1)]);
          }
        }
      }

      Log("All OSM pbf download attempts failed (Geofabrik + mirror).");
      return false;
    }

    /// <summary>One download attempt: streams url -> tmp with progress. Throws on any failure.</summary>
    private static void DownloadTo(string url, string tmp)
    {
      using var http = new HttpClient { Timeout = TimeSpan.FromHours(2) };
      http.DefaultRequestHeaders.UserAgent.ParseAdd("ArcFlow.OsmRoutes/1.0 (+https://github.com/arcflow/ArcFlow.OsmRoutes)");
      using var resp = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
      resp.EnsureSuccessStatusCode();
      var total = resp.Content.Headers.ContentLength ?? -1L;

      using var input = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
      using var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
      var buffer = new byte[1 << 20];
      long done = 0;
      var sw = Stopwatch.StartNew();
      var lastReport = 0.0;
      int read;
      while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
      {
        output.Write(buffer, 0, read);
        done += read;
        if (sw.Elapsed.TotalSeconds - lastReport >= 0.25)
        {
          ReportDownload(done, total, sw);
          lastReport = sw.Elapsed.TotalSeconds;
        }
      }
      ReportDownload(done, total, sw);
      Console.WriteLine();
    }

    private static void TryDeleteFile(string p)
    {
      try { if (File.Exists(p)) File.Delete(p); } catch { /* ignore */ }
    }

    private static string TryGetRemoteMd5(string pbfUrl)
    {
      try
      {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(1) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ArcFlow.OsmRoutes/1.0 (+https://github.com/arcflow/ArcFlow.OsmRoutes)");
        var text = http.GetStringAsync(pbfUrl + ".md5").GetAwaiter().GetResult();
        // Geofabrik .md5 format: "<hex>  <filename>"
        var hex = text.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        return hex.Length > 0 ? hex[0] : null;
      }
      catch (Exception ex)
      {
        Log($"(could not fetch remote .md5: {ex.Message})");
        return null;
      }
    }

    private static string Md5Of(string path)
    {
      using var md5 = MD5.Create();
      using var fs = File.OpenRead(path);
      var hash = md5.ComputeHash(fs);
      return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    private static void ReportDownload(long done, long total, Stopwatch sw)
    {
      var secs = sw.Elapsed.TotalSeconds;
      var mbps = secs > 0 ? done / 1024.0 / 1024.0 / secs : 0;
      var doneMb = done / 1024.0 / 1024.0;
      if (total > 0)
      {
        var pct = (double) done / total;
        var totalMb = total / 1024.0 / 1024.0;
        var etaSec = mbps > 0 ? (totalMb - doneMb) / mbps : 0;
        const int width = 24;
        var filled = (int) (pct * width);
        if (filled > width) filled = width;
        var bar = new string('#', filled) + new string('-', width - filled);
        Console.Write($"\r[{bar}] {pct * 100,5:0.0}%  {doneMb,7:0.0}/{totalMb:0.0} MB  {mbps,5:0.0} MB/s  eta {(int) etaSec}s   ");
      }
      else
      {
        Console.Write($"\r{doneMb,7:0.0} MB  {mbps,5:0.0} MB/s   ");
      }
    }

    // ---- build (ported from ItineroRouteProvider.LoadOrBuildRouterDb) ---------------------

    private static void BuildRouterDb(string pbfPath, string routerDbPath, bool contract, bool footPrivate)
    {
      if (!File.Exists(pbfPath))
        throw new FileNotFoundException("OSM pbf not found", pbfPath);

      var pbfMb = new FileInfo(pbfPath).Length / 1024.0 / 1024.0;
      Log($"Building RouterDb from '{pbfPath}' ({pbfMb:0} MB, pedestrian).");

      var pedestrian = footPrivate ? LoadCustomPedestrian() : Vehicle.Pedestrian;

      _buildSw = Stopwatch.StartNew();
      WireBuildLogging();

      var routerDb = new RouterDb();
      using (var pbf = File.OpenRead(pbfPath))
        routerDb.LoadOsmData(pbf, pedestrian);
      EndHeartbeatLine();
      Log($"Graph loaded in {Human(_buildSw.Elapsed)}.");

      if (contract)
      {
        Log("Contracting (slow, one-time) ...");
        routerDb.AddContracted(pedestrian.Shortest());
        EndHeartbeatLine();
        Log($"Contracted in {Human(_buildSw.Elapsed)}.");
      }
      else
      {
        Log("Skipping contraction (--contract false — routing works uncontracted).");
      }

      var outDir = Path.GetDirectoryName(Path.GetFullPath(routerDbPath));
      if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
      Log($"Serializing to '{routerDbPath}' ...");
      using (var outStream = File.Open(routerDbPath, FileMode.Create))
        routerDb.Serialize(outStream);
      EndHeartbeatLine();

      // Write a sidecar .md5 of the produced routerdb for the CI md5-skip / integrity check.
      var routerMd5 = Md5Of(routerDbPath);
      File.WriteAllText(routerDbPath + ".md5", $"{routerMd5}  {Path.GetFileName(routerDbPath)}\n");

      var mb = new FileInfo(routerDbPath).Length / 1024.0 / 1024.0;
      Log($"RouterDb built + serialized ({mb:0} MB) in {Human(_buildSw.Elapsed)}");
      Log($"routerdb md5: {routerMd5}");
    }

    /// <summary>
    /// Loads the forked pedestrian profile (foot=private walkable) shipped next to the exe.
    /// It keeps Itinero's profile name "pedestrian", so RouterDbs built with it stay compatible
    /// with consumers that request the "pedestrian"/"shortest" profile by name.
    /// </summary>
    // NB: the `Vehicle` alias above is the STATIC factory (Itinero.Osm.Vehicles.Vehicle);
    // the profile base type is Itinero.Profiles.Vehicle, which DynamicVehicle derives from.
    private static Itinero.Profiles.Vehicle LoadCustomPedestrian()
    {
      var luaPath = Path.Combine(AppContext.BaseDirectory, "pedestrian.private.lua");
      if (!File.Exists(luaPath))
        throw new FileNotFoundException("Custom pedestrian profile not found next to the exe", luaPath);
      return DynamicVehicle.Load(File.ReadAllText(luaPath));
    }

    // ---- throttled Itinero/OsmSharp progress heartbeat ------------------------------------

    private static readonly Stopwatch _buildLogThrottle = new Stopwatch();
    private static readonly TimeSpan BuildLogInterval = TimeSpan.FromSeconds(1);
    private static bool _heartbeatDirty;
    private static Stopwatch _buildSw;

    private static void WireBuildLogging()
    {
      _buildLogThrottle.Restart();
      Itinero.Logging.Logger.LogAction = (origin, level, message, parameters) => ThrottledBuildLog("itinero", message);
      OsmSharp.Logging.Logger.LogAction = (origin, level, message, parameters) => ThrottledBuildLog("osm", message);
    }

    private static void ThrottledBuildLog(string source, string message)
    {
      if (_buildLogThrottle.IsRunning && _buildLogThrottle.Elapsed < BuildLogInterval) return;
      _buildLogThrottle.Restart();
      var elapsed = _buildSw != null ? Human(_buildSw.Elapsed).PadLeft(9) : new string(' ', 9);
      Console.Write($"\r{elapsed}  [{source}] {message}".PadRight(78).Substring(0, 78));
      _heartbeatDirty = true;
    }

    private static void EndHeartbeatLine()
    {
      if (!_heartbeatDirty) return;
      Console.WriteLine();
      _heartbeatDirty = false;
    }

    // ---- helpers --------------------------------------------------------------------------

    private static void Log(string msg) => Console.WriteLine(msg);

    private static string Human(TimeSpan t)
    {
      var h = (int) t.TotalHours;
      if (h > 0) return $"{h}h {t.Minutes}m {t.Seconds}s";
      if (t.Minutes > 0) return $"{t.Minutes}m {t.Seconds}s";
      return $"{t.Seconds}s";
    }

    private static string Arg(string[] args, string name)
    {
      for (var i = 0; i < args.Length; i++)
      {
        if (!string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) continue;
        return i + 1 < args.Length ? args[i + 1] : "";
      }
      return null;
    }

    private static string Env(string name)
    {
      var v = Environment.GetEnvironmentVariable(name);
      return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private static bool ParseBool(string s, bool fallback)
    {
      if (string.IsNullOrWhiteSpace(s)) return fallback;
      s = s.Trim().ToLowerInvariant();
      return s == "1" || s == "true" || s == "yes" || s == "on";
    }
  }
}

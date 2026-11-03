using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace Cut.Core;

// checks github for a newer release and installs it over this one: downloads the setup
// exe, verifies it, runs it silently (same app id, same folder, closes cut first) and
// the installer reopens cut when it's done. portable builds just get a link.
public static class Updater
{
    private const string Repo = "NotFuture1/cut";

    public sealed record Release(string Tag, Version Version, string PageUrl, string? SetupUrl, long SetupSize, string? Sha256);

    public static Version Current
    {
        get
        {
            var v = typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    // installed copies have inno's uninstaller next to them
    public static bool CanSelfUpdate =>
        OperatingSystem.IsWindows() && File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    private static HttpClient Client()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"cut/{Current}");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public static Version? ParseTag(string tag)
    {
        var t = tag.TrimStart('v', 'V');
        var cut = t.IndexOfAny(new[] { '-', '+' });
        if (cut >= 0) t = t[..cut];
        if (!Version.TryParse(t, out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    public static async Task<Release?> CheckAsync()
    {
        if (Environment.GetEnvironmentVariable("CUT_NO_UPDATE") == "1") return null;
        try
        {
            using var c = Client();
            var json = await c.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            var ver = ParseTag(tag);
            if (ver == null || ver <= Current) return null;
            string? url = null, sha = null;
            long size = 0;
            foreach (var a in root.GetProperty("assets").EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                if (!name.StartsWith("cut-setup-", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                url = a.GetProperty("browser_download_url").GetString();
                size = a.GetProperty("size").GetInt64();
                if (a.TryGetProperty("digest", out var d) && d.GetString() is { } dg && dg.StartsWith("sha256:"))
                    sha = dg[7..];
            }
            Logger.Info($"update available: {tag}");
            return new Release(tag, ver, root.GetProperty("html_url").GetString() ?? $"https://github.com/{Repo}/releases", url, size, sha);
        }
        catch (Exception ex)
        {
            Logger.Info($"update check: {ex.Message}");
            return null;
        }
    }

    public static async Task<bool> DownloadAndRunAsync(Release r, Action<double> progress)
    {
        if (r.SetupUrl == null) return false;
        try
        {
            Directory.CreateDirectory(Paths.UpdateDir);
            var file = Path.Combine(Paths.UpdateDir, $"cut-setup-{r.Version}.exe");
            using (var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            {
                c.DefaultRequestHeaders.UserAgent.ParseAdd($"cut/{Current}");
                using var resp = await c.GetAsync(r.SetupUrl, HttpCompletionOption.ResponseHeadersRead);
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? r.SetupSize;
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var dst = File.Create(file);
                var buf = new byte[1 << 16];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n));
                    done += n;
                    if (total > 0) progress(done * 100.0 / total);
                }
            }
            var len = new FileInfo(file).Length;
            if (r.SetupSize > 0 && len != r.SetupSize) throw new IOException($"download size {len} != {r.SetupSize}");
            if (r.Sha256 != null)
            {
                await using var fs = File.OpenRead(file);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(fs)).ToLowerInvariant();
                if (hash != r.Sha256.ToLowerInvariant()) throw new IOException("download checksum mismatch");
            }
            // same app id + folder: installs over this version, closes us, reopens cut after
            Process.Start(new ProcessStartInfo(file, "/SILENT /SP- /SUPPRESSMSGBOXES /NOCANCEL /CLOSEAPPLICATIONS /NORESTART")
            {
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("update", ex);
            return false;
        }
    }
}

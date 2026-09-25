using System.Drawing;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ClaudeUsageTray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayAppContext());
    }
}

internal sealed class TrayAppContext : ApplicationContext
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private static readonly string CredentialsPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

    private readonly NotifyIcon _trayIcon;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly ToolStripMenuItem _detailItem;
    private Icon? _currentIcon;
    private bool _refreshing;
    private UsageSnapshot? _lastGood;
    private DateTime _lastGoodAt;
    private int _consecutiveFailures;

    private const int BaseIntervalMs = 5 * 60_000; // normal poll cadence

    public TrayAppContext()
    {
        _detailItem = new ToolStripMenuItem("Loading…") { Enabled = false };
        var menu = new ContextMenuStrip();
        menu.Items.Add(_detailItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Refresh now", null, async (_, _) => await RefreshAsync());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _trayIcon = new NotifyIcon
        {
            Icon = MakeIcon(Color.Gray),
            Text = "Claude usage: loading…",
            ContextMenuStrip = menu,
            Visible = true,
        };

        _timer = new System.Windows.Forms.Timer { Interval = BaseIntervalMs };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();

        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var usage = await FetchUsageAsync();
            _lastGood = usage;
            _lastGoodAt = DateTime.Now;
            _consecutiveFailures = 0;
            _timer.Interval = BaseIntervalMs;
            ShowSnapshot(usage, stale: false);
        }
        catch (RateLimitedException rl)
        {
            // Back off, but keep showing the last known value — don't go gray over a 429.
            _consecutiveFailures++;
            _timer.Interval = (int)Math.Min(
                rl.RetryAfter?.TotalMilliseconds ?? BaseIntervalMs * Math.Pow(2, _consecutiveFailures),
                30 * 60_000);
            ShowLastGoodOrGray("rate limited, backing off");
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            _timer.Interval = (int)Math.Min(BaseIntervalMs * Math.Pow(2, _consecutiveFailures), 30 * 60_000);
            ShowLastGoodOrGray(Truncate(ex.Message, 60));
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void ShowSnapshot(UsageSnapshot usage, bool stale)
    {
        var pct = usage.FiveHourPct;
        SetIcon(ColorForPct(pct));
        var resets = usage.FiveHourResetsAt is { } r ? $" · resets {r.ToLocalTime():h:mm tt}" : "";
        var staleNote = stale ? $" (as of {_lastGoodAt:h:mm tt})" : "";
        SetTooltip($"Claude 5h limit: {pct:0.#}% used{resets}{staleNote}");
        _detailItem.Text = $"5h: {pct:0.#}%{resets}   |   7d: {usage.SevenDayPct:0.#}%{staleNote}";
    }

    private void ShowLastGoodOrGray(string reason)
    {
        if (_lastGood is { } usage)
        {
            ShowSnapshot(usage, stale: true);
        }
        else
        {
            SetIcon(Color.Gray);
            SetTooltip($"Claude usage: {Truncate(reason, 100)}");
            _detailItem.Text = Truncate(reason, 80);
        }
    }

    private async Task<UsageSnapshot> FetchUsageAsync()
    {
        // Re-read every poll — Claude Code rotates the token in place.
        using var credDoc = JsonDocument.Parse(await File.ReadAllTextAsync(CredentialsPath));
        var token = credDoc.RootElement.GetProperty("claudeAiOauth").GetProperty("accessToken").GetString()
                    ?? throw new InvalidOperationException("No accessToken in credentials file");

        using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("anthropic-beta", "oauth-2025-04-20");

        using var resp = await _http.SendAsync(req);
        if ((int)resp.StatusCode == 429)
            throw new RateLimitedException(resp.Headers.RetryAfter?.Delta
                ?? (resp.Headers.RetryAfter?.Date is { } d ? d - DateTimeOffset.Now : null));
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());

        return new UsageSnapshot(
            FiveHourPct: ReadPct(doc.RootElement, "five_hour"),
            FiveHourResetsAt: ReadResetsAt(doc.RootElement, "five_hour"),
            SevenDayPct: ReadPct(doc.RootElement, "seven_day"));
    }

    private static double ReadPct(JsonElement root, string window) =>
        root.TryGetProperty(window, out var w) && w.ValueKind == JsonValueKind.Object &&
        w.TryGetProperty("utilization", out var u) && u.ValueKind == JsonValueKind.Number
            ? u.GetDouble()
            : 0;

    private static DateTimeOffset? ReadResetsAt(JsonElement root, string window) =>
        root.TryGetProperty(window, out var w) && w.ValueKind == JsonValueKind.Object &&
        w.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(r.GetString(), out var dto)
            ? dto
            : null;

    /// <summary>Smooth green → yellow → orange → red over 0–100%.</summary>
    private static Color ColorForPct(double pct)
    {
        var stops = new (double Pct, Color Color)[]
        {
            (0,   Color.FromArgb(0, 200, 83)),    // green
            (50,  Color.FromArgb(255, 214, 0)),   // yellow
            (75,  Color.FromArgb(255, 140, 0)),   // orange
            (100, Color.FromArgb(229, 28, 35)),   // red
        };
        pct = Math.Clamp(pct, 0, 100);
        for (var i = 1; i < stops.Length; i++)
        {
            if (pct > stops[i].Pct) continue;
            var (p0, c0) = stops[i - 1];
            var (p1, c1) = stops[i];
            var t = (pct - p0) / (p1 - p0);
            return Color.FromArgb(
                (int)(c0.R + (c1.R - c0.R) * t),
                (int)(c0.G + (c1.G - c0.G) * t),
                (int)(c0.B + (c1.B - c0.B) * t));
        }
        return stops[^1].Color;
    }

    private void SetIcon(Color color)
    {
        var icon = MakeIcon(color);
        _trayIcon.Icon = icon;
        _currentIcon?.Dispose();
        _currentIcon = icon;
    }

    private void SetTooltip(string text) => _trayIcon.Text = Truncate(text, 127);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static Icon MakeIcon(Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 3, 3, 26, 26);
            using var pen = new Pen(Color.FromArgb(80, 0, 0, 0), 1.5f);
            g.DrawEllipse(pen, 3, 3, 26, 26);
        }
        var hIcon = bmp.GetHicon();
        try
        {
            // Clone so we can release the GDI handle immediately.
            using var tmp = Icon.FromHandle(hIcon);
            return (Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _currentIcon?.Dispose();
        _http.Dispose();
        base.ExitThreadCore();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}

internal sealed record UsageSnapshot(double FiveHourPct, DateTimeOffset? FiveHourResetsAt, double SevenDayPct);

internal sealed class RateLimitedException(TimeSpan? retryAfter) : Exception("Rate limited (429)")
{
    public TimeSpan? RetryAfter { get; } = retryAfter is { } r && r > TimeSpan.Zero ? r : null;
}

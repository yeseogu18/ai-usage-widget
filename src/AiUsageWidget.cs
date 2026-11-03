// AI 사용량 위젯 (ai-usage-widget): ChatGPT·Claude 남은 사용량을 Windows 바탕화면에 배터리처럼 표시
// Copyright (c) 2026 yeseogu18. MIT License. OpenAI·Anthropic과 무관한 비공식 도구
// .NET Framework 4.8 / C# 5 / Microsoft WebView2 SDK 1.0.4314.35
// 데이터: 각 서비스 공식 사용량 화면이 쓰는 데이터
//   ChatGPT  chatgpt.com/backend-api/wham/usage   (화면 chatgpt.com/settings/usage)
//   Claude   claude.ai/api/organizations/{id}/usage (화면 claude.ai/settings/usage)
// 로그인: 위젯 전용 WebView2 프로필(data\webview). 로그인 정보는 이 PC 밖으로 보내지 않는다
// 로고: 저장소에 포함하지 않는다. assets\chatgpt.png, assets\claude.png가 있으면 그 그림을, 없으면 글자 배지를 그린다

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace AiUsageWidget
{
    static class App
    {
        public static readonly string Root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        public static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        public static readonly int Pid = Process.GetCurrentProcess().Id;
        public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string RunName = "AiUsageWidget";
        static readonly object logLock = new object();

        public static string DataDir { get { return Path.Combine(Root, "data"); } }
        public static string AssetDir { get { return Path.Combine(Root, "assets"); } }
        public static string WebViewDir { get { return Path.Combine(DataDir, "webview"); } }
        public static string SettingsPath { get { return Path.Combine(DataDir, "settings.json"); } }
        public static string StatePath { get { return Path.Combine(DataDir, "state.json"); } }
        public static string LogPath { get { return Path.Combine(DataDir, "widget.log"); } }
        public static string CmdPath { get { return Path.Combine(DataDir, "cmd.txt"); } }
        public static string CmdResultPath { get { return Path.Combine(DataDir, "cmd-result.txt"); } }
        public static string DebugFlagPath { get { return Path.Combine(DataDir, "debug.on"); } }
        public static string PreviewPath { get { return Path.Combine(DataDir, "preview.png"); } }

        public static void Log(string msg)
        {
            try
            {
                lock (logLock)
                {
                    Directory.CreateDirectory(DataDir);
                    FileInfo fi = new FileInfo(LogPath);
                    if (fi.Exists && fi.Length > 512 * 1024)
                    {
                        string old = LogPath + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(LogPath, old);
                    }
                    File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + msg + Environment.NewLine, Utf8);
                }
            }
            catch { }
        }

        public static string NowIso() { return DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture); }

        public static DateTime? ParseIso(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            DateTimeOffset dto;
            if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out dto)) return dto.LocalDateTime;
            return null;
        }

        public static bool IsAutoRun()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    return k != null && k.GetValue(RunName) != null;
                }
            }
            catch { return false; }
        }

        public static void SetAutoRun(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue(RunName, false);
                }
            }
            catch (Exception ex) { Log("autorun: " + ex.Message); }
        }

        public static void OpenUrl(string url)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(url);
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch (Exception ex) { Log("open url: " + ex.Message); }
        }
    }

    static class Json
    {
        public static readonly JavaScriptSerializer Ser = Create();

        static JavaScriptSerializer Create()
        {
            JavaScriptSerializer s = new JavaScriptSerializer();
            s.MaxJsonLength = int.MaxValue;
            return s;
        }

        public static Dictionary<string, object> Obj(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            try { return Ser.DeserializeObject(raw) as Dictionary<string, object>; }
            catch { return null; }
        }

        public static bool Bool(Dictionary<string, object> d, string k)
        {
            object v;
            return d != null && d.TryGetValue(k, out v) && v is bool && (bool)v;
        }

        public static string Str(Dictionary<string, object> d, string k)
        {
            object v;
            if (d != null && d.TryGetValue(k, out v) && v != null) return Convert.ToString(v, CultureInfo.InvariantCulture);
            return null;
        }

        public static double Num(Dictionary<string, object> d, string k)
        {
            object v;
            if (d != null && d.TryGetValue(k, out v) && v != null)
            {
                try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); }
                catch { }
            }
            return 0;
        }
    }

    public class Settings
    {
        public int X { get; set; }
        public int Y { get; set; }
        public bool HasPos { get; set; }
        public bool TopMost { get; set; }
        public int IntervalSec { get; set; }

        public Settings() { IntervalSec = 120; }

        public static Settings Load()
        {
            try
            {
                if (File.Exists(App.SettingsPath))
                {
                    Settings s = Json.Ser.Deserialize<Settings>(File.ReadAllText(App.SettingsPath, Encoding.UTF8));
                    if (s != null)
                    {
                        if (s.IntervalSec < 60) s.IntervalSec = 120;
                        return s;
                    }
                }
            }
            catch (Exception ex) { App.Log("settings load: " + ex.Message); }
            return new Settings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(App.DataDir);
                File.WriteAllText(App.SettingsPath, Json.Ser.Serialize(this), App.Utf8);
            }
            catch (Exception ex) { App.Log("settings save: " + ex.Message); }
        }
    }

    public class LimitInfo
    {
        public string Label { get; set; }
        public double Used { get; set; }
        public string ResetIso { get; set; }

        [ScriptIgnore]
        public DateTime? ResetLocal { get { return App.ParseIso(ResetIso); } }
    }

    public class ServiceState
    {
        public string Key { get; set; }
        public string Name { get; set; }
        public string Status { get; set; }
        public string Message { get; set; }
        public string Plan { get; set; }
        public string Extra { get; set; }
        public string UpdatedAt { get; set; }
        public string CheckedAt { get; set; }
        public List<LimitInfo> Limits { get; set; }

        public ServiceState() { Status = "init"; Limits = new List<LimitInfo>(); }
        public ServiceState(string key, string name) : this() { Key = key; Name = name; }

        [ScriptIgnore]
        public bool HasData { get { return Limits != null && Limits.Count > 0; } }

        [ScriptIgnore]
        public double Remaining
        {
            get
            {
                if (!HasData) return -1;
                double m = 100;
                foreach (LimitInfo l in Limits) m = Math.Min(m, 100 - l.Used);
                return Math.Max(0, Math.Min(100, m));
            }
        }

        [ScriptIgnore]
        public LimitInfo Binding
        {
            get
            {
                LimitInfo b = null;
                if (Limits == null) return null;
                foreach (LimitInfo l in Limits) if (b == null || l.Used > b.Used) b = l;
                return b;
            }
        }

        [ScriptIgnore]
        public DateTime? UpdatedLocal { get { return App.ParseIso(UpdatedAt); } }
    }

    public class StateFile
    {
        public string SavedAt { get; set; }
        public List<ServiceState> Services { get; set; }
    }

    static class Scripts
    {
        // 결과는 JSON 문자열 하나. 로그인 토큰은 페이지 안에서만 쓰고 밖으로 내보내지 않는다.
        public const string Gpt = @"(async () => {
  const out = { ok: false };
  try {
    const s = await fetch('/api/auth/session', { credentials: 'include' });
    const ct = s.headers.get('content-type') || '';
    if (!s.ok) { out.status = s.status; out.cf = ct.indexOf('html') >= 0; return JSON.stringify(out); }
    let sj = null;
    try { sj = await s.json(); } catch (e) { out.cf = true; return JSON.stringify(out); }
    if (!sj || !sj.accessToken) { out.login = true; return JSON.stringify(out); }
    const h = { Authorization: 'Bearer ' + sj.accessToken };
    if (sj.account && sj.account.id) h['ChatGPT-Account-Id'] = sj.account.id;
    const r = await fetch('/backend-api/wham/usage', { headers: h, credentials: 'include' });
    const ct2 = r.headers.get('content-type') || '';
    if (!r.ok) { out.status = r.status; out.cf = ct2.indexOf('html') >= 0; out.login = r.status === 401; return JSON.stringify(out); }
    const j = await r.json();
    const lim = [];
    const lab = (w) => { const sec = w.limit_window_seconds || 0; if (sec >= 6 * 86400) return '주간'; if (sec >= 86400) return Math.round(sec / 86400) + '일'; if (sec >= 3600) return Math.round(sec / 3600) + '시간'; if (sec > 0) return Math.round(sec / 60) + '분'; return '한도'; };
    const add = (w) => { if (w && w.used_percent !== undefined && w.used_percent !== null) lim.push({ label: lab(w), used: Number(w.used_percent), reset: w.reset_at ? new Date(w.reset_at * 1000).toISOString() : (w.reset_after_seconds ? new Date(Date.now() + w.reset_after_seconds * 1000).toISOString() : null) }); };
    const rl = j.rate_limit || {};
    add(rl.primary_window);
    add(rl.secondary_window);
    if (rl.limit_reached && lim.length === 0) lim.push({ label: '한도', used: 100, reset: null });
    out.ok = true; out.limits = lim; out.plan = j.plan_type || '';
    const ex = [];
    if (j.credits && j.credits.balance != null) ex.push('크레딧 ' + Math.round(Number(j.credits.balance)).toLocaleString('ko-KR') + ' 남음');
    if (j.rate_limit_reset_credits && j.rate_limit_reset_credits.available_count) ex.push('한도 초기화권 ' + j.rate_limit_reset_credits.available_count + '개');
    out.extra = ex.join(' · ');
    return JSON.stringify(out);
  } catch (e) { out.error = String(e); return JSON.stringify(out); }
})()";

        public const string Claude = @"(async () => {
  const out = { ok: false };
  try {
    const r0 = await fetch('/api/organizations', { credentials: 'include' });
    const ct0 = r0.headers.get('content-type') || '';
    if (!r0.ok) { out.status = r0.status; out.cf = ct0.indexOf('html') >= 0; out.login = !out.cf && (r0.status === 401 || r0.status === 403); return JSON.stringify(out); }
    const orgs = await r0.json();
    if (!Array.isArray(orgs) || orgs.length === 0) { out.login = true; return JSON.stringify(out); }
    const org = orgs.find(o => (o.capabilities || []).some(c => /max|pro|team|enterprise|raven/.test(c))) || orgs[0];
    const r = await fetch('/api/organizations/' + org.uuid + '/usage', { credentials: 'include' });
    const ct = r.headers.get('content-type') || '';
    if (!r.ok) { out.status = r.status; out.cf = ct.indexOf('html') >= 0; out.login = !out.cf && (r.status === 401 || r.status === 403); return JSON.stringify(out); }
    const j = await r.json();
    const lim = [];
    const add = (label, used, reset) => { if (used !== null && used !== undefined && !isNaN(Number(used))) lim.push({ label: label, used: Number(used), reset: reset || null }); };
    if (Array.isArray(j.limits)) {
      for (const l of j.limits) {
        if (l.is_active === false) continue;
        const k = (l.kind || '') + ' ' + (l.group || '');
        let label = '한도';
        if (/week/.test(k)) label = '주간';
        else if (/session|five|hour/.test(k)) label = '5시간';
        else if (/day/.test(k)) label = '일간';
        if (l.scope && l.scope.model && l.scope.model.display_name) label += ' ' + l.scope.model.display_name;
        add(label, l.percent, l.resets_at);
      }
    }
    if (j.five_hour && !lim.some(x => x.label.indexOf('5시간') === 0)) add('5시간', j.five_hour.utilization, j.five_hour.resets_at);
    if (j.seven_day && !lim.some(x => x.label.indexOf('주간') === 0)) add('주간', j.seven_day.utilization, j.seven_day.resets_at);
    out.ok = true; out.limits = lim; out.plan = org.rate_limit_tier || '';
    if (j.extra_usage) out.extra = '한도 초과 추가 결제(사용 크레딧) ' + (j.extra_usage.is_enabled ? '켜짐' : '꺼짐');
    return JSON.stringify(out);
  } catch (e) { out.error = String(e); return JSON.stringify(out); }
})()";

        public const string GptCheck = @"(async () => { try { const r = await fetch('/api/auth/session', { credentials: 'include' }); const j = await r.json(); return j && j.accessToken ? 'yes' : 'no'; } catch (e) { return 'no'; } })()";

        public const string ClaudeCheck = @"(async () => { try { const r = await fetch('/api/organizations', { credentials: 'include' }); return r.ok ? 'yes' : 'no'; } catch (e) { return 'no'; } })()";
    }

    static class Cdp
    {
        public static async Task<string> EvalAsync(CoreWebView2 core, string js)
        {
            string p = "{\"expression\":" + Json.Ser.Serialize(js) + ",\"awaitPromise\":true,\"returnByValue\":true}";
            string res = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", p);
            Dictionary<string, object> d = Json.Obj(res);
            if (d == null) return null;
            object r;
            if (d.ContainsKey("exceptionDetails")) App.Log("eval exception: " + Trunc(res, 300));
            if (d.TryGetValue("result", out r))
            {
                Dictionary<string, object> rd = r as Dictionary<string, object>;
                object v;
                if (rd != null && rd.TryGetValue("value", out v))
                {
                    if (v == null) return null;
                    string sv = v as string;
                    return sv ?? Json.Ser.Serialize(v);
                }
            }
            return null;
        }

        public static string Trunc(string s, int n)
        {
            if (s == null) return "";
            return s.Length <= n ? s : s.Substring(0, n);
        }
    }

    static class Renderer
    {
        public const float BaseW = 320f, TopPad = 8f, RowH = 46f, RowGap = 2f, FooterH = 18f, BottomPad = 4f;
        static Image claudeLogo, gptLogo;
        static bool logosLoaded;

        public static Size Measure(float s, int rows)
        {
            float h = TopPad + rows * RowH + Math.Max(0, rows - 1) * RowGap + FooterH + BottomPad;
            return new Size((int)Math.Ceiling(BaseW * s), (int)Math.Ceiling(h * s));
        }

        static void LoadLogos()
        {
            if (logosLoaded) return;
            logosLoaded = true;
            claudeLogo = LoadImage(Path.Combine(App.AssetDir, "claude.png"));
            gptLogo = LoadImage(Path.Combine(App.AssetDir, "chatgpt.png"));
        }

        static Image LoadImage(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                byte[] b = File.ReadAllBytes(path);
                using (MemoryStream ms = new MemoryStream(b))
                using (Image img = Image.FromStream(ms))
                {
                    return new Bitmap(img);
                }
            }
            catch { return null; }
        }

        public static Color BatteryColor(double rem)
        {
            if (rem < 0) return Color.FromArgb(142, 142, 147);
            if (rem < 20) return Color.FromArgb(255, 69, 58);
            if (rem < 50) return Color.FromArgb(255, 176, 32);
            return Color.FromArgb(48, 209, 88);
        }

        public static string DayLabel(DateTime t, DateTime now)
        {
            if (t.Date == now.Date) return "오늘";
            if (t.Date == now.Date.AddDays(1)) return "내일";
            if (t.Date == now.Date.AddDays(-1)) return "어제";
            return t.ToString("M/d", CultureInfo.InvariantCulture) + "(" + "일월화수목금토"[(int)t.DayOfWeek] + ")";
        }

        public static string FmtReset(DateTime? r, DateTime now)
        {
            if (!r.HasValue) return "초기화 시각 미표시";
            DateTime t = r.Value;
            TimeSpan span = t - now;
            if (span.TotalMinutes < 1) return "곧 초기화";
            if (span.TotalMinutes < 60) return ((int)Math.Ceiling(span.TotalMinutes)).ToString(CultureInfo.InvariantCulture) + "분 뒤 초기화";
            if (span.TotalHours < 5) return string.Format(CultureInfo.InvariantCulture, "{0}시간 {1}분 뒤 초기화", (int)span.TotalHours, span.Minutes);
            return DayLabel(t, now) + " " + t.ToString("HH:mm", CultureInfo.InvariantCulture) + " 초기화";
        }

        static string Freshness(IList<ServiceState> svcs, DateTime now)
        {
            DateTime? latest = null;
            foreach (ServiceState sv in svcs)
            {
                DateTime? u = sv.UpdatedLocal;
                if (u.HasValue && (!latest.HasValue || u.Value > latest.Value)) latest = u;
            }
            if (!latest.HasValue) return "";
            TimeSpan ago = now - latest.Value;
            if (ago.TotalMinutes < 1) return "방금 갱신";
            if (ago.TotalMinutes < 60) return ((int)ago.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "분 전 갱신";
            return DayLabel(latest.Value, now) + " " + latest.Value.ToString("HH:mm", CultureInfo.InvariantCulture) + " 갱신";
        }

        static void SubLine(ServiceState sv, DateTime now, out string text, out Color color)
        {
            Color normal = Color.FromArgb(168, 173, 180);
            Color warn = Color.FromArgb(255, 196, 92);
            Color bad = Color.FromArgb(255, 150, 140);
            DateTime? u = sv.UpdatedLocal;
            string basis = u.HasValue ? u.Value.ToString("HH:mm", CultureInfo.InvariantCulture) + " 기준" : "";
            if (sv.Status == "login") { text = "로그인 필요 · 여기를 클릭"; color = warn; return; }
            if (sv.Status == "error") { text = "연결 안 됨 · " + (sv.HasData ? basis : (sv.Message ?? "")); color = bad; return; }
            if (sv.Status == "init") { text = sv.HasData ? "불러오는 중 · " + basis : "불러오는 중"; color = normal; return; }
            LimitInfo b = sv.Binding;
            text = b == null ? "" : b.Label + " · " + FmtReset(b.ResetLocal, now);
            color = normal;
        }

        public static GraphicsPath RoundRect(RectangleF r, float rad)
        {
            GraphicsPath p = new GraphicsPath();
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        static void DrawImageAlpha(Graphics g, Image img, RectangleF dest, int alpha)
        {
            if (alpha >= 255) { g.DrawImage(img, dest); return; }
            ColorMatrix cm = new ColorMatrix();
            cm.Matrix33 = alpha / 255f;
            using (ImageAttributes ia = new ImageAttributes())
            {
                ia.SetColorMatrix(cm);
                g.DrawImage(img, Rectangle.Round(dest), 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, ia);
            }
        }

        static void DrawBadge(Graphics g, RectangleF r, string letter, Color bg, Color fg, float s, int alpha)
        {
            using (GraphicsPath p = RoundRect(r, 6.5f * s))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(alpha, bg))) g.FillPath(b, p);
            using (Font f = new Font("Segoe UI", r.Height * 0.56f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (SolidBrush fb = new SolidBrush(Color.FromArgb(alpha, fg)))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString(letter, f, fb, new RectangleF(r.X, r.Y + r.Height * 0.03f, r.Width, r.Height), sf);
            }
        }

        static void DrawBattery(Graphics g, RectangleF r, double rem, float s, int alpha)
        {
            Color outline = Color.FromArgb((int)(alpha * 0.6), 255, 255, 255);
            using (GraphicsPath body = RoundRect(r, 4.5f * s))
            using (Pen pen = new Pen(outline, Math.Max(1.2f, 1.5f * s))) g.DrawPath(pen, body);
            RectangleF nub = new RectangleF(r.Right + 1.4f * s, r.Top + r.Height * 0.3f, 2.6f * s, r.Height * 0.4f);
            using (GraphicsPath np = RoundRect(nub, 1.2f * s))
            using (SolidBrush nb = new SolidBrush(outline)) g.FillPath(nb, np);
            if (rem < 0) return;
            float inset = 2.8f * s;
            RectangleF inner = new RectangleF(r.Left + inset, r.Top + inset, r.Width - 2 * inset, r.Height - 2 * inset);
            float fw = (float)(inner.Width * Math.Max(0, Math.Min(100, rem)) / 100.0);
            if (rem > 0 && fw < 2f * s) fw = 2f * s;
            if (fw <= 0) return;
            using (GraphicsPath fp = RoundRect(new RectangleF(inner.Left, inner.Top, fw, inner.Height), 2.2f * s))
            using (SolidBrush fb = new SolidBrush(Color.FromArgb(alpha, BatteryColor(rem)))) g.FillPath(fb, fp);
        }

        public static RectangleF[] Draw(Graphics g, float s, int W, int H, IList<ServiceState> svcs, DateTime now, bool refreshing, int hoverRow)
        {
            LoadLogos();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.Clear(Color.Transparent);

            using (GraphicsPath p = RoundRect(new RectangleF(0.5f, 0.5f, W - 1f, H - 1f), 14f * s))
            {
                using (SolidBrush bg = new SolidBrush(Color.FromArgb(228, 23, 25, 30))) g.FillPath(bg, p);
                using (Pen border = new Pen(Color.FromArgb(48, 255, 255, 255), Math.Max(1f, s))) g.DrawPath(border, p);
            }

            RectangleF[] rows = new RectangleF[svcs.Count];
            float pad = 14f * s;
            using (Font fName = new Font("Malgun Gothic", 13.5f * s, FontStyle.Bold, GraphicsUnit.Pixel))
            using (Font fSub = new Font("Malgun Gothic", 11f * s, FontStyle.Regular, GraphicsUnit.Pixel))
            using (Font fPct = new Font("Segoe UI", 17f * s, FontStyle.Bold, GraphicsUnit.Pixel))
            using (Font fCap = new Font("Malgun Gothic", 10.5f * s, FontStyle.Regular, GraphicsUnit.Pixel))
            using (StringFormat left = new StringFormat(StringFormatFlags.NoWrap))
            using (StringFormat right = new StringFormat(StringFormatFlags.NoWrap))
            {
                left.Trimming = StringTrimming.EllipsisCharacter;
                left.LineAlignment = StringAlignment.Near;
                right.Alignment = StringAlignment.Far;
                right.LineAlignment = StringAlignment.Center;

                for (int i = 0; i < svcs.Count; i++)
                {
                    ServiceState sv = svcs[i];
                    float top = (TopPad + i * (RowH + RowGap)) * s;
                    RectangleF row = new RectangleF(6f * s, top, W - 12f * s, RowH * s);
                    rows[i] = row;
                    if (i == hoverRow)
                        using (GraphicsPath hp = RoundRect(row, 9f * s))
                        using (SolidBrush hb = new SolidBrush(Color.FromArgb(20, 255, 255, 255))) g.FillPath(hb, hp);

                    bool showData = sv.HasData && sv.Status != "login";
                    int alpha = sv.Status == "ok" ? 255 : 150;
                    double rem = showData ? sv.Remaining : -1;

                    float logo = 26f * s;
                    Image img = sv.Key == "claude" ? claudeLogo : gptLogo;
                    RectangleF lr = new RectangleF(pad, top + (RowH * s - logo) / 2f, logo, logo);
                    if (img != null) DrawImageAlpha(g, img, lr, alpha);
                    else if (sv.Key == "claude") DrawBadge(g, lr, "C", Color.FromArgb(201, 106, 74), Color.White, s, alpha);
                    else DrawBadge(g, lr, "G", Color.FromArgb(233, 235, 239), Color.FromArgb(32, 34, 38), s, alpha);

                    float pctW = 48f * s, bw = 38f * s, bh = 18f * s;
                    float pctX = W - pad - pctW;
                    float bx = pctX - 8f * s - bw;
                    float by = top + (RowH * s - bh) / 2f;
                    DrawBattery(g, new RectangleF(bx, by, bw, bh), rem, s, alpha);

                    string pct = rem < 0 ? "--" : ((int)Math.Round(rem)).ToString(CultureInfo.InvariantCulture) + "%";
                    using (SolidBrush bp = new SolidBrush(Color.FromArgb(alpha, 245, 246, 248)))
                        g.DrawString(pct, fPct, bp, new RectangleF(pctX - 6f * s, top, pctW + 6f * s, RowH * s), right);

                    float tx = lr.Right + 10f * s;
                    float tw = Math.Max(10f, bx - 8f * s - tx);
                    using (SolidBrush bn = new SolidBrush(Color.FromArgb(alpha, 245, 246, 248)))
                        g.DrawString(sv.Name, fName, bn, new RectangleF(tx, top + 5f * s, tw, 20f * s), left);
                    string line;
                    Color lc;
                    SubLine(sv, now, out line, out lc);
                    using (SolidBrush bl = new SolidBrush(lc))
                        g.DrawString(line, fSub, bl, new RectangleF(tx, top + 25f * s, tw, 17f * s), left);
                }

                string cap = refreshing ? "갱신 중…" : Freshness(svcs, now);
                float cy = (TopPad + svcs.Count * RowH + Math.Max(0, svcs.Count - 1) * RowGap) * s;
                using (SolidBrush bc = new SolidBrush(Color.FromArgb(128, 134, 142)))
                using (StringFormat capF = new StringFormat(StringFormatFlags.NoWrap))
                {
                    capF.Alignment = StringAlignment.Far;
                    capF.LineAlignment = StringAlignment.Center;
                    g.DrawString(cap, fCap, bc, new RectangleF(pad, cy, W - 2 * pad, FooterH * s), capF);
                }
            }
            return rows;
        }

        public static string Tooltip(IList<ServiceState> svcs, DateTime now)
        {
            StringBuilder sb = new StringBuilder();
            foreach (ServiceState sv in svcs)
            {
                sb.Append(sv.Name);
                if (!string.IsNullOrEmpty(sv.Plan)) sb.Append(" (").Append(sv.Plan).Append(")");
                sb.AppendLine();
                if (sv.Status == "login") sb.AppendLine("  로그인 필요: 이 줄을 클릭하면 로그인 창이 열립니다");
                else if (sv.Status == "error") sb.AppendLine("  최근 갱신 실패: " + (sv.Message ?? ""));
                if (sv.Limits != null)
                    foreach (LimitInfo l in sv.Limits)
                    {
                        DateTime? r = l.ResetLocal;
                        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  {0} 한도: {1:0}% 사용, {2:0}% 남음{3}", l.Label, l.Used, Math.Max(0, 100 - l.Used),
                            r.HasValue ? " · " + DayLabel(r.Value, now) + " " + r.Value.ToString("HH:mm", CultureInfo.InvariantCulture) + " 초기화" : ""));
                    }
                if (!string.IsNullOrEmpty(sv.Extra)) sb.AppendLine("  " + sv.Extra);
                DateTime? u = sv.UpdatedLocal;
                if (u.HasValue) sb.AppendLine("  " + DayLabel(u.Value, now) + " " + u.Value.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " 기준");
            }
            sb.Append("클릭: 새로고침 · 더블클릭: 공식 사용량 화면 · 우클릭: 메뉴 · 끌어서 이동");
            return sb.ToString();
        }
    }

    class HiddenHost : Form
    {
        public HiddenHost()
        {
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-20000, -20000);
            Size = new Size(480, 360);
            Text = "AI 사용량 위젯 (데이터)";
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x80 | 0x08000000;
                return cp;
            }
        }
    }

    class LoginForm : Form
    {
        public readonly WebView2 View;
        readonly CoreWebView2Environment env;
        readonly string startUrl, host, checkJs;
        readonly bool quiet;
        System.Windows.Forms.Timer checkTimer;
        bool checking, done;
        public event EventHandler LoggedIn;

        public LoginForm(CoreWebView2Environment env, string title, string startUrl, string host, string checkJs, bool quiet)
        {
            this.env = env;
            this.startUrl = startUrl;
            this.host = host;
            this.checkJs = checkJs;
            this.quiet = quiet;
            Text = title;
            float sc = 1f;
            try { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) sc = g.DpiX / 96f; } catch { }
            Size = new Size((int)(520 * sc), (int)(780 * sc));
            if (quiet)
            {
                StartPosition = FormStartPosition.Manual;
                Location = new Point(-20000, -20000);
                ShowInTaskbar = false;
            }
            else StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            View = new WebView2();
            View.Dock = DockStyle.Fill;
            Controls.Add(View);
            Load += OnLoadAsync;
            FormClosed += delegate { if (checkTimer != null) checkTimer.Stop(); };
        }

        protected override bool ShowWithoutActivation { get { return quiet; } }

        async void OnLoadAsync(object sender, EventArgs e)
        {
            try
            {
                await View.EnsureCoreWebView2Async(env);
                View.CoreWebView2.Navigate(startUrl);
                checkTimer = new System.Windows.Forms.Timer();
                checkTimer.Interval = 2500;
                checkTimer.Tick += OnCheck;
                checkTimer.Start();
            }
            catch (Exception ex) { App.Log("login init: " + ex.Message); }
        }

        async void OnCheck(object sender, EventArgs e)
        {
            if (checking || done || IsDisposed || View.CoreWebView2 == null) return;
            checking = true;
            try
            {
                Uri u;
                if (!Uri.TryCreate(View.CoreWebView2.Source, UriKind.Absolute, out u)) return;
                if (!u.Host.EndsWith(host, StringComparison.OrdinalIgnoreCase)) return;
                string r = await Cdp.EvalAsync(View.CoreWebView2, checkJs);
                if (r == "yes")
                {
                    done = true;
                    App.Log("login ok: " + host);
                    EventHandler h = LoggedIn;
                    if (h != null) h(this, EventArgs.Empty);
                    Close();
                }
            }
            catch (Exception ex) { App.Log("login check: " + ex.Message); }
            finally { checking = false; }
        }
    }

    class WidgetForm : Form
    {
        const int WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        const int WM_NCLBUTTONDOWN = 0xA1, HTCAPTION = 2, WM_DPICHANGED = 0x02E0, WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40, SWP_NOOWNERZORDER = 0x200;
        const uint EVENT_SYSTEM_FOREGROUND = 3, WINEVENT_OUTOFCONTEXT = 0, GW_HWNDPREV = 3;

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int x; public int y; }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx; public int cy; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte BlendOp; public byte BlendFlags; public byte SourceConstantAlpha; public byte AlphaFormat; }
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int left; public int top; public int right; public int bottom; }
        delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);
        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr hObject);
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hWinEventHook);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string windowTitle);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        const string GptLanding = "https://chatgpt.com/robots.txt", GptRoot = "https://chatgpt.com/", GptUsagePage = "https://chatgpt.com/settings/usage", GptLogin = "https://chatgpt.com/auth/login";
        const string ClaudeLanding = "https://claude.ai/robots.txt", ClaudeRoot = "https://claude.ai/", ClaudeUsagePage = "https://claude.ai/settings/usage", ClaudeLogin = "https://claude.ai/login";

        readonly Settings settings;
        readonly List<ServiceState> services = new List<ServiceState>();
        readonly ServiceState gpt, claude;
        float dpiScale = 1f;
        RectangleF[] rowRects = new RectangleF[0];
        int hoverRow = -1;
        bool refreshing, cmdBusy, closing, mouseDownLeft;
        Point downPt;
        System.Windows.Forms.Timer refreshTimer, tickTimer, cmdTimer;
        ContextMenuStrip menu;
        ToolStripMenuItem miTop, miAuto;
        ToolTip tip;
        string lastTip;
        CoreWebView2Environment env;
        HiddenHost host;
        WebView2 fetchView;
        LoginForm loginForm;
        WinEventDelegate fgDelegate;
        IntPtr fgHook = IntPtr.Zero;
        IntPtr desktopHost = IntPtr.Zero;
        Bitmap lastFrame;

        public WidgetForm()
        {
            settings = Settings.Load();
            gpt = new ServiceState("chatgpt", "ChatGPT");
            claude = new ServiceState("claude", "Claude");
            services.Add(gpt);
            services.Add(claude);
            LoadLastState();
            Text = "AI 사용량 위젯";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            tip = new ToolTip();
            tip.InitialDelay = 350;
            tip.AutoPopDelay = 30000;
            tip.ReshowDelay = 150;
            tip.ShowAlways = true;
            BuildMenu();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        void BuildMenu()
        {
            menu = new ContextMenuStrip();
            menu.Font = new Font("Malgun Gothic", 9f);
            menu.Items.Add("지금 새로고침", null, delegate { Task t = RefreshAsync(); });
            menu.Items.Add("ChatGPT 사용량 화면 열기", null, delegate { App.OpenUrl(GptUsagePage); });
            menu.Items.Add("Claude 사용량 화면 열기", null, delegate { App.OpenUrl(ClaudeUsagePage); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("ChatGPT 다시 로그인", null, delegate { OpenLogin("chatgpt", false); });
            menu.Items.Add("Claude 다시 로그인", null, delegate { OpenLogin("claude", false); });
            menu.Items.Add(new ToolStripSeparator());
            miTop = new ToolStripMenuItem("항상 위에 표시");
            miTop.Click += delegate
            {
                settings.TopMost = !settings.TopMost;
                miTop.Checked = settings.TopMost;
                settings.Save();
                ApplyZOrder();
            };
            menu.Items.Add(miTop);
            miAuto = new ToolStripMenuItem("Windows 시작 시 자동 실행");
            miAuto.Click += delegate
            {
                bool on = !App.IsAutoRun();
                App.SetAutoRun(on);
                miAuto.Checked = App.IsAutoRun();
            };
            menu.Items.Add(miAuto);
            menu.Items.Add("위치 초기화", null, delegate
            {
                settings.HasPos = false;
                settings.Save();
                Location = InitialPosition(Renderer.Measure(dpiScale, services.Count));
                UpdateDpi();
                RenderNow();
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("종료", null, delegate { Close(); });
            menu.Opening += delegate
            {
                miTop.Checked = settings.TopMost;
                miAuto.Checked = App.IsAutoRun();
            };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            UpdateDpi();
        }

        void UpdateDpi()
        {
            float s = 0;
            try { s = GetDpiForWindow(Handle) / 96f; } catch { }
            if (s <= 0)
            {
                try { using (Graphics g = Graphics.FromHwnd(Handle)) s = g.DpiX / 96f; } catch { s = 1f; }
            }
            dpiScale = s <= 0 ? 1f : s;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Location = InitialPosition(Renderer.Measure(dpiScale, services.Count));
            UpdateDpi();
            RenderNow();
            fgDelegate = new WinEventDelegate(OnForeground);
            fgHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, fgDelegate, 0, 0, WINEVENT_OUTOFCONTEXT);
            refreshTimer = new System.Windows.Forms.Timer();
            refreshTimer.Interval = Math.Max(60, settings.IntervalSec) * 1000;
            refreshTimer.Tick += delegate { Task t = RefreshAsync(); };
            refreshTimer.Start();
            tickTimer = new System.Windows.Forms.Timer();
            tickTimer.Interval = 30000;
            tickTimer.Tick += delegate { RenderNow(); };
            tickTimer.Start();
            cmdTimer = new System.Windows.Forms.Timer();
            cmdTimer.Interval = 2000;
            cmdTimer.Tick += delegate { ProcessCommands(); };
            cmdTimer.Start();
            SystemEvents.PowerModeChanged += OnPowerMode;
            App.Log("start pid=" + App.Pid + " scale=" + dpiScale.ToString("0.00", CultureInfo.InvariantCulture) + " pos=" + Left + "," + Top);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ApplyZOrder();
            BeginInvoke(new Action(delegate { Task t = RefreshAsync(); }));
        }

        Point InitialPosition(Size sz)
        {
            if (settings.HasPos)
            {
                Rectangle r = new Rectangle(settings.X, settings.Y, sz.Width, sz.Height);
                foreach (Screen sc in Screen.AllScreens)
                {
                    Rectangle inter = Rectangle.Intersect(sc.WorkingArea, r);
                    if (inter.Width > 40 && inter.Height > 30) return new Point(settings.X, settings.Y);
                }
            }
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int m = (int)(24 * dpiScale);
            return new Point(wa.Right - sz.Width - m, wa.Top + m);
        }

        void RenderNow()
        {
            if (!IsHandleCreated || closing) return;
            try
            {
                Size sz = Renderer.Measure(dpiScale, services.Count);
                Bitmap bmp = new Bitmap(sz.Width, sz.Height, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(bmp))
                    rowRects = Renderer.Draw(g, dpiScale, sz.Width, sz.Height, services, DateTime.Now, refreshing, hoverRow);
                PushBitmap(bmp);
                if (lastFrame != null) lastFrame.Dispose();
                lastFrame = bmp;
                string t = Renderer.Tooltip(services, DateTime.Now);
                if (t != lastTip) { tip.SetToolTip(this, t); lastTip = t; }
            }
            catch (Exception ex) { App.Log("render: " + ex.Message); }
        }

        void PushBitmap(Bitmap bmp)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBmp = IntPtr.Zero, oldBmp = IntPtr.Zero;
            try
            {
                hBmp = bmp.GetHbitmap(Color.FromArgb(0));
                oldBmp = SelectObject(memDc, hBmp);
                SIZE size = new SIZE();
                size.cx = bmp.Width;
                size.cy = bmp.Height;
                POINT src = new POINT();
                POINT dst = new POINT();
                dst.x = Left;
                dst.y = Top;
                BLENDFUNCTION blend = new BLENDFUNCTION();
                blend.BlendOp = 0;
                blend.BlendFlags = 0;
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = 1;
                UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2);
            }
            finally
            {
                if (hBmp != IntPtr.Zero) { SelectObject(memDc, oldBmp); DeleteObject(hBmp); }
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        void SavePreview()
        {
            try
            {
                if (lastFrame == null) return;
                Directory.CreateDirectory(App.DataDir);
                lastFrame.Save(App.PreviewPath, ImageFormat.Png);
            }
            catch (Exception ex) { App.Log("preview: " + ex.Message); }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEACTIVATE) { m.Result = (IntPtr)MA_NOACTIVATE; return; }
            if (m.Msg == WM_DPICHANGED)
            {
                int dpi = (int)((long)m.WParam & 0xFFFF);
                if (dpi > 0) dpiScale = dpi / 96f;
                RECT r = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
                SetWindowPos(Handle, IntPtr.Zero, r.left, r.top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                RenderNow();
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        int RowAt(Point p)
        {
            for (int i = 0; i < rowRects.Length; i++) if (rowRects[i].Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) { mouseDownLeft = true; downPt = e.Location; }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (mouseDownLeft && (Math.Abs(e.X - downPt.X) > 4 || Math.Abs(e.Y - downPt.Y) > 4))
            {
                mouseDownLeft = false;
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
                settings.X = Left;
                settings.Y = Top;
                settings.HasPos = true;
                settings.Save();
                return;
            }
            int r = RowAt(e.Location);
            if (r != hoverRow) { hoverRow = r; RenderNow(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hoverRow != -1) { hoverRow = -1; RenderNow(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && mouseDownLeft) { mouseDownLeft = false; OnClickAt(e.Location); }
            else if (e.Button == MouseButtons.Right) ShowMenu();
            mouseDownLeft = false;
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button != MouseButtons.Left) return;
            int i = RowAt(e.Location);
            if (i < 0) return;
            App.OpenUrl(services[i].Key == "claude" ? ClaudeUsagePage : GptUsagePage);
        }

        void OnClickAt(Point p)
        {
            int i = RowAt(p);
            if (i >= 0 && i < services.Count && services[i].Status == "login") { OpenLogin(services[i].Key, false); return; }
            Task t = RefreshAsync();
        }

        void ShowMenu()
        {
            try { SetForegroundWindow(Handle); } catch { }
            menu.Show(Cursor.Position);
        }

        static string ClassOf(IntPtr h)
        {
            StringBuilder sb = new StringBuilder(256);
            GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }

        static bool IsDesktopClass(string cls) { return cls == "Progman" || cls == "WorkerW"; }

        IntPtr FindDesktopHost()
        {
            if (desktopHost != IntPtr.Zero && IsWindow(desktopHost) && FindWindowEx(desktopHost, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero) return desktopHost;
            IntPtr found = IntPtr.Zero;
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero) { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            desktopHost = found;
            return found;
        }

        void PlaceAboveDesktop()
        {
            IntPtr desk = FindDesktopHost();
            if (desk == IntPtr.Zero) return;
            IntPtr above = GetWindow(desk, GW_HWNDPREV);
            if (above == IntPtr.Zero || above == Handle) return;
            SetWindowPos(Handle, above, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }

        void ApplyZOrder()
        {
            if (!IsHandleCreated) return;
            if (settings.TopMost)
            {
                SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
                return;
            }
            IntPtr fg = GetForegroundWindow();
            if (fg != IntPtr.Zero && IsDesktopClass(ClassOf(fg))) SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            else PlaceAboveDesktop();
        }

        void OnForeground(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            try
            {
                if (settings.TopMost || closing || hwnd == IntPtr.Zero || !IsHandleCreated) return;
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                if (pid == (uint)App.Pid) return;
                string cls = ClassOf(hwnd);
                if (IsDesktopClass(cls))
                {
                    SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
                    return;
                }
                if (cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd" || cls == "NotifyIconOverflowWindow" || cls == "TopLevelWindowForOverflowXamlIsland"
                    || cls == "#32768" || cls == "ForegroundStaging" || cls == "XamlExplorerHostIslandWindow" || cls == "Windows.UI.Core.CoreWindow"
                    || cls == "MultitaskingViewFrame" || cls == "TaskSwitcherWnd") return;
                PlaceAboveDesktop();
            }
            catch { }
        }

        async Task EnsureWebViewAsync()
        {
            if (env == null)
            {
                Directory.CreateDirectory(App.WebViewDir);
                CoreWebView2EnvironmentOptions opts = new CoreWebView2EnvironmentOptions("--disable-features=CalculateNativeWinOcclusion --disable-backgrounding-occluded-windows", "ko-KR");
                env = await CoreWebView2Environment.CreateAsync(null, App.WebViewDir, opts);
            }
            if (host == null)
            {
                host = new HiddenHost();
                fetchView = new WebView2();
                fetchView.Dock = DockStyle.Fill;
                host.Controls.Add(fetchView);
                host.Show();
            }
            if (fetchView.CoreWebView2 == null)
            {
                await fetchView.EnsureCoreWebView2Async(env);
                fetchView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                fetchView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            }
        }

        async Task RefreshAsync()
        {
            if (refreshing || closing) return;
            refreshing = true;
            RenderNow();
            try
            {
                await EnsureWebViewAsync();
                await FetchServiceAsync(gpt, GptLanding, GptRoot, Scripts.Gpt);
                await FetchServiceAsync(claude, ClaudeLanding, ClaudeRoot, Scripts.Claude);
                SaveState();
            }
            catch (Exception ex)
            {
                App.Log("refresh failed: " + ex);
                foreach (ServiceState sv in services)
                {
                    if (sv.Status != "login") { sv.Status = "error"; sv.Message = "위젯 내부 오류"; }
                }
            }
            refreshing = false;
            RenderNow();
            SavePreview();
        }

        async Task FetchServiceAsync(ServiceState sv, string landing, string root, string js)
        {
            sv.CheckedAt = App.NowIso();
            Dictionary<string, object> d = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                await NavigateAsync(fetchView.CoreWebView2, attempt == 0 ? landing : root, 30000);
                await WaitPastChallengeAsync(fetchView.CoreWebView2, 20000);
                string raw = await Cdp.EvalAsync(fetchView.CoreWebView2, js);
                d = Json.Obj(raw);
                if (attempt == 0 && d != null && Json.Bool(d, "cf")) { App.Log(sv.Key + ": security check, retry via " + root); continue; }
                break;
            }
            ApplyResult(sv, d);
            App.Log(sv.Key + ": " + sv.Status + (sv.HasData ? " remaining=" + sv.Remaining.ToString("0", CultureInfo.InvariantCulture) + "%" : "") + (string.IsNullOrEmpty(sv.Message) ? "" : " (" + sv.Message + ")"));
        }

        static async Task<bool> NavigateAsync(CoreWebView2 core, string url, int timeoutMs)
        {
            TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>();
            EventHandler<CoreWebView2NavigationCompletedEventArgs> h = delegate(object s, CoreWebView2NavigationCompletedEventArgs e) { tcs.TrySetResult(e.IsSuccess); };
            core.NavigationCompleted += h;
            try
            {
                core.Navigate(url);
                Task done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
                return done == tcs.Task && tcs.Task.Result;
            }
            finally { core.NavigationCompleted -= h; }
        }

        static async Task WaitPastChallengeAsync(CoreWebView2 core, int timeoutMs)
        {
            DateTime until = DateTime.Now.AddMilliseconds(timeoutMs);
            while (DateTime.Now < until)
            {
                string t = null;
                try { t = await Cdp.EvalAsync(core, "document.title || ''"); } catch { }
                if (t == null || !Regex.IsMatch(t, "Just a moment|잠시만 기다|Attention Required|Checking your browser|보안 확인", RegexOptions.IgnoreCase)) return;
                await Task.Delay(1000);
            }
        }

        static string PrettyPlan(string p)
        {
            if (string.IsNullOrEmpty(p)) return "";
            string s = p.Replace("default_claude_", "").Replace("claude_", "").Replace("_", " ").Trim();
            if (s.Length == 0) return "";
            return char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        static void ApplyResult(ServiceState sv, Dictionary<string, object> d)
        {
            if (d == null) { sv.Status = "error"; sv.Message = "응답 없음"; return; }
            if (Json.Bool(d, "ok"))
            {
                List<LimitInfo> lims = new List<LimitInfo>();
                object lo;
                if (d.TryGetValue("limits", out lo) && lo != null && !(lo is string))
                {
                    System.Collections.IEnumerable arr = lo as System.Collections.IEnumerable;
                    if (arr != null)
                        foreach (object o in arr)
                        {
                            Dictionary<string, object> ld = o as Dictionary<string, object>;
                            if (ld == null) continue;
                            LimitInfo li = new LimitInfo();
                            li.Label = Json.Str(ld, "label") ?? "한도";
                            li.Used = Math.Max(0, Math.Min(100, Json.Num(ld, "used")));
                            li.ResetIso = Json.Str(ld, "reset");
                            lims.Add(li);
                        }
                }
                if (lims.Count == 0) { sv.Status = "error"; sv.Message = "한도 정보 없음"; return; }
                sv.Limits = lims;
                sv.Plan = PrettyPlan(Json.Str(d, "plan"));
                sv.Extra = Json.Str(d, "extra");
                sv.Status = "ok";
                sv.Message = null;
                sv.UpdatedAt = App.NowIso();
                return;
            }
            if (Json.Bool(d, "login")) { sv.Status = "login"; sv.Message = "로그인 필요"; return; }
            sv.Status = "error";
            string st = Json.Str(d, "status");
            string er = Json.Str(d, "error");
            if (Json.Bool(d, "cf")) sv.Message = "보안 확인 대기";
            else if (!string.IsNullOrEmpty(st)) sv.Message = "HTTP " + st;
            else if (!string.IsNullOrEmpty(er)) sv.Message = "연결 안 됨";
            else sv.Message = "알 수 없는 응답";
        }

        void SaveState()
        {
            try
            {
                StateFile sf = new StateFile();
                sf.SavedAt = App.NowIso();
                sf.Services = services;
                Directory.CreateDirectory(App.DataDir);
                File.WriteAllText(App.StatePath, Json.Ser.Serialize(sf), App.Utf8);
            }
            catch (Exception ex) { App.Log("state save: " + ex.Message); }
        }

        void LoadLastState()
        {
            try
            {
                if (!File.Exists(App.StatePath)) return;
                StateFile sf = Json.Ser.Deserialize<StateFile>(File.ReadAllText(App.StatePath, Encoding.UTF8));
                if (sf == null || sf.Services == null) return;
                foreach (ServiceState old in sf.Services)
                {
                    if (old == null) continue;
                    foreach (ServiceState cur in services)
                    {
                        if (cur.Key != old.Key) continue;
                        cur.Limits = old.Limits ?? new List<LimitInfo>();
                        cur.Plan = old.Plan;
                        cur.Extra = old.Extra;
                        cur.UpdatedAt = old.UpdatedAt;
                        cur.Status = "init";
                    }
                }
            }
            catch (Exception ex) { App.Log("state load: " + ex.Message); }
        }

        string StatusSummary()
        {
            StringBuilder sb = new StringBuilder();
            foreach (ServiceState sv in services)
            {
                sb.Append(sv.Key).Append('=').Append(sv.Status);
                if (sv.HasData) sb.Append('(').Append(sv.Remaining.ToString("0", CultureInfo.InvariantCulture)).Append("% left)");
                if (!string.IsNullOrEmpty(sv.Message)) sb.Append('[').Append(sv.Message).Append(']');
                sb.Append(' ');
            }
            return sb.ToString().Trim();
        }

        async void OpenLogin(string key, bool quiet)
        {
            try
            {
                await EnsureWebViewAsync();
                if (loginForm != null && !loginForm.IsDisposed) loginForm.Close();
                bool isClaude = key == "claude";
                LoginForm f = new LoginForm(env, (isClaude ? "Claude" : "ChatGPT") + " 로그인 · AI 사용량 위젯", isClaude ? ClaudeLogin : GptLogin,
                    isClaude ? "claude.ai" : "chatgpt.com", isClaude ? Scripts.ClaudeCheck : Scripts.GptCheck, quiet);
                f.LoggedIn += delegate { BeginInvoke(new Action(delegate { Task t = RefreshAsync(); })); };
                f.FormClosed += delegate
                {
                    if (loginForm == f) loginForm = null;
                    Task t2 = RefreshAsync();
                };
                loginForm = f;
                f.Show();
            }
            catch (Exception ex) { App.Log("open login: " + ex.Message); }
        }

        async Task<string> CaptureAsync(string which)
        {
            WebView2 v = null;
            if (which == "login" && loginForm != null && !loginForm.IsDisposed) v = loginForm.View;
            else if (which == "fetch") v = fetchView;
            if (v == null || v.CoreWebView2 == null) return "(no view)";
            string path = Path.Combine(App.DataDir, "capture-" + which + ".png");
            using (FileStream fs = File.Create(path))
            {
                await v.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
            }
            return path;
        }

        async void ProcessCommands()
        {
            if (cmdBusy || closing || !File.Exists(App.CmdPath)) return;
            cmdBusy = true;
            try
            {
                string[] lines;
                try
                {
                    lines = File.ReadAllLines(App.CmdPath, Encoding.UTF8);
                    File.Delete(App.CmdPath);
                }
                catch { return; }
                bool debug = File.Exists(App.DebugFlagPath);
                foreach (string line0 in lines)
                {
                    string line = line0.Trim();
                    if (line.Length == 0) continue;
                    string cmd = line, arg = "";
                    int sp = line.IndexOf(' ');
                    if (sp > 0) { cmd = line.Substring(0, sp); arg = line.Substring(sp + 1).Trim(); }
                    string result = "ok";
                    bool hasLogin = loginForm != null && !loginForm.IsDisposed && loginForm.View.CoreWebView2 != null;
                    try
                    {
                        switch (cmd.ToLowerInvariant())
                        {
                            case "refresh":
                                await RefreshAsync();
                                result = StatusSummary();
                                break;
                            case "status":
                                result = StatusSummary();
                                break;
                            case "login":
                                {
                                    string[] a = arg.Split(' ');
                                    OpenLogin(a[0], a.Length > 1 && a[1] == "quiet");
                                    break;
                                }
                            case "closelogin":
                                if (loginForm != null && !loginForm.IsDisposed) loginForm.Close();
                                break;
                            case "show":
                                if (loginForm != null && !loginForm.IsDisposed)
                                {
                                    Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                                    loginForm.Location = new Point(wa.Left + (wa.Width - loginForm.Width) / 2, wa.Top + (wa.Height - loginForm.Height) / 2);
                                    loginForm.Activate();
                                }
                                else result = "(no login window)";
                                break;
                            case "capture":
                                result = await CaptureAsync(arg);
                                break;
                            case "url":
                                result = hasLogin ? loginForm.View.CoreWebView2.Source : "(no login window)";
                                break;
                            case "eval":
                                if (!debug) { result = "debug off"; break; }
                                if (!hasLogin) { result = "(no login window)"; break; }
                                result = Cdp.Trunc(await Cdp.EvalAsync(loginForm.View.CoreWebView2, arg), 4000);
                                break;
                            case "nav":
                                if (!debug) { result = "debug off"; break; }
                                if (!hasLogin) { result = "(no login window)"; break; }
                                loginForm.View.CoreWebView2.Navigate(arg);
                                break;
                            case "render":
                                RenderNow();
                                SavePreview();
                                break;
                            case "quit":
                                Close();
                                break;
                            default:
                                result = "unknown command";
                                break;
                        }
                    }
                    catch (Exception ex) { result = "error: " + ex.Message; }
                    try { File.AppendAllText(App.CmdResultPath, DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + cmd + " -> " + (result ?? "(null)") + Environment.NewLine, App.Utf8); } catch { }
                }
            }
            finally { cmdBusy = false; }
        }

        void OnPowerMode(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode != PowerModes.Resume) return;
            try
            {
                BeginInvoke(new Action(async delegate
                {
                    await Task.Delay(15000);
                    Task t = RefreshAsync();
                }));
            }
            catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            closing = true;
            try { SystemEvents.PowerModeChanged -= OnPowerMode; } catch { }
            try { if (fgHook != IntPtr.Zero) UnhookWinEvent(fgHook); } catch { }
            try { if (loginForm != null && !loginForm.IsDisposed) loginForm.Close(); } catch { }
            try
            {
                if (fetchView != null) fetchView.Dispose();
                if (host != null) host.Close();
            }
            catch { }
            App.Log("exit");
            base.OnFormClosing(e);
        }
    }

    static class RenderTest
    {
        public static int Run(string[] args)
        {
            try
            {
                StateFile sf = Json.Ser.Deserialize<StateFile>(File.ReadAllText(args[1], Encoding.UTF8));
                float scale = args.Length >= 4 ? float.Parse(args[3], CultureInfo.InvariantCulture) : 1f;
                DateTime now = DateTime.Now;
                if (args.Length >= 5) now = DateTime.Parse(args[4], CultureInfo.InvariantCulture);
                bool refreshing = args.Length >= 6 && args[5] == "refreshing";
                Size sz = Renderer.Measure(scale, sf.Services.Count);
                using (Bitmap bmp = new Bitmap(sz.Width, sz.Height, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                        Renderer.Draw(g, scale, sz.Width, sz.Height, sf.Services, now, refreshing, -1);
                    bmp.Save(args[2], ImageFormat.Png);
                }
                return 0;
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(args[2] + ".error.txt", ex.ToString()); } catch { }
                return 1;
            }
        }
    }

    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [STAThread]
        static int Main(string[] args)
        {
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
            if (args.Length >= 3 && args[0] == "--render-test") return RenderTest.Run(args);
            if (args.Length >= 1 && (args[0] == "--quit" || args[0] == "--refresh"))
            {
                try
                {
                    Directory.CreateDirectory(App.DataDir);
                    File.WriteAllText(App.CmdPath, args[0].Substring(2), App.Utf8);
                }
                catch { }
                return 0;
            }
            bool created;
            Mutex mtx = new Mutex(true, @"Local\AiUsageWidget.v1", out created);
            if (!created)
            {
                // 이미 떠 있으면 새로 실행한 쪽이 이어받는다: 기존 위젯에 종료를 요청하고 기다린다
                try { Directory.CreateDirectory(App.DataDir); File.WriteAllText(App.CmdPath, "quit", App.Utf8); } catch { }
                bool got = false;
                try { got = mtx.WaitOne(15000); }
                catch (AbandonedMutexException) { got = true; }
                if (!got) { App.Log("takeover timeout"); return 0; }
                App.Log("takeover from previous instance");
            }
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { App.Log("thread exception: " + e.Exception); };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { App.Log("unhandled: " + e.ExceptionObject); };
                Application.Run(new WidgetForm());
            }
            finally
            {
                try { mtx.ReleaseMutex(); } catch { }
                mtx.Dispose();
            }
            return 0;
        }
    }
}

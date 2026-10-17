// YouTube Lyric Notch for Windows — 화면과 무관한 로직(macOS 판 main.swift와 같은 규칙).
// 타임라인 → 지금 곡 → 가사(LRCLIB, 벅스) → 유튜브 설명란·댓글 → 곡 판별 → 캐시.
// Windows API를 쓰지 않아 macOS에서도 돌려 점검할 수 있다(Selftest.Run).
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LyricNotch;

public record Track(double Start, string Name);

public class Lyrics
{
    public List<string> Lines { get; set; } = new();
    public List<double>? Times { get; set; }  // null: 싱크 없는 가사
    public bool Note { get; set; }             // 가사 대신 "찾는 중…" 같은 안내 한 줄
    public static Lyrics MakeNote(string s) => new() { Lines = new() { s }, Times = new() { 0 }, Note = true };
}

public class Info
{
    public string Desc { get; set; } = "";
    public List<string> Comments { get; set; } = new();
    public string Author { get; set; } = "";
    public string Category { get; set; } = "";
}

/// 영상 하나에서 알아낸 곡: 한 곡(Author가 있으면 채널 이름도 붙여 검색) / 타임라인 여러 곡 / 모름
public record Songs(string? One = null, string? Author = null, List<Track>? List = null)
{
    public bool None => One == null && List == null;
}

public static class Core
{
    // MARK: 타임라인

    static readonly Regex Stamp = new(@"(?:(\d{1,2}):)?(\d{1,2}):(\d{2})");

    /// "00:00 가수 - 곡" 꼴 줄을 모아 시간순으로. 3곡 미만이면 타임라인이 아니라고 본다(유튜브 챕터 규칙과 같다).
    public static List<Track> Timeline(string text)
    {
        var seen = new HashSet<double>();
        var output = new List<Track>();
        foreach (var line in text.Split('\n', '\r'))
        {
            var m = Stamp.Match(line);
            if (!m.Success) continue;
            double N(int i) => m.Groups[i].Success ? double.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture) : 0;
            var start = N(1) * 3600 + N(2) * 60 + N(3);
            var name = Stamp.Replace(line, "");
            name = Regex.Replace(name, @"\[\s*\]|\(\s*\)|【\s*】", "");
            // 앞뒤 장식(┃, ▶, 🎵, 번호 "1." …)을 떼고 괄호·느낌표 같은 곡 이름 끝은 남긴다
            name = Regex.Replace(name, @"^[^\p{L}\p{N}(\[【""'“‘]*(\d{1,3}[.)]\s+)?|[^\p{L}\p{N})\]】""'”’!?.]+$", "");
            if (name.Length > 0 && seen.Add(start)) output.Add(new Track(start, name));
        }
        return output.Count >= 3 ? output.OrderBy(t => t.Start).ToList() : new List<Track>();
    }

    // MARK: 가사

    static readonly Regex Tag = new(@"\[(\d+):(\d+(?:\.\d+)?)\]");

    /// LRC → 줄·시각. [offset:±ms] 보정, 같은 시각 여러 줄(번역·화음)은 " / "로 합치고, 단어 타이밍 <mm:ss.xx>은 뗀다.
    public static Lyrics ParseLRC(string s)
    {
        var pairs = new List<(double t, string x, int i)>();
        double offset = 0;
        foreach (var line in s.Split('\n'))
        {
            var o = Regex.Match(line, @"^\s*\[offset:\s*([+-]?\d+)\]", RegexOptions.IgnoreCase);
            if (o.Success) { offset = double.Parse(o.Groups[1].Value, CultureInfo.InvariantCulture) / 1000; continue; }
            var ms = Tag.Matches(line);
            if (ms.Count == 0) continue;
            var last = ms[ms.Count - 1];
            var text = Regex.Replace(line.Substring(last.Index + last.Length), @"<\d+:\d+(?:\.\d+)?>", "").Trim();
            foreach (Match m in ms)
                pairs.Add((double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 60
                           + double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), text, pairs.Count));
        }
        var lines = new List<string>();
        var times = new List<double>();
        foreach (var (t, x, _) in pairs.OrderBy(p => p.t).ThenBy(p => p.i))
        {
            if (times.Count > 0 && times[^1] == t - offset)
            {
                if (x.Length > 0 && !lines[^1].Contains(x)) lines[^1] += lines[^1].Length == 0 ? x : " / " + x;
            }
            else { times.Add(t - offset); lines.Add(x); }
        }
        return new Lyrics { Lines = lines, Times = times };
    }

    /// 벅스 싱크 가사 "11.0|첫 줄＃23.0|둘째 줄＃…"
    public static Lyrics ParseBugs(string s)
    {
        var pairs = new List<(double, string)>();
        foreach (var e in s.Split('＃'))
        {
            var p = e.Split('|', 2);
            if (p.Length == 2 && double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var t)) pairs.Add((t, p[1].Trim()));
        }
        return new Lyrics { Lines = pairs.Select(p => p.Item2).ToList(), Times = pairs.Select(p => p.Item1).ToList() };
    }

    public static Lyrics PlainLines(string s) =>
        new() { Lines = s.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList() };

    /// 지금 부르는 줄 번호. -1이면 첫 줄 전. 싱크 없는 가사는 곡 진행률로 어림(간주가 길면 어긋남).
    public static int LineIndex(Lyrics l, double t, double length)
    {
        if (l.Times == null) return Math.Min(l.Lines.Count - 1, (int)(t / Math.Max(length, 1) * l.Lines.Count));
        return l.Times.FindLastIndex(x => x <= t);
    }

    static string StripDiacritics(string s)
    {
        // NFD로 풀어 결합 부호만 빼고 다시 NFC로(한글 음절은 그대로 돌아온다)
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in d) if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// 검색어 후보(악센트는 풀어서: México → Mexico): 괄호 속을 뺀 것 → "가수 - 제목"이면 첫 가수 + 제목,
    /// 괄호 속 다른 이름 + 제목 → 괄호만 푼 것 → (단일 곡 영상이면) 채널 이름을 붙인 것.
    /// hint(플리의 주인공 가수)가 있으면 "가수 + 곡 제목"을 맨 앞에: 곡 제목만 적은 플리에서 같은 제목의 다른 가수 곡을 피한다.
    public static List<string> Queries(string name, string? author = null, string? hint = null)
    {
        name = StripDiacritics(name);
        static string Tidy(string s)
        {
            s = Regex.Replace(s, @"(?i)\b(official|music video|lyrics?|audio|visualizer|m/?v)\b|가사|뮤직비디오|뮤비", " ");
            s = Regex.Replace(s, @"[-–—_|/:~""“”()\[\]【】「」,&]", " ");
            return string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }
        static string Strip(string s) => Regex.Replace(s, @"\([^)]*\)|\[[^\]]*\]|【[^】]*】", " ");
        var qs = new List<string> { Tidy(Strip(name)) };
        if (hint != null) qs.Insert(0, Tidy(hint + " " + Strip(name)));
        var r = Regex.Match(name, @"\s+[-–—]\s+|_");
        if (r.Success)
        {
            var artist = name[..r.Index];
            var title = Tidy(Strip(name[(r.Index + r.Length)..]));
            qs.Add(Tidy(Strip(artist).Split(',', '&')[0]) + " " + title);
            // "곡 - 가수" 순서일 수도 있어서 반대로도("하지마 - 릴러말즈, TOIL" → "릴러말즈 하지마")
            qs.Add(Tidy(Strip(name[(r.Index + r.Length)..]).Split(',', '&')[0]) + " " + Tidy(Strip(artist)));
            foreach (Match m in Regex.Matches(artist, @"\(([^)]*)\)")) qs.Add(Tidy(m.Groups[1].Value) + " " + title);
        }
        qs.Add(Tidy(name));
        if (author != null) qs.Add(Tidy(author.Replace(" - Topic", "") + " " + Strip(name)));
        return qs.Where(q => q.Length > 0).Distinct().ToList();
    }

    /// 글자·숫자만 소문자로("&amp;" 같은 HTML 문자 참조는 버림)
    public static string Squash(string s) =>
        new(Regex.Replace(StripDiacritics(s).ToLowerInvariant(), @"&#?\w+;", "").Where(char.IsLetterOrDigit).ToArray());

    /// 검색 결과의 곡 제목(또는 가수)이 우리 곡 이름 안에 있나. 괄호 속 다른 언어 제목도 인정, (Feat.…) (Prod.…)는 안 본다.
    public static bool SameSong(string name, string title)
    {
        var n = Squash(name);
        var inner = Regex.Matches(title, @"\(([^)]*)\)").Select(m => m.Groups[1].Value)
            .Where(x => !Regex.IsMatch(x, @"(?i)^\s*(feat|ft|prod|with)\b"));
        var outer = Regex.Replace(title, @"\([^)]*\)", "");
        return new[] { outer }.Concat(inner).Select(Squash).Any(p => p.Length > 0 && n.Contains(p));
    }

    /// 검색 결과의 가수(여러 명·괄호 속 다른 이름 중 하나라도)가 곡 이름·영상 제목·채널 어딘가에 나오나
    public static bool ArtistIn(string artist, string context)
    {
        var c = Squash(context);
        var s = Regex.Replace(artist, @"(?i)\s+(feat\.?|ft\.?|with|x)\s+", ",");
        s = Regex.Replace(s, @"\(([^)]*)\)", ",$1,");
        return s.Split(',', '&', '×', '/').Select(Squash).Any(p => p.Length >= 2 && c.Contains(p));
    }

    /// 플리 영상 제목에 나온 낱말 중 타임라인 곡 이름들에 가장 자주(2곡 이상) 나오는 것 = 그 플리의 주인공 가수.
    /// "릴러말즈 노래모음"처럼 가수를 빼고 곡 제목만 적은 플리에서 검색어와 가수 확인에 쓴다.
    public static string? PlaylistArtist(string title, List<Track> tracks)
    {
        var names = tracks.Select(t => Squash(t.Name)).ToList();
        var best = Regex.Split(title, @"[^\p{L}\p{N}]+").Where(w => w.Length >= 2).Distinct()
            .Select(w => (w, n: names.Count(x => x.Contains(Squash(w))))).OrderByDescending(x => x.n).FirstOrDefault();
        return best.n >= 2 ? best.w : null;
    }

    /// 벅스 곡 검색 결과에서 제목이 맞는 곡들(반주 버전은 빼고): id와 가수.
    /// 가수 칸 앞에 HTML 주석이 끼는 줄(여러 가수)이 있어 주석을 건너뛰고 첫 가수를 읽는다.
    public static List<(string id, string artist)> BugsCandidates(string html, string name)
    {
        var list = new List<(string, string)>();
        foreach (Match row in Regex.Matches(html, @"<tr[^>]*trackId=""\d+"".*?</tr>", RegexOptions.Singleline))
        {
            var t = Regex.Match(row.Value, @"value=""(\d+)"" name=""check"" disc_id=""\d+"" title=""([^""]*)""");
            var a = Regex.Match(row.Value, @"<p class=""artist"">.*?<a [^>]*title=""([^""]*)""", RegexOptions.Singleline);
            if (t.Success && a.Success && !t.Groups[2].Value.Contains("Inst.") && SameSong(name, t.Groups[2].Value))
                list.Add((t.Groups[1].Value, a.Groups[1].Value));
        }
        return list;
    }

    /// 제목과 가수가 둘 다 맞는 첫 곡(같은 제목의 다른 가수 곡이 위에 오는 일이 많다)
    public static string? BugsTrack(string html, string name, string context) =>
        BugsCandidates(html, name).Where(c => ArtistIn(c.artist, context)).Select(c => c.id).FirstOrDefault();

    /// 벅스 곡 페이지의 재생 시간(초). 검색 결과에는 곡 길이가 없다.
    static async Task<double?> BugsLength(string id)
    {
        var d = await Fetch($"https://music.bugs.co.kr/track/{id}");
        if (d == null) return null;
        var m = Regex.Match(Regex.Replace(d, "<[^>]+>", " "), @"재생 ?시간[^0-9]{0,200}(\d{1,2}):(\d{2})");
        return m.Success ? double.Parse(m.Groups[1].Value) * 60 + double.Parse(m.Groups[2].Value) : null;
    }

    // MARK: 네트워크

    static readonly HttpClient Http = new(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(8) };
    static readonly ConcurrentDictionary<string, DateTime> Next = new();
    static readonly SemaphoreSlim GateLock = new(1, 1);

    /// 서버마다 줄 세우기: LRCLIB는 0.5초 간격, 429면 그 서버로 가는 모든 요청이 Retry-After만큼 함께 쉰다.
    static async Task Turn(string host, double spacing)
    {
        TimeSpan wait;
        await GateLock.WaitAsync();
        try
        {
            var start = Next.TryGetValue(host, out var n) && n > DateTime.UtcNow ? n : DateTime.UtcNow;
            Next[host] = start.AddSeconds(spacing);
            wait = start - DateTime.UtcNow;
        }
        finally { GateLock.Release(); }
        if (wait > TimeSpan.Zero) await Task.Delay(wait);
    }

    static void BackOff(string host, double seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        Next.AddOrUpdate(host, until, (_, old) => old > until ? old : until);
    }

    /// GET(body가 있으면 POST). 429면 Retry-After만큼(최대 10초), 503이면 1초 쉬고 다시(두 번까지). 실패면 null.
    public static async Task<string?> Fetch(string url, Dictionary<string, string>? query = null, string? body = null)
    {
        if (query != null) url += "?" + string.Join("&", query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        var host = new Uri(url).Host;
        var lrclib = host == "lrclib.net";
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Turn(host, lrclib ? 0.5 : 0);
            using var req = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, url);
            // LRCLIB는 앱 이름을 밝혀 달라고 한다. 유튜브·벅스는 브라우저처럼.
            req.Headers.TryAddWithoutValidation("User-Agent", lrclib ? "YouTubeLyricNotch/1.2.0 (personal Windows app)" : "Mozilla/5.0 (youtube-notch, personal)");
            req.Headers.TryAddWithoutValidation("Accept-Language", "ko");
            if (body != null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            try
            {
                using var r = await Http.SendAsync(req);
                if (r.StatusCode == HttpStatusCode.OK) return await r.Content.ReadAsStringAsync();
                if ((int)r.StatusCode == 429) BackOff(host, Math.Min(10, r.Headers.RetryAfter?.Delta?.TotalSeconds ?? 2));
                else if ((int)r.StatusCode == 503 && attempt == 0) await Task.Delay(1000);
                else return null;
            }
            catch { return null; }
        }
        return null;
    }

    class Hit
    {
        public string? trackName { get; set; }
        public string? artistName { get; set; }
        public double? duration { get; set; }
        public string? plainLyrics { get; set; }
        public string? syncedLyrics { get; set; }
    }

    record Found(Lyrics? Synced, Lyrics? Plain, bool Failed);

    /// LRCLIB(영어권 강함)와 벅스(국내 곡 강함)에 함께 물어 싱크 가사 > 일반 가사. 제목이 안 맞는 결과는 버린다.
    /// null: 어딘가에 닿지 못해(오프라인, 요청 제한) 못 찾았을 수 있음 → 나중에 다시
    /// context: 영상 제목·채널. 결과의 가수가 곡 이름·hint·context 어딘가에 있거나 곡 길이가 비슷해야 같은 곡으로 본다.
    public static async Task<Lyrics?> FetchLyrics(string name, string? author, double length, string? hint = null, string context = "")
    {
        var qs = Queries(name, author, hint);
        var ctx = string.Join(" ", name, hint ?? "", context);
        var bugs = BugsLyrics(name, ctx, qs.Take(3).ToList(), length);
        var lrc = await LrclibLyrics(name, ctx, qs, length);
        if (lrc.Synced != null) return lrc.Synced;
        var b = await bugs;
        return b.Synced ?? lrc.Plain ?? b.Plain ?? (lrc.Failed || b.Failed ? null : Lyrics.MakeNote("가사를 못 찾았어요"));
    }

    static async Task<Found> LrclibLyrics(string name, string ctx, List<string> qs, double length)
    {
        Lyrics? plain = null;
        var failed = false;
        foreach (var q in qs.Take(3))  // 요청 제한이 빡빡해서 세 번까지
        {
            var d = await Fetch("https://lrclib.net/api/search", new() { ["q"] = q });
            if (d == null) { failed = true; continue; }
            List<Hit>? hits;
            try { hits = JsonSerializer.Deserialize<List<Hit>>(d); } catch { continue; }
            if (hits == null) continue;
            // 제목이 맞고, 가수가 맞거나(곡 이름·영상 제목·채널에 나옴) 곡 길이가 비슷한 것만. 같은 제목의 다른 가수 곡을 막는다.
            // 그중 싱크 가사, 가수까지 맞는 것, 곡 길이가 비슷한 것 우선
            bool ArtistOK(Hit h) => ArtistIn(h.artistName ?? "", ctx);
            bool LengthOK(Hit h) => h.duration is { } d && length > 0 && Math.Abs(d - length) <= Math.Max(6, length * 0.05);
            double Cost(Hit h) => Math.Abs((h.duration ?? length) - length) + (h.syncedLyrics == null ? 30 : 0) + (ArtistOK(h) ? 0 : 20);
            var best = hits.Where(h => (h.syncedLyrics != null || h.plainLyrics != null) && SameSong(name, h.trackName ?? "") && (ArtistOK(h) || LengthOK(h)))
                .OrderBy(Cost).FirstOrDefault();
            if (!string.IsNullOrEmpty(best?.syncedLyrics)) return new Found(ParseLRC(best.syncedLyrics), null, false);
            if (plain == null && !string.IsNullOrEmpty(best?.plainLyrics)) plain = PlainLines(best.plainLyrics);
        }
        return new Found(null, plain, failed);
    }

    static async Task<Found> BugsLyrics(string name, string ctx, List<string> qs, double length)
    {
        string? id = null;
        var failed = false;
        foreach (var q in qs)  // 맞는 곡이 나올 때까지 검색어를 바꿔 가며
        {
            var d = await Fetch("https://music.bugs.co.kr/search/track", new() { ["q"] = q });
            if (d == null) { failed = true; continue; }
            var found = BugsCandidates(d, name);
            id = found.Where(c => ArtistIn(c.artist, ctx)).Select(c => c.id).FirstOrDefault();
            // 가수 표기가 달라(빅나티 ↔ BIG Naughty (서동현)) 안 맞으면 곡 길이로: 재생 시간이 타임라인 구간과 비슷한지(후보 3개까지)
            if (id == null && length > 0)
                foreach (var c in found.Take(3))
                    if (await BugsLength(c.id) is { } l && Math.Abs(l - length) <= Math.Max(6, length * 0.05)) { id = c.id; break; }
            if (id != null) break;
        }
        if (id == null) return new Found(null, null, failed);
        static string? Text(string? d)
        {
            if (d == null) return null;
            try { var s = JsonDocument.Parse(d).RootElement.GetProperty("lyrics").GetString(); return string.IsNullOrEmpty(s) ? null : s; }
            catch { return null; }
        }
        var plainTask = Fetch($"https://music.bugs.co.kr/player/lyrics/N/{id}");  // 싱크(T)가 없을 때를 대비해 같이 받는다
        var synced = await Fetch($"https://music.bugs.co.kr/player/lyrics/T/{id}");
        if (Text(synced) is { } s) return new Found(ParseBugs(s), null, false);
        var p = await plainTask;
        if (Text(p) is { } ps) return new Found(null, PlainLines(ps), false);
        return new Found(null, null, synced == null || p == null);
    }

    // MARK: 유튜브

    /// HTML·JSON 속 "key":"값"들을 풀어서
    public static List<string> JsonFields(string s, string key)
    {
        var output = new List<string>();
        foreach (Match m in Regex.Matches(s, "\"" + Regex.Escape(key) + "\":(\"(?:[^\"\\\\]|\\\\.)*\")"))
            try { output.Add(JsonSerializer.Deserialize<string>(m.Groups[1].Value) ?? ""); } catch { }
        return output;
    }

    /// 설명란·댓글 첫 페이지(타임라인은 고정 댓글에 많다)와 채널 이름, 분류. 댓글은 유튜브 웹이 쓰는 내부 API로 받는다.
    public static async Task<Info?> FetchInfo(string id)
    {
        var html = await Fetch("https://www.youtube.com/watch", new() { ["v"] = id });
        if (html == null) return null;
        var comments = new List<string>();
        var end = html.IndexOf("\"sectionIdentifier\":\"comment-item-section\"", StringComparison.Ordinal);
        var version = JsonFields(html, "INNERTUBE_CLIENT_VERSION").FirstOrDefault();
        if (end > 0 && JsonFields(html[..end], "token").LastOrDefault() is { } token && version != null)
        {
            var body = JsonSerializer.Serialize(new { context = new { client = new { clientName = "WEB", clientVersion = version, hl = "ko" } }, continuation = token });
            var r = await Fetch("https://www.youtube.com/youtubei/v1/next", new() { ["prettyPrint"] = "false" }, body);
            if (r == null) return null;
            comments = JsonFields(r, "content");
        }
        return new Info
        {
            Desc = JsonFields(html, "shortDescription").FirstOrDefault() ?? "",
            Comments = comments,
            Author = JsonFields(html, "author").FirstOrDefault() ?? "",
            Category = JsonFields(html, "category").FirstOrDefault() ?? "",
        };
    }

    /// Windows 미디어 세션에는 영상 id가 없어서, 제목(과 채널)이 똑같은 영상을 유튜브 검색에서 찾는다.
    /// 제목이 정확히 같은 결과만 믿고, 채널까지 같은 것을 먼저 고른다.
    public static async Task<string?> FindVideo(string title, string channel)
    {
        var html = await Fetch("https://www.youtube.com/results", new() { ["search_query"] = title });
        if (html == null) return null;
        string? sameTitle = null;
        foreach (Match m in Regex.Matches(html, "\"videoRenderer\":\\{\"videoId\":\"([\\w-]{11})\""))
        {
            var block = html.Substring(m.Index, Math.Min(6000, html.Length - m.Index));
            var t = Regex.Match(block, "\"title\":\\{\"runs\":\\[\\{\"text\":(\"(?:[^\"\\\\]|\\\\.)*\")");
            var o = Regex.Match(block, "\"ownerText\":\\{\"runs\":\\[\\{\"text\":(\"(?:[^\"\\\\]|\\\\.)*\")");
            string Decode(Match x) { try { return x.Success ? JsonSerializer.Deserialize<string>(x.Groups[1].Value) ?? "" : ""; } catch { return ""; } }
            if (Squash(Decode(t)) != Squash(title) || Squash(title).Length == 0) continue;
            if (channel.Length == 0 || Squash(Decode(o)) == Squash(channel)) return m.Groups[1].Value;
            sameTitle ??= m.Groups[1].Value;
        }
        return sameTitle;
    }

    // MARK: 곡 알아내기

    /// 곡 알아내는 순서: ① 제목의 "가수 - 곡" ② 설명란(타임라인, 또는 유튜브 자동 생성 "곡 · 가수") ③ 댓글 타임라인
    /// ④ 그래도 없으면 제목 그대로. 15분 넘는 영상(플리·모음·라이브)은 제목을 한 곡으로 보지 않는다.
    public static Songs Identify(string title, string desc, List<string> comments, string author, double duration)
    {
        var isLong = duration > 15 * 60;
        if (!isLong && SongInTitle(title) is { } s) return new Songs(One: s);
        var d = Timeline(desc);
        if (d.Count > 0) return new Songs(List: d);
        if (SongInDescription(desc) is { } sd) return new Songs(One: sd);
        var c = comments.Select(Timeline).OrderByDescending(x => x.Count).FirstOrDefault();
        if (c is { Count: > 0 }) return new Songs(List: c);
        return isLong ? new Songs() : new Songs(One: title, Author: author);
    }

    /// 제목이 한 곡이면 "가수 - 곡": "가수 - 곡", "가수 _ 곡", 또는 가수 '곡' 꼴일 때만(플리·모음 제목은 아니다).
    public static string? SongInTitle(string raw)
    {
        if (Regex.IsMatch(raw, @"(?i)playlist|플리|플레이리스트|모음|\bmix\b")) return null;
        var title = Regex.Replace(raw, @"\[[^\]]*\]|【[^】]*】|(?i)\((official[^)]*|m/?v|lyrics?[^)]*|가사)\)|\s\|.*$", "").Trim();
        if (Regex.IsMatch(title, @"\s[-–—_]\s")) return title;
        var m = Regex.Match(title, @"^(.*?)\s*['‘""“「『]([^'’""”」』]+)['’""”」』]");
        return m.Success && m.Groups[1].Value.Length > 0 ? $"{m.Groups[1].Value} - {m.Groups[2].Value}" : null;
    }

    /// 유튜브 자동 생성 설명란("Provided to YouTube by …\n\n곡 · 가수 · …")이면 "가수 - 곡"
    public static string? SongInDescription(string d)
    {
        if (!d.StartsWith("Provided to YouTube by")) return null;
        var p = d.Split('\n').FirstOrDefault(l => l.Contains(" · "))?.Split(" · ");
        return p is { Length: >= 2 } ? $"{p[1].Trim()} - {p[0].Trim()}" : null;
    }

    public static bool IsMusic(string title, Info meta, Songs songs) =>
        meta.Category == "Music" || (songs.List is { Count: > 0 } && Regex.IsMatch(title, @"(?i)playlist|플리|플레이리스트|노래\s*모음"));
}

/// 한 번 찾은 가사·타임라인·영상 id는 %LOCALAPPDATA%\YouTubeLyricNotch에 둔다: 같은 플리를 다시 틀면 바로.
public static class Store
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YouTubeLyricNotch");
    public static T? Load<T>(string name) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(Dir, name))); } catch { return null; }
    }
    public static void Save<T>(T value, string name)
    {
        try { Directory.CreateDirectory(Dir); File.WriteAllText(Path.Combine(Dir, name), JsonSerializer.Serialize(value)); } catch { }
    }
}

public static class Selftest
{
    static void Check(bool ok, string what) { if (!ok) throw new Exception("selftest 실패: " + what); }

    public static void Run()
    {
        var t = Core.Timeline("🎧 tracklist\n1. 00:00 아이유 - 밤편지\n[03:45] 10cm - 폰서트\n1:02:03 ~ 1:05:00 Song (feat. X)\n07:10 아이유 - 밤편지 (다시)\n" +
                              "03:45 같은 시간 줄은 버림\n0:09:56┃    DIMO REX - GIRLFRIEND (Feat. CAMO)\n00:15:20 한요한_It's You (Feat. NO:EL) 🎵");
        Check(t.Select(x => x.Name).SequenceEqual(new[] { "아이유 - 밤편지", "10cm - 폰서트", "아이유 - 밤편지 (다시)", "DIMO REX - GIRLFRIEND (Feat. CAMO)", "한요한_It's You (Feat. NO:EL)", "Song (feat. X)" }),
              "timeline 이름: " + string.Join(" | ", t.Select(x => x.Name)));
        Check(t.Select(x => x.Start).SequenceEqual(new double[] { 0, 225, 430, 596, 920, 3723 }), "timeline 시각");
        Check(Core.Timeline("2:30 여기 좋다\n3:10 여기도").Count == 0, "타임라인 아님");
        var l = Core.ParseLRC("[00:01.50]첫 줄\n[00:05.00][00:20.00]후렴\n[00:10.00]");
        Check(l.Lines.SequenceEqual(new[] { "첫 줄", "후렴", "", "후렴" }) && Core.LineIndex(l, 0, 30) == -1 && Core.LineIndex(l, 12, 30) == 2, "LRC");
        var o = Core.ParseLRC("[ar:IU]\n[offset:+500]\n[00:02.00]a\n[00:02.00]b\n[00:03.00]<00:03.10>c <00:03.50>d");
        Check(o.Lines.SequenceEqual(new[] { "a / b", "c d" }) && o.Times!.SequenceEqual(new[] { 1.5, 2.5 }), "LRC offset·합치기");
        Check(Core.LineIndex(new Lyrics { Lines = new() { "a", "b", "c" } }, 25, 30) == 2, "싱크 없는 가사");
        var q = Core.Queries("아이유 (IU) - 밤편지 (Through the Night) [MV]");
        Check(q.SequenceEqual(new[] { "아이유 밤편지", "밤편지 아이유", "IU 밤편지", "아이유 IU 밤편지 Through the Night" }), "queries: " + string.Join(" | ", q));
        Check(Core.Queries("CHUNG HA (청하) - México").SequenceEqual(new[] { "CHUNG HA Mexico", "Mexico CHUNG HA", "청하 Mexico", "CHUNG HA 청하 Mexico" }), "queries 악센트");
        Check(Core.Queries("하지마 - 릴러말즈, TOIL").Take(2).SequenceEqual(new[] { "하지마 릴러말즈 TOIL", "릴러말즈 하지마" }), "queries 곡 - 가수");
        Check(Core.Queries("파테코, 제이씨유카, Kid Wine - 널 떠올리는 중이야")[1] == "파테코 널 떠올리는 중이야", "queries 첫 가수");
        Check(Core.SameSong("아이유 - 밤편지", "Through the Night (밤편지)") && Core.SameSong("JAEHA (재하)_3　2　1", "3 2 1"), "sameSong");
        Check(!Core.SameSong("Crush - Whatever You Do (Feat. GRAY)", "Other (Feat. GRAY)") && !Core.SameSong("dori_2 O' CLOCK", "Yours"), "sameSong 아님");
        var b = Core.ParseBugs("11.0|난 아직＃23.5|i want to see you again＃끝");
        Check(b.Lines.SequenceEqual(new[] { "난 아직", "i want to see you again" }) && b.Times!.SequenceEqual(new[] { 11, 23.5 }), "bugs");
        static string Row(int id, string title, string artist) =>
            $"<tr trackId=\"{id}\"><input type=\"checkbox\" value=\"{id}\" name=\"check\" disc_id=\"1\" title=\"{title}\"><p class=\"artist\"><a href=\"x\" title=\"{artist}\">{artist}</a></p></tr>";
        var rows = Row(1, "Rainbow days come after rain", "Drew") + Row(2, "RAINBOW (Inst.)", "PL(피엘)") + Row(3, "RAINBOW", "PL(피엘)");
        Check(Core.BugsTrack(rows, "PL (피엘)_RAINBOW", "PL (피엘)_RAINBOW") == "3", "bugsTrack");
        // 같은 제목, 다른 가수: 곡 제목만 적힌 플리에서 성시경의 "거리에서"를 고르지 않는다
        var same = Row(1, "거리에서", "성시경") + Row(2, "거리에서", "김광석") + Row(3, "거리에서 (Feat. ASH ISLAND)", "릴러말즈 (Leellamarz)");
        Check(Core.BugsTrack(same, "거리에서 (Feat. ASH ISLAND)", "거리에서 (Feat. ASH ISLAND) 릴러말즈 [Playlist] 릴러말즈 노래모음 |Leellamarz|") == "3", "bugsTrack 가수");
        Check(Core.BugsTrack(Row(1, "거리에서", "성시경"), "거리에서", "거리에서 [Playlist] 감성힙합 모음") == null, "bugsTrack 다른 가수");
        // 여러 가수인 줄은 가수 칸 앞에 HTML 주석이 낀다
        var multi = @"<tr trackId=""7""><input type=""checkbox"" value=""7"" name=""check"" disc_id=""1"" title=""하지마""><p class=""artist""> <!-- a tag title --> <a href=""x"" class=""artistTitle"" title=""릴러말즈 (Leellamarz)"">릴러말즈</a> <a href=""javascript:void(0);"" class=""more"" title=""아티스트 전체보기"">x</a></p></tr>";
        Check(Core.BugsTrack(multi, "하지마 - 릴러말즈, TOIL", "하지마 - 릴러말즈, TOIL") == "7", "bugsTrack 여러 가수");
        Check(Core.ArtistIn("Leellamarz, ASH ISLAND", "릴러말즈 (Leellamarz)_거리에서") && Core.ArtistIn("IU (아이유)", "아이유 - 밤편지") && !Core.ArtistIn("성시경", "릴러말즈 노래모음"), "artistIn");
        var lr = Core.Timeline("0:00 거리에서 (Feat. ASH ISLAND)\n3:29 Trip (Feat. Hannah)\n7:23 하지마 - 릴러말즈, TOIL\n9:00 RUN - 릴러말즈, Panda Gomm");
        Check(Core.PlaylistArtist("[Playlist] 설렘 가득 감성힙합 릴러말즈 노래모음 |Leellamarz|", lr) == "릴러말즈" && Core.PlaylistArtist("[Playlist] 감성 플리", lr) == null, "playlistArtist");
        Check(Core.Queries("거리에서 (Feat. ASH ISLAND)", hint: "릴러말즈")[0] == "릴러말즈 거리에서", "queries hint");
        Check(Core.JsonFields("x\"author\":\"a\",\"shortDescription\":\"0:00 \\\"곡\\\"\\n1:00 b\",\"author\":\"핑프\"", "author").SequenceEqual(new[] { "a", "핑프" }), "jsonFields");
        var chapters = "0:00 Intro\n0:45 Verse\n1:30 Chorus";
        Check(Core.SongInTitle("[MV] IU(아이유) '밤편지'") == "IU(아이유) - 밤편지" && Core.SongInTitle("볼빨간사춘기 _ 나만 봄") != null, "songInTitle");
        Check(Core.SongInTitle("볼빨간사춘기 (BOL4) - 나만, 봄 (Bom) | 가사") == "볼빨간사춘기 (BOL4) - 나만, 봄 (Bom)", "songInTitle 꼬리표");
        Check(Core.SongInTitle("[Playlist] 감성 힙합 - 모음") == null && Core.SongInTitle("오늘의 브이로그") == null, "songInTitle 아님");
        Check(Core.SongInDescription("Provided to YouTube by Kakao\n\n밤편지 · IU\n\n밤편지\n\n℗ 2017") == "IU - 밤편지", "songInDescription");
        Check(Core.Identify("IU - 밤편지", chapters, new(), "IU", 260).One == "IU - 밤편지", "identify ① 제목");
        Check(Core.Identify("[Playlist] 가을", chapters, new() { "0:00 a\n1:00 b\n2:00 c\n3:00 d" }, "x", 3000).List!.Count == 3, "identify ② 설명란");
        Check(Core.Identify("[Playlist] 가을", "설명", new() { "좋아요", chapters }, "x", 3000).List!.Count == 3, "identify ③ 댓글");
        var four = Core.Identify("밤편지", "", new(), "IU", 260);
        Check(four.One == "밤편지" && four.Author == "IU", "identify ④");
        Check(Core.Identify("라이브 방송", "", new(), "x", 5000).None, "identify 모름");
    }
}

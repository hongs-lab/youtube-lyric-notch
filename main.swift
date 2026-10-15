// 유튜브 노치: Chrome에서 틀어 둔 유튜브의 지금 곡과 가사를 맥북 노치에 띄운다. 노치에 마우스를 올리면 펼쳐져 가사와 재생 버튼이 나온다.
// 잠금 화면에서는 시계 아래에 곡·가사 카드를 띄운다(비공개 SkyLight API로 잠금 화면 위 층에 올림). '지금 재생 중'에도 곡·지금 가사 줄을 보낸다.
// 플리 영상(한 영상에 여러 곡)은 설명란·댓글의 타임라인으로 지금 곡을 찾는다. 가사는 LRCLIB → 벅스 순으로 찾는다.
// 준비(한 번만): Chrome 메뉴 보기 > 개발자 > 'Apple Events의 자바스크립트 허용'
// 빌드: swiftc -O main.swift -o notch    실행: ./notch    점검: ./notch --selftest    설치용 앱·dmg: ./release.sh
// 노치를 펼친 채 우클릭: 로그인할 때 자동으로 열기, 종료
import AppKit
import MediaPlayer
import ServiceManagement
import SwiftUI

// MARK: 타임라인

struct Track: Equatable { var start: Double; var name: String }

let stamp = try! NSRegularExpression(pattern: #"(?:(\d{1,2}):)?(\d{1,2}):(\d{2})"#)

/// "00:00 가수 - 곡" 꼴 줄을 모아 시간순으로. 3곡 미만이면 타임라인이 아니라고 본다(유튜브 챕터 규칙과 같다).
func timeline(_ text: String) -> [Track] {
    var seen = Set<Double>(), out: [Track] = []
    for line in text.components(separatedBy: .newlines) {
        let ns = line as NSString, all = NSRange(location: 0, length: ns.length)
        guard let m = stamp.firstMatch(in: line, range: all) else { continue }
        let n = { (i: Int) -> Double in m.range(at: i).location == NSNotFound ? 0 : Double(ns.substring(with: m.range(at: i)))! }
        let start = n(1) * 3600 + n(2) * 60 + n(3)
        let name = stamp.stringByReplacingMatches(in: line, range: all, withTemplate: "")
            .replacingOccurrences(of: #"\[\s*\]|\(\s*\)|【\s*】"#, with: "", options: .regularExpression)
            // 앞뒤 장식(┃, ▶, 🎵, 번호 "1." …)을 떼고 괄호·느낌표 같은 곡 이름 끝은 남긴다
            .replacingOccurrences(of: #"^[^\p{L}\p{N}(\[【"'“‘]*(\d{1,3}[.)]\s+)?|[^\p{L}\p{N})\]】"'”’!?.]+$"#, with: "", options: .regularExpression)
        if !name.isEmpty, seen.insert(start).inserted { out.append(Track(start: start, name: name)) }
    }
    return out.count >= 3 ? out.sorted { $0.start < $1.start } : []
}

/// 영상 하나에서 알아낸 곡: 한 곡(author가 있으면 채널 이름도 붙여 검색) / 타임라인 여러 곡 / 모름
enum Songs: Equatable { case one(String, author: String?), list([Track]), none }

/// 곡 알아내는 순서: ① 제목의 "가수 - 곡" ② 설명란(타임라인, 또는 유튜브 자동 생성 "곡 · 가수") ③ 댓글 타임라인
/// ④ 그래도 없으면 제목 그대로. 15분 넘는 영상(플리·모음·라이브)은 제목을 한 곡으로 보지 않는다.
/// 제목이 먼저라, 한 곡짜리 뮤비 설명란의 "0:45 Verse" 같은 구간 표시를 곡 이름으로 착각하지 않는다.
func identify(title: String, desc: String, comments: [String], author: String, duration: Double) -> Songs {
    let long = duration > 15 * 60
    if !long, let s = songInTitle(title) { return .one(s, author: nil) }
    let d = timeline(desc)
    if !d.isEmpty { return .list(d) }
    if let s = songInDescription(desc) { return .one(s, author: nil) }
    if let c = comments.map(timeline).max(by: { $0.count < $1.count }), !c.isEmpty { return .list(c) }
    return long ? .none : .one(title, author: author)
}

/// 제목이 한 곡이면 "가수 - 곡": "가수 - 곡", "가수 _ 곡", 또는 가수 '곡' 꼴일 때만(플리·모음 제목은 아니다).
/// [MV], [가사/Lyrics], "| 가사" 같은 꼬리표는 뗀다.
func songInTitle(_ raw: String) -> String? {
    guard raw.range(of: #"(?i)playlist|플리|플레이리스트|모음|\bmix\b"#, options: .regularExpression) == nil else { return nil }
    let title = raw.replacingOccurrences(of: #"\[[^\]]*\]|【[^】]*】|(?i)\((official[^)]*|m/?v|lyrics?[^)]*|가사)\)|\s\|.*$"#, with: "", options: .regularExpression)
        .trimmingCharacters(in: .whitespaces)
    if title.range(of: #"\s[-–—_]\s"#, options: .regularExpression) != nil { return title }
    if let m = title.firstMatch(of: #/^(.*?)\s*['‘"“「『]([^'’"”」』]+)['’"”」』]/#), !m.1.isEmpty { return "\(m.1) - \(m.2)" }
    return nil
}

/// 유튜브 자동 생성 설명란("Provided to YouTube by …\n\n곡 · 가수 · …")이면 "가수 - 곡"
func songInDescription(_ d: String) -> String? {
    guard d.hasPrefix("Provided to YouTube by"),
          let p = d.components(separatedBy: .newlines).first(where: { $0.contains(" · ") })?.components(separatedBy: " · "), p.count >= 2
    else { return nil }
    return "\(p[1]) - \(p[0])"
}

// MARK: 가사

struct Lyrics: Codable {
    var lines: [String] = []
    var times: [Double]? = nil  // nil: 싱크 없는 가사
    var note = false  // 가사 대신 "찾는 중…" 같은 안내 한 줄
    static func note(_ s: String) -> Lyrics { Lyrics(lines: [s], times: [0], note: true) }
}

let tag = try! NSRegularExpression(pattern: #"\[(\d+):(\d+(?:\.\d+)?)\]"#)

/// LRC → 줄·시각. [offset:±ms] 시간 보정을 적용하고, 같은 시각의 여러 줄(번역·화음)은 " / "로 합치고,
/// 단어 단위 타이밍 <mm:ss.xx>은 뗀다.
func parseLRC(_ s: String) -> Lyrics {
    var pairs: [(Double, String)] = [], offset = 0.0
    for line in s.components(separatedBy: .newlines) {
        if let r = line.range(of: #"^\s*\[offset:\s*[+-]?\d+\]"#, options: [.regularExpression, .caseInsensitive]) {
            offset = (Double(line[r].filter { "+-0123456789".contains($0) }) ?? 0) / 1000  // +면 가사를 그만큼 일찍
            continue
        }
        let ns = line as NSString
        let ms = tag.matches(in: line, range: NSRange(location: 0, length: ns.length))
        guard let last = ms.last else { continue }
        let text = ns.substring(from: NSMaxRange(last.range))
            .replacingOccurrences(of: #"<\d+:\d+(?:\.\d+)?>"#, with: "", options: .regularExpression).trimmingCharacters(in: .whitespaces)
        for m in ms { pairs.append((Double(ns.substring(with: m.range(at: 1)))! * 60 + Double(ns.substring(with: m.range(at: 2)))!, text)) }
    }
    var lines: [String] = [], times: [Double] = []
    for (t, x) in pairs.enumerated().sorted(by: { ($0.element.0, $0.offset) < ($1.element.0, $1.offset) }).map(\.element) {
        if times.last == t - offset {  // 같은 시각: 한 줄로
            if !x.isEmpty, !lines[lines.count - 1].contains(x) { lines[lines.count - 1] += lines[lines.count - 1].isEmpty ? x : " / " + x }
        } else { times.append(t - offset); lines.append(x) }
    }
    return Lyrics(lines: lines, times: times)
}

/// 지금 부르는 줄 번호. -1이면 첫 줄 전.
func lineIndex(_ l: Lyrics, at t: Double, length: Double) -> Int {
    guard let times = l.times else {
        // ponytail: 싱크 없는 가사는 곡 진행률로 줄을 어림한다. 간주가 길면 어긋남.
        return min(l.lines.count - 1, Int(t / max(length, 1) * Double(l.lines.count)))
    }
    return times.lastIndex { $0 <= t } ?? -1
}

/// 검색어 후보(악센트는 풀어서: México → Mexico):
/// 괄호 속(영문 제목, feat., MV …)을 뺀 것 → "가수 - 제목"이면 첫 가수 + 제목, 괄호 속 다른 이름 + 제목
/// (여러 가수, 영문·한글 이름 중 하나만 맞아도 찾게) → 괄호만 푼 것 → (단일 곡 영상이면) 채널 이름을 붙인 것.
func queries(_ name: String, author: String? = nil) -> [String] {
    let name = name.applyingTransform(.stripDiacritics, reverse: false) ?? name
    func tidy(_ s: String) -> String {
        s.replacingOccurrences(of: #"(?i)\b(official|music video|lyrics?|audio|visualizer|m/?v)\b|가사|뮤직비디오|뮤비"#, with: " ", options: .regularExpression)
            .replacingOccurrences(of: #"[-–—_|/:~"“”()\[\]【】「」,&]"#, with: " ", options: .regularExpression)
            .components(separatedBy: .whitespaces).filter { !$0.isEmpty }.joined(separator: " ")
    }
    func strip(_ s: String) -> String { s.replacingOccurrences(of: #"\([^)]*\)|\[[^\]]*\]|【[^】]*】"#, with: " ", options: .regularExpression) }
    var qs = [tidy(strip(name))]
    if let r = name.range(of: #"\s+[-–—]\s+|_"#, options: .regularExpression) {
        let artist = String(name[..<r.lowerBound]), title = tidy(strip(String(name[r.upperBound...])))
        qs.append(tidy(strip(artist).components(separatedBy: CharacterSet(charactersIn: ",&")).first ?? artist) + " " + title)
        qs += artist.matches(of: #/\(([^)]*)\)/#).map { tidy(String($0.1)) + " " + title }
    }
    qs.append(tidy(name))
    let bare = strip(name)
    if let author { qs.append(tidy(author.replacingOccurrences(of: " - Topic", with: "") + " " + bare)) }
    var seen = Set<String>()
    return qs.filter { !$0.isEmpty && seen.insert($0).inserted }
}

struct Hit: Decodable { var trackName: String?; var artistName: String?; var duration: Double?; var plainLyrics: String?; var syncedLyrics: String? }

struct BugsLyrics: Decodable { var lyrics: String? }

/// 글자·숫자만 소문자로("&amp;" 같은 HTML 문자 참조는 버림)
func squash(_ s: String) -> String {
    String(String.UnicodeScalarView((s.applyingTransform(.stripDiacritics, reverse: false) ?? s).lowercased().replacingOccurrences(of: #"&#?\w+;"#, with: "", options: .regularExpression)
        .unicodeScalars.filter(CharacterSet.alphanumerics.contains)))
}

/// 검색 결과의 곡 제목(또는 가수)이 우리 곡 이름 안에 있나. 엉뚱한 곡의 가사를 막는다.
/// "Through the Night (밤편지)"처럼 괄호 속 다른 언어 제목도 인정하고, (Feat. …) (Prod. …)는 안 본다.
func sameSong(_ name: String, _ title: String) -> Bool {
    let n = squash(name)
    let inner = title.matches(of: #/\(([^)]*)\)/#).map { String($0.1) }.filter { $0.range(of: #"(?i)^\s*(feat|ft|prod|with)\b"#, options: .regularExpression) == nil }
    let outer = title.replacingOccurrences(of: #"\([^)]*\)"#, with: "", options: .regularExpression)
    return ([outer] + inner).map(squash).contains { !$0.isEmpty && n.contains($0) }
}

/// GET(body가 있으면 POST). 429(요청 제한)면 Retry-After만큼(최대 10초) 쉬고 두 번까지 다시. 실패면 nil.
/// 서버마다 요청을 줄 세운다: LRCLIB는 0.5초 간격, 429면 그 서버로 가는 모든 요청이 Retry-After만큼 함께 쉰다.
/// (지금 곡 검색과 미리 받기가 한꺼번에 몰리면 LRCLIB가 바로 429를 준다. LiveLyrics 방식)
actor Gate {
    var next: [String: Date] = [:]
    func turn(_ host: String, spacing: Double) async {
        let start = max(Date(), next[host] ?? .distantPast)
        next[host] = start.addingTimeInterval(spacing)
        if start > Date() { try? await Task.sleep(for: .seconds(start.timeIntervalSinceNow)) }
    }
    func backOff(_ host: String, _ seconds: Double) { next[host] = max(next[host] ?? .distantPast, Date().addingTimeInterval(seconds)) }
}
let gate = Gate()

/// GET(body가 있으면 POST). 429면 Retry-After만큼(최대 10초), 503이면 1초 쉬고 다시(두 번까지). 실패면 nil.
func fetch(_ url: String, _ query: [String: String] = [:], body: Data? = nil) async -> Data? {
    var c = URLComponents(string: url)!
    if !query.isEmpty { c.queryItems = query.map { URLQueryItem(name: $0.key, value: $0.value) } }
    let host = c.host ?? "", lrclib = host == "lrclib.net"
    var req = URLRequest(url: c.url!)
    // LRCLIB는 앱 이름을 밝혀 달라고 한다. 유튜브·벅스는 브라우저처럼.
    req.setValue(lrclib ? "YouTubeNotch/1.1.0 (personal macOS app)" : "Mozilla/5.0 (youtube-notch, personal)", forHTTPHeaderField: "User-Agent")
    req.timeoutInterval = 8  // 서버가 늘어지면 기본 60초를 기다리게 된다
    if let body { req.httpMethod = "POST"; req.httpBody = body; req.setValue("application/json", forHTTPHeaderField: "Content-Type") }
    for attempt in 0..<3 {
        await gate.turn(host, spacing: lrclib ? 0.5 : 0)
        guard let (d, r) = try? await URLSession.shared.data(for: req), let h = r as? HTTPURLResponse else { return nil }
        if h.statusCode == 200 { return d }
        if h.statusCode == 429 { await gate.backOff(host, min(10, Double(h.value(forHTTPHeaderField: "Retry-After") ?? "") ?? 2)) }
        else if h.statusCode == 503, attempt == 0 { try? await Task.sleep(for: .seconds(1)) }
        else { return nil }
    }
    return nil
}

/// 벅스 싱크 가사 "11.0|첫 줄＃23.0|둘째 줄＃…"
func parseBugs(_ s: String) -> Lyrics {
    let pairs = s.components(separatedBy: "＃").compactMap { e -> (Double, String)? in
        let p = e.split(separator: "|", maxSplits: 1, omittingEmptySubsequences: false)
        guard p.count == 2, let t = Double(p[0]) else { return nil }
        return (t, p[1].trimmingCharacters(in: .whitespaces))
    }
    return Lyrics(lines: pairs.map { $0.1 }, times: pairs.map { $0.0 })
}

/// 벅스 곡 검색 결과에서 제목이 우리 곡 이름과 맞는 첫 곡의 id(반주 버전은 가사가 없으니 건너뜀)
func bugsTrack(_ html: String, for name: String) -> String? {
    html.matches(of: #/value="(\d+)" name="check" disc_id="\d+" title="([^"]*)"/#)
        .first { !$0.2.contains("Inst.") && sameSong(name, String($0.2)) }.map { String($0.1) }
}

func plainLines(_ s: String) -> Lyrics { Lyrics(lines: s.components(separatedBy: .newlines).filter { !$0.trimmingCharacters(in: .whitespaces).isEmpty }) }

/// 한 곳에서 찾은 결과. failed: 요청이 실패해서 없을 수도 있음
typealias Found = (synced: Lyrics?, plain: Lyrics?, failed: Bool)

/// LRCLIB(영어권 강함)와 벅스(국내 곡 강함)에 함께 물어 싱크 가사 > 일반 가사. 제목이 안 맞는 결과는 버린다.
/// nil: 어딘가에 닿지 못해(오프라인, 요청 제한) 못 찾았을 수 있음 → 나중에 다시
func fetchLyrics(_ name: String, author: String?, length: Double) async -> Lyrics? {
    let qs = queries(name, author: author)
    async let bugs = bugsLyrics(name, Array(qs.prefix(3)))
    let lrc = await lrclibLyrics(name, qs, length)
    if let s = lrc.synced { return s }  // 벅스 쪽은 취소된다
    let b = await bugs
    return b.synced ?? lrc.plain ?? b.plain ?? (lrc.failed || b.failed ? nil : .note("가사를 못 찾았어요"))
}

func lrclibLyrics(_ name: String, _ qs: [String], _ length: Double) async -> Found {
    var plain: Lyrics?, failed = false
    for q in qs.prefix(3) {  // 요청 제한이 빡빡해서 세 번까지
        guard let d = await fetch("https://lrclib.net/api/search", ["q": q]) else { failed = true; continue }
        guard let hits = try? JSONDecoder().decode([Hit].self, from: d) else { continue }
        // 제목이 맞는 것만. 싱크 가사, 가수까지 맞는 것, 곡 길이가 비슷한 것 우선
        let cost = { (h: Hit) in
            abs((h.duration ?? length) - length) + (h.syncedLyrics == nil ? 30 : 0) + (sameSong(name, h.artistName ?? "") ? 0 : 20)
        }
        let best = hits.filter { ($0.syncedLyrics != nil || $0.plainLyrics != nil) && sameSong(name, $0.trackName ?? "") }.min { cost($0) < cost($1) }
        if let s = best?.syncedLyrics, !s.isEmpty { return (parseLRC(s), nil, false) }
        if plain == nil, let p = best?.plainLyrics, !p.isEmpty { plain = plainLines(p) }
    }
    return (nil, plain, failed)
}

func bugsLyrics(_ name: String, _ qs: [String]) async -> Found {
    var id: String?, failed = false
    for q in qs where id == nil {  // 제목이 맞는 곡이 나올 때까지 검색어를 바꿔 가며
        guard let d = await fetch("https://music.bugs.co.kr/search/track", ["q": q]) else { failed = true; continue }
        id = bugsTrack(String(decoding: d, as: UTF8.self), for: name)
    }
    guard let id else { return (nil, nil, failed) }
    func text(_ d: Data?) -> String? { d.flatMap { try? JSONDecoder().decode(BugsLyrics.self, from: $0) }?.lyrics.flatMap { $0.isEmpty ? nil : $0 } }
    async let plain = fetch("https://music.bugs.co.kr/player/lyrics/N/\(id)")  // 싱크(T)가 없을 때를 대비해 같이 받는다
    let synced = await fetch("https://music.bugs.co.kr/player/lyrics/T/\(id)")
    if let s = text(synced) { return (parseBugs(s), nil, false) }
    let p = await plain
    if let s = text(p) { return (nil, plainLines(s), false) }
    return (nil, nil, synced == nil || p == nil)
}

if CommandLine.arguments.contains("--selftest") {
    let t = timeline("""
    🎧 tracklist
    1. 00:00 아이유 - 밤편지
    [03:45] 10cm - 폰서트
    1:02:03 ~ 1:05:00 Song (feat. X)
    07:10 아이유 - 밤편지 (다시)
    03:45 같은 시간 줄은 버림
    0:09:56┃    DIMO REX - GIRLFRIEND (Feat. CAMO)
    00:15:20 한요한_It's You (Feat. NO:EL) 🎵
    """)
    precondition(t.map(\.name) == ["아이유 - 밤편지", "10cm - 폰서트", "아이유 - 밤편지 (다시)", "DIMO REX - GIRLFRIEND (Feat. CAMO)",
                                   "한요한_It's You (Feat. NO:EL)", "Song (feat. X)"], "\(t)")
    precondition(t.map(\.start) == [0, 225, 430, 596, 920, 3723])
    precondition(timeline("2:30 여기 좋다\n3:10 여기도").isEmpty)
    let l = parseLRC("[00:01.50]첫 줄\n[00:05.00][00:20.00]후렴\n[00:10.00]")
    precondition(l.lines == ["첫 줄", "후렴", "", "후렴"] && lineIndex(l, at: 0, length: 30) == -1 && lineIndex(l, at: 12, length: 30) == 2)
    precondition(lineIndex(Lyrics(lines: ["a", "b", "c"]), at: 25, length: 30) == 2)
    let o = parseLRC("[ar:IU]\n[offset:+500]\n[00:02.00]a\n[00:02.00]b\n[00:03.00]<00:03.10>c <00:03.50>d")
    precondition(o.lines == ["a / b", "c d"] && o.times == [1.5, 2.5], "\(o)")
    let q = queries("아이유 (IU) - 밤편지 (Through the Night) [MV]")
    precondition(q == ["아이유 밤편지", "IU 밤편지", "아이유 IU 밤편지 Through the Night"], "\(q)")
    precondition(queries("CHUNG HA (청하) - México") == ["CHUNG HA Mexico", "청하 Mexico", "CHUNG HA 청하 Mexico"], "\(queries("CHUNG HA (청하) - México"))")
    precondition(queries("파테코, 제이씨유카, Kid Wine - 널 떠올리는 중이야")[1] == "파테코 널 떠올리는 중이야")
    precondition(sameSong("CHUNG HA (청하) - México", "Mexico") && squash("밤편지") == "밤편지")
    precondition(queries("현서 (HYUN SEO)_널 떠나보내기엔 너무 아프지만 (Feat. 이을 (E.ul))")[0] == "현서 널 떠나보내기엔 너무 아프지만")
    precondition(jsonFields(#"x"author":"a","shortDescription":"0:00 \"곡\"\n1:00 b","author":"핑프""#, "author") == ["a", "핑프"])
    let b = parseBugs("11.0|난 아직＃23.5|i want to see you again＃끝")
    precondition(b.lines == ["난 아직", "i want to see you again"] && b.times == [11, 23.5])
    let row = #"<tr trackId="6170006"><input type="checkbox" value="6170006" name="check" disc_id="1" title="See you again (Feat. A &amp; B)">"#
    precondition(bugsTrack(row, for: "소수빈_See you again") == "6170006" && bugsTrack(row, for: "다른 곡") == nil)
    let rows = #"value="1" name="check" disc_id="1" title="Rainbow days come after rain" value="2" name="check" disc_id="1" title="RAINBOW (Inst.)" value="3" name="check" disc_id="1" title="RAINBOW""#
    precondition(bugsTrack(rows, for: "PL (피엘)_RAINBOW") == "3")
    precondition(sameSong("아이유 - 밤편지", "Through the Night (밤편지)") && sameSong("JAEHA (재하)_3　2　1", "3 2 1"))
    precondition(!sameSong("Crush - Whatever You Do (Feat. GRAY)", "Other (Feat. GRAY)") && !sameSong("dori_2 O' CLOCK", "Yours"))
    // 곡 알아내는 순서
    let chapters = "0:00 Intro\n0:45 Verse\n1:30 Chorus"
    precondition(songInTitle("[MV] IU(아이유) '밤편지'") == "IU(아이유) - 밤편지" && songInTitle("볼빨간사춘기 _ 나만 봄") != nil)
    precondition(songInTitle("볼빨간사춘기 (BOL4) - 나만, 봄 (Bom) | 가사") == "볼빨간사춘기 (BOL4) - 나만, 봄 (Bom)")
    precondition(songInTitle("아이유(IU) - 밤편지 [가사/Lyrics]") == "아이유(IU) - 밤편지")
    precondition(songInTitle("[Playlist] 감성 힙합 - 모음") == nil && songInTitle("오늘의 브이로그") == nil)
    precondition(songInDescription("Provided to YouTube by Kakao\n\n밤편지 · IU\n\n밤편지\n\n℗ 2017") == "IU - 밤편지")
    precondition(identify(title: "IU - 밤편지", desc: chapters, comments: [], author: "IU", duration: 260) == .one("IU - 밤편지", author: nil))  // ① 제목 먼저
    precondition(identify(title: "[Playlist] 가을", desc: chapters, comments: ["0:00 a\n1:00 b\n2:00 c\n3:00 d"], author: "x", duration: 3000)
                 == .list(timeline(chapters)))  // ② 설명란이 댓글보다 먼저
    precondition(identify(title: "밤편지", desc: "Provided to YouTube by Kakao\n\n밤편지 · IU", comments: [], author: "IU - Topic", duration: 260)
                 == .one("IU - 밤편지", author: nil))  // ② 자동 생성 설명란
    precondition(identify(title: "[Playlist] 가을", desc: "설명", comments: ["좋아요", chapters], author: "x", duration: 3000) == .list(timeline(chapters)))  // ③ 댓글
    precondition(identify(title: "밤편지", desc: "", comments: [], author: "IU", duration: 260) == .one("밤편지", author: "IU"))  // ④ 제목 그대로
    precondition(identify(title: "라이브 방송", desc: "", comments: [], author: "x", duration: 5000) == .none)
    print("selftest ok")
    exit(0)
}

// MARK: Chrome에서 읽기

// 지금 영상 id·제목·시간. AppleScript JS는 페이지와 격리된 공간에서 돌아 DOM만 보인다
// (movie_player.getVideoData 같은 유튜브 함수는 안 보임). 설명란·댓글은 그래서 fetchInfo가 따로 받는다.
let js = """
(() => { try {
  const v = document.querySelector('#movie_player video'), id = new URLSearchParams(location.search).get('v');
  if (!v || !id) return '';
  return JSON.stringify({ id, title: document.title.replace(/^[(][0-9]+[)] /, '').replace(/ - YouTube$/, ''),
    time: v.currentTime, duration: v.duration || 0, playing: !v.paused,
    ad: document.getElementById('movie_player').classList.contains('ad-showing') });
} catch (e) { return ''; } })()
"""

/// HTML·JSON 속 "key":"값"들을 풀어서
func jsonFields(_ s: String, _ key: String) -> [String] {
    let re = try! NSRegularExpression(pattern: #""\#(key)":("(?:[^"\\]|\\.)*")"#), ns = s as NSString
    return re.matches(in: s, range: NSRange(location: 0, length: ns.length)).compactMap {
        try? JSONDecoder().decode(String.self, from: Data(ns.substring(with: $0.range(at: 1)).utf8))
    }
}

/// 영상 페이지에서 꺼낸 것. category는 업로더가 고른 분류("Music", "Gaming", "Comedy" …)
struct Info: Codable { var desc: String; var comments: [String]; var author: String; var category: String }

/// 설명란·댓글 첫 페이지(타임라인은 고정 댓글에 많다)와 채널 이름, 분류. 댓글은 탭을 스크롤해야 불러와지니
/// 유튜브 웹이 쓰는 내부 API(/youtubei/v1/next)로 직접 받는다. nil: 네트워크 실패 → 나중에 다시
func fetchInfo(_ id: String) async -> Info? {
    guard let d = await fetch("https://www.youtube.com/watch", ["v": id]) else { return nil }
    let html = String(decoding: d, as: UTF8.self)
    var comments: [String] = []
    // 댓글 칸("comment-item-section") 바로 앞의 이어받기 토큰
    if let end = html.range(of: #""sectionIdentifier":"comment-item-section""#),
       let token = jsonFields(String(html[..<end.lowerBound]), "token").last,
       let version = jsonFields(html, "INNERTUBE_CLIENT_VERSION").first {
        let body = try! JSONSerialization.data(withJSONObject: ["context": ["client": ["clientName": "WEB", "clientVersion": version, "hl": "ko"]],
                                                                "continuation": token])
        guard let r = await fetch("https://www.youtube.com/youtubei/v1/next", ["prettyPrint": "false"], body: body) else { return nil }
        comments = jsonFields(String(decoding: r, as: UTF8.self), "content")
    }
    return Info(desc: jsonFields(html, "shortDescription").first ?? "", comments: comments, author: jsonFields(html, "author").first ?? "", category: jsonFields(html, "category").first ?? "")
}

// 탭 안에서 소리를 분석한다. video.captureStream()으로 소리를 '복사'해 분석기에만 물리고 소리 0으로 내보내니,
// 원래 재생되는 소리는 건드리지 않는다. 저음 → 고음 4칸의 크기(0~255)를 돌려준다.
// 유튜브는 다음 영상을 같은 <video>에 src만 바꿔 끼우는데, 그때 복사해 둔 트랙은 'live'인 채로 소리만 끊긴다.
// 그래서 src가 바뀌거나 재생 중인데 2초 넘게 0만 나오면 다시 연결한다.
let meterJS = """
(() => { try {
  const v = document.querySelector('#movie_player video');
  const ctx = window.__notchCtx = window.__notchCtx || new AudioContext();
  let a = window.__notch;
  if (!a || a.v !== v || a.src !== v.src || a.ctx !== ctx || a.track.readyState === 'ended' || a.zeros > 16) {
    const s = v.captureStream();
    s.getVideoTracks().forEach(t => t.stop());
    const track = s.getAudioTracks()[0], an = ctx.createAnalyser(), g = ctx.createGain();
    an.fftSize = 256; an.smoothingTimeConstant = 0.5; an.minDecibels = -75; an.maxDecibels = -20; g.gain.value = 0;
    ctx.createMediaStreamSource(new MediaStream([track])).connect(an); an.connect(g); g.connect(ctx.destination);
    if (a && a.ctx !== ctx) a.ctx.close();
    a = window.__notch = { v, src: v.src, ctx, an, track, zeros: 0, buf: new Uint8Array(an.frequencyBinCount) };
  }
  if (ctx.state !== 'running') ctx.resume();
  a.an.getByteFrequencyData(a.buf);
  const band = (lo, hi) => { let s = 0; for (let i = lo; i < hi; i++) s += a.buf[i]; return Math.round(s / (hi - lo)); };
  const x = [band(1, 4), band(4, 12), band(12, 32), band(32, 80)];
  a.zeros = x.some(n => n > 0) || v.paused ? 0 : a.zeros + 1;
  return x.join(',');
} catch (e) { return ''; } })()
"""

/// "CrTb" 같은 네 글자 Apple Event 코드
func fcc(_ s: String) -> FourCharCode { s.utf8.reduce(0) { $0 << 8 | FourCharCode($1) } }

/// Chrome에 Apple Event를 직접 보낸다. NSAppleScript는 메인 스레드에서만 써야 해서 읽을 때마다 화면이 ~150ms 멈췄는데,
/// 이건 다른 스레드에서 보내도 된다. 코드는 Chrome의 AppleScript 사전(sdef)에서: 탭 CrTb, 창 cwin, 실행 CrSu/ExJa, 자바스크립트 JvSc.
enum Chrome {
    static let target = NSAppleEventDescriptor(bundleIdentifier: "com.google.Chrome")
    static let all = withUnsafeBytes(of: fcc("all ")) { NSAppleEventDescriptor(descriptorType: fcc("abso"), bytes: $0.baseAddress, length: 4)! }  // 네이티브 바이트 순서로

    /// want 종류의 form(번호·id·속성)이 data인 것, from 안에서
    static func spec(_ want: String, _ form: String, _ data: NSAppleEventDescriptor, of from: NSAppleEventDescriptor = .null()) -> NSAppleEventDescriptor {
        let r = NSAppleEventDescriptor.record()
        r.setDescriptor(NSAppleEventDescriptor(typeCode: fcc(want)), forKeyword: fcc("want"))
        r.setDescriptor(NSAppleEventDescriptor(enumCode: fcc(form)), forKeyword: fcc("form"))
        r.setDescriptor(data, forKeyword: fcc("seld"))
        r.setDescriptor(from, forKeyword: fcc("from"))
        return r.coerce(toDescriptorType: fcc("obj "))!
    }

    static func send(_ cls: String, _ id: String, _ direct: NSAppleEventDescriptor, _ params: [String: NSAppleEventDescriptor] = [:]) throws -> NSAppleEventDescriptor {
        let e = NSAppleEventDescriptor.appleEvent(withEventClass: fcc(cls), eventID: fcc(id), targetDescriptor: target,
                                                  returnID: AEReturnID(-1), transactionID: AETransactionID(0))
        e.setParam(direct, forKeyword: fcc("----"))
        for (k, v) in params { e.setParam(v, forKeyword: fcc(k)) }
        let reply = try e.sendEvent(options: [.waitForReply, .neverInteract], timeout: 2)
        if let n = reply.paramDescriptor(forKeyword: fcc("errn"))?.int32Value, n != 0 { throw NSError(domain: NSOSStatusErrorDomain, code: Int(n)) }
        return reply.paramDescriptor(forKeyword: fcc("----")) ?? .null()
    }

    /// 모든 탭의 위치와 URL(창 id·탭 id·URL을 한 번씩만 묻는다)
    static func tabs() throws -> [(tab: NSAppleEventDescriptor, url: String)] {
        let windows = spec("cwin", "indx", all), everyTab = spec("CrTb", "indx", all, of: windows)
        func get(_ property: String, of x: NSAppleEventDescriptor) throws -> NSAppleEventDescriptor {
            try send("core", "getd", spec("prop", "prop", NSAppleEventDescriptor(typeCode: fcc(property)), of: x))
        }
        let wids = try get("ID  ", of: windows), tids = try get("ID  ", of: everyTab), urls = try get("URL ", of: everyTab)
        var out: [(NSAppleEventDescriptor, String)] = []
        for w in 0..<wids.numberOfItems {
            guard let wid = wids.atIndex(w + 1), let ts = tids.atIndex(w + 1), let us = urls.atIndex(w + 1) else { continue }
            for i in 0..<ts.numberOfItems {
                guard let tid = ts.atIndex(i + 1), let u = us.atIndex(i + 1)?.stringValue else { continue }
                out.append((spec("CrTb", "ID  ", tid, of: spec("cwin", "ID  ", wid)), u))
            }
        }
        return out
    }

    static func run(_ js: String, in tab: NSAppleEventDescriptor) throws -> String {
        try send("CrSu", "ExJa", tab, ["JvSc": NSAppleEventDescriptor(string: js)]).stringValue ?? ""
    }

    /// 유튜브 영상 탭을 읽는다. 재생 중인 탭 우선. 꺼져 있으면 깨우지 않는다.
    /// 자바스크립트 허용은 Chrome 프로필마다 따로라, 읽히는 탭이 하나도 없을 때만 오류로 알린다.
    static func read() -> Result<(json: String, tab: NSAppleEventDescriptor)?, NSError> {
        guard !NSRunningApplication.runningApplications(withBundleIdentifier: "com.google.Chrome").isEmpty else { return .success(nil) }
        var found: (json: String, tab: NSAppleEventDescriptor)?, failed: NSError?
        do {
            for (tab, url) in try tabs() where url.contains("youtube.com/watch") {
                do {
                    let r = try run(js, in: tab)
                    if r.isEmpty { continue }
                    if r.contains("\"playing\":true") { return .success((r, tab)) }
                    if found == nil { found = (r, tab) }
                } catch { failed = error as NSError }
            }
        } catch { return .failure(error as NSError) }
        if found == nil, let failed { return .failure(failed) }
        return .success(found)
    }
}

let chromeQueue = DispatchQueue(label: "chrome"), meterQueue = DispatchQueue(label: "meter")

/// 한 번 찾은 가사·타임라인은 디스크(~/Library/Caches/youtube-notch)에 둔다: 같은 플리를 다시 틀면 기다림 없이 바로.
/// ponytail: 계속 쌓기만 한다(곡당 몇 KB). 커지면 오래된 것부터 지우기.
enum Store {
    static let dir = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0].appendingPathComponent("youtube-notch")
    static func load<T: Decodable>(_ name: String) -> T? { try? JSONDecoder().decode(T.self, from: Data(contentsOf: dir.appendingPathComponent(name))) }
    static func save<T: Encodable>(_ value: T, _ name: String) {
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        try? JSONEncoder().encode(value).write(to: dir.appendingPathComponent(name), options: .atomic)
    }
}

struct Video: Decodable { var id, title: String; var time, duration: Double; var playing, ad: Bool }

final class Model: ObservableObject {
    @Published var video: Video?
    @Published var problem: String?
    @Published var expanded = false
    @Published var lyrics: [String: Lyrics] = Store.load("lyrics.json") ?? [:]  // 곡 이름 → 가사
    var info: [String: Info] = Store.load("info.json") ?? [:]  // 영상 id → 설명란·댓글, 채널, 분류(저장해 둔 걸 먼저 쓰고 새로 받아 바꾼다)
    var dirty = false  // 디스크에 아직 안 쓴 게 있음
    var last: (json: String, tab: NSAppleEventDescriptor)?  // 마지막으로 읽은 영상(설명란이 오자마자 다시 반영하려고)
    var lastFetch = Date.distantPast  // 미리 찾기 간격
    var asked = Set<String>()
    var tries: [String: Int] = [:]  // 곡 이름 → 가사 찾기 실패 횟수
    var artwork: [String: MPMediaItemArtwork] = [:]  // 영상 id → 썸네일(잠금 화면·제어 센터용)
    @Published var thumbs: [String: NSImage] = [:]  // 영상 id → 썸네일(노치·카드용, 한 번 받아 두고 계속 쓴다)
    var published = (key: "", at: Date.distantPast)
    var tracks: [Track] = []  // 타임라인이면 곡들
    var single: (name: String, author: String?)?  // 한 곡짜리면 그 곡
    var polledAt = Date()
    var tab: NSAppleEventDescriptor?  // 띄워 둔 영상의 탭(tab id … of window id …)
    var busy = (poll: false, listen: false)  // Chrome에 묻는 중이면 겹쳐 보내지 않는다
    var loading = Set<String>()  // 가사 찾는 중인 곡
    @Published var levels: [Double]?  // 막대 4개 높이 0~1(저음 → 고음). nil이면 못 읽음
    var lo = [Double](repeating: 255, count: 4), hi = [Double](repeating: 0, count: 4)

    /// 영상 시간. 0.5초마다 읽고 그 사이는 시계로 채운다.
    var t: Double { guard let v = video else { return 0 }; return v.time + (v.playing ? Date().timeIntervalSince(polledAt) : 0) }

    /// 노치 크기: 접힘 / 곡이 있으면 크게 / 곡 없이 안내만 있으면 작게
    var size: CGSize { !visible ? notch : !expanded ? small : video != nil ? big : compact }
    /// 보일 게 있나(곡, 또는 연결 안내). 없으면 실제 노치 크기로 접혀 들어가 사라진다.
    var visible: Bool { video != nil || problem != nil }

    var song: (name: String, start: Double, length: Double) {
        guard let v = video else { return ("", 0, 1) }
        guard !tracks.isEmpty else { return (single?.name ?? v.title, 0, v.duration) }
        let i = tracks.lastIndex { $0.start <= t } ?? 0
        let end = i + 1 < tracks.count ? tracks[i + 1].start : v.duration
        return (tracks[i].name, tracks[i].start, end - tracks[i].start)
    }

    /// Chrome을 1초마다 읽는다(다른 스레드에서, 화면 안 멈춤)
    func poll() {
        guard !busy.poll else { return }
        busy.poll = true
        chromeQueue.async {
            let r = Chrome.read()
            DispatchQueue.main.async {
                self.busy.poll = false
                switch r {
                case .success(let found?): self.problem = nil; self.last = found; self.apply(found.json, tab: found.tab)
                case .success(nil): self.problem = nil; self.video = nil
                case .failure(let e) where e.code == -1743:
                    self.problem = "시스템 설정 > 개인정보 보호 및 보안 > 자동화에서\nChrome 제어를 허용해 주세요"; self.video = nil
                case .failure(let e) where e.code == 12:
                    self.problem = "Chrome 보기 > 개발자 >\nApple Events의 자바스크립트 허용을 켜 주세요"; self.video = nil
                case .failure: break  // Chrome이 잠깐 바쁨(시간 초과 등): 다음 번에 다시
                }
            }
        }
    }

    /// Chrome에서 읽은 영상 JSON과 그 탭을 반영한다
    func apply(_ json: String, tab: NSAppleEventDescriptor) {
        guard var v = try? JSONDecoder().decode(Video.self, from: Data(json.utf8)) else { video = nil; return }
        // 광고 동안은 곡 시간을 멈춰 둔다. 켜자마자 광고면 광고 시간을 곡 시간으로 착각하지 않게 아직 띄우지 않는다.
        if v.ad {
            guard var old = video else { return }
            old.ad = true; old.playing = false; v = old
        }
        if asked.insert(v.id).inserted {
            let id = v.id
            Task {
                async let art = fetch("https://i.ytimg.com/vi/\(id)/mqdefault.jpg")  // 썸네일과 설명란·댓글을 함께 받는다
                let i = await fetchInfo(id)
                await MainActor.run {
                    if let i { self.info[id] = i; self.dirty = true } else { DispatchQueue.main.asyncAfter(deadline: .now() + 30) { self.asked.remove(id) } }
                    if let l = self.last { self.apply(l.json, tab: l.tab) }  // 다음 읽기를 기다리지 않고 바로
                }
                if let d = await art, let img = NSImage(data: d) {
                    await MainActor.run { self.thumbs[id] = img; self.artwork[id] = MPMediaItemArtwork(boundsSize: img.size) { _ in img } }
                }
            }
        }
        // 설명란·댓글·분류가 와야 음악인지, 지금 곡이 뭔지 안다. 그 전엔 새 영상을 띄우지 않는다(1초쯤).
        guard let meta = info[v.id] else { return }
        let songs = identify(title: v.title, desc: meta.desc, comments: meta.comments, author: meta.author, duration: v.duration)
        var found: [Track] = [], one: (String, String?)?
        switch songs { case .list(let t): found = t; case .one(let n, let a): one = (n, a); case .none: break }
        // 음악만: 분류가 음악이거나, 제목이 플리이고 타임라인이 있으면. 아니면 노치·잠금 화면·미디어 키 모두 비켜 준다.
        guard meta.category == "Music" || (!found.isEmpty && v.title.range(of: #"(?i)playlist|플리|플레이리스트|노래\s*모음"#, options: .regularExpression) != nil)
        else { video = nil; self.tab = nil; return }
        self.tab = tab
        video = v
        polledAt = Date()
        tracks = found
        single = one

        let s = song
        // 곡을 못 알아낸 긴 영상(타임라인 없는 플리·라이브 …)은 제목으로 가사를 찾지 않는다
        if songs == .none {
            if lyrics[s.name] == nil { lyrics[s.name] = .note("타임라인을 못 찾았어요") }
            return
        }
        want(s.name, author: single?.author, length: s.length)
        // 플리의 나머지 곡 가사도 지금 곡 다음부터 차례로 미리 찾아 둔다(곡이 바뀌거나 건너뛰어도 바로 나오게).
        // 요청 제한을 피하려고 한 번에 한 곡, 1초씩 쉬어 가며.
        guard loading.isEmpty, Date().timeIntervalSince(lastFetch) > 1, !tracks.isEmpty else { return }
        let i = tracks.lastIndex { $0.start <= t } ?? 0
        if let k = (Array(i + 1..<tracks.count) + Array(0..<i)).first(where: { lyrics[tracks[$0].name] == nil }) {
            let end = k + 1 < tracks.count ? tracks[k + 1].start : v.duration
            want(tracks[k].name, author: nil, length: end - tracks[k].start)
        }
    }

    /// 그 곡 가사를 아직 안 찾았으면 찾기 시작
    func want(_ name: String, author: String?, length: Double) {
        guard lyrics[name] == nil else { return }
        // "광고 스킵", "intro" 같은 타임라인 메모는 곡이 아니다
        if name.range(of: #"(?i)^\s*(광고|ad\b|intro|인트로|outro|아웃트로)"#, options: .regularExpression) != nil { lyrics[name] = .note("♪"); return }
        lyrics[name] = .note("가사 찾는 중…")
        loading.insert(name)
        Task {
            let l = await fetchLyrics(name, author: author, length: length)
            await MainActor.run {
                self.loading.remove(name)
                self.lastFetch = Date()
                if l?.note == false { self.dirty = true }
                // 서버에 못 닿았으면 30초 뒤 다시, 세 번까지(특정 검색어에만 계속 503을 주는 경우가 있다)
                let n = l == nil ? self.tries[name, default: 0] + 1 : 0
                self.tries[name] = n
                self.lyrics[name] = l ?? .note(n < 3 ? "가사 서버에 연결 못 함, 30초 뒤 다시" : "가사를 못 찾았어요")
                if l == nil && n < 3 { DispatchQueue.main.asyncAfter(deadline: .now() + 30) { self.lyrics[name] = nil } }
            }
        }
    }

    /// 새로 찾은 가사·타임라인을 디스크에(안내 문구는 빼고)
    func save() {
        guard dirty else { return }
        dirty = false
        Store.save(lyrics.filter { !$0.value.note }, "lyrics.json")
        Store.save(info, "info.json")
    }

    /// 띄워 둔 영상의 탭에서 유튜브 플레이어 버튼을 누른다
    func press(_ action: String) {
        guard let tab else { return }
        chromeQueue.async {
            _ = try? Chrome.run(action, in: tab)
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) { self.poll() }
        }
    }

    /// 초당 8번 탭 안의 소리 크기를 읽어 막대 높이로(다른 스레드에서). 막대는 접혀 있을 때만 보이니 그때만.
    func listen() {
        guard let tab, video?.playing == true, !expanded, !isLocked() else { levels = nil; return }  // 잠겨 있으면 노치가 안 보인다
        guard !busy.listen else { return }
        busy.listen = true
        meterQueue.async {
            let x = ((try? Chrome.run(meterJS, in: tab)) ?? "").split(separator: ",").compactMap { Double($0) }
            DispatchQueue.main.async { self.busy.listen = false; self.meter(x) }
        }
    }

    func meter(_ x: [Double]) {
        guard x.count == 4 else { levels = nil; return }
        guard x.contains(where: { $0 > 0 }) else { levels = [0, 0, 0, 0]; return }  // 무음이거나 분석기 준비 중
        // 막대마다 최근 소리 범위에 맞춰 늘린다(조용한 곡도 꽉 차게): 범위는 바로 넓히고 6초쯤에 걸쳐 좁힌다
        levels = x.indices.map { k in
            lo[k] = x[k] < lo[k] ? x[k] : lo[k] + (x[k] - lo[k]) * 0.02
            hi[k] = x[k] > hi[k] ? x[k] : hi[k] + (x[k] - hi[k]) * 0.02
            return (x[k] - lo[k]) / max(hi[k] - lo[k], 25)
        }
    }

    /// 지금 부르는 가사 한 줄. 가사가 없거나 안내 문구뿐이면 nil
    var line: String? {
        let s = song
        guard let l = lyrics[s.name], !l.note else { return nil }
        let i = lineIndex(l, at: t - s.start, length: s.length)
        return l.lines.indices.contains(i) && !l.lines[i].isEmpty ? l.lines[i] : "♪"
    }

    /// '지금 재생 중'(잠금 화면·제어 센터·미디어 키)에 곡을 알린다. 가수 자리에 지금 가사 줄을 넣어
    /// 잠금 화면에서도 가사가 한 줄씩 넘어간다. 바뀐 게 있을 때만(아니면 5초마다 시간 맞추러) 보낸다.
    func publish() {
        let c = MPNowPlayingInfoCenter.default()
        guard let v = video else {
            if c.playbackState != .stopped { c.nowPlayingInfo = nil; c.playbackState = .stopped; published.key = "" }
            return
        }
        let s = song, sub = line ?? v.title
        let key = "\(s.name)|\(sub)|\(v.playing)|\(artwork[v.id] != nil)"
        guard key != published.key || Date().timeIntervalSince(published.at) > 5 else { return }
        published = (key, Date())
        var info: [String: Any] = [MPMediaItemPropertyTitle: s.name, MPMediaItemPropertyArtist: sub, MPMediaItemPropertyAlbumTitle: v.title,
                                   MPMediaItemPropertyPlaybackDuration: s.length, MPNowPlayingInfoPropertyElapsedPlaybackTime: t - s.start,
                                   MPNowPlayingInfoPropertyPlaybackRate: v.playing ? 1.0 : 0.0]
        info[MPMediaItemPropertyArtwork] = artwork[v.id]
        c.nowPlayingInfo = info
        c.playbackState = v.playing ? .playing : .paused
    }
}

let playPause = "document.querySelector('#movie_player .ytp-play-button').click()"
let nextVideo = "document.querySelector('#movie_player .ytp-next-button').click()"
// 재생목록이 아니면 '이전' 버튼이 숨겨져 있으니 브라우저 뒤로 가기
let prevVideo = "(b => b && getComputedStyle(b).display !== 'none' ? b.click() : history.back())(document.querySelector('#movie_player .ytp-prev-button'))"

// MARK: 노치

// ponytail: 화면은 실행할 때 한 번만 고른다. 모니터를 꽂고 빼면 다시 실행.
let screen = NSScreen.screens.first { $0.safeAreaInsets.top > 0 } ?? NSScreen.main!
let hasNotch = screen.safeAreaInsets.top > 0
let notch = CGSize(width: hasNotch ? screen.frame.width - (screen.auxiliaryTopLeftArea?.width ?? 0) - (screen.auxiliaryTopRightArea?.width ?? 0) : 0,
                   height: hasNotch ? screen.safeAreaInsets.top : NSStatusBar.system.thickness)
let wing = notch.height + 10  // 노치 양옆으로 삐져나오는 폭(썸네일·막대 한 칸)
let small = CGSize(width: notch.width + wing * 2, height: notch.height)
let big = CGSize(width: max(small.width, 380), height: notch.height + 300)
let compact = CGSize(width: max(small.width, 330), height: notch.height + 50)  // 곡 없이 안내 한 줄

func clock(_ s: Double) -> String { let s = max(0, Int(s)); return String(format: "%d:%02d", s / 60, s % 60) }

/// 탭에서 읽은 소리 크기(저음 → 고음)를 따라 튀는 막대. 소리를 못 읽으면 재생 중일 때 잔잔하게 일렁인다.
struct Bars: View {
    var levels: [Double]?
    var playing: Bool
    func height(_ i: Int, _ t: Double) -> CGFloat {
        if let levels { return 3 + 11 * levels[i] }
        return playing ? 6 + 4 * sin(t * (1.3 + Double(i) * 0.4) + Double(i) * 1.7) : 3
    }

    var body: some View {
        TimelineView(.animation(paused: !playing || levels != nil)) { c in
            let t = c.date.timeIntervalSinceReferenceDate
            HStack(spacing: 2) {
                ForEach(0..<4, id: \.self) { i in Capsule().frame(width: 3, height: height(i, t)) }
            }
            .frame(height: 14)
            .animation(.easeOut(duration: 0.12), value: levels)
        }
    }
}

struct NotchView: View {
    @ObservedObject var m: Model

    var body: some View {
        TimelineView(.periodic(from: .now, by: 0.25)) { _ in
            let size = m.size
            Group { if !m.expanded { closed } else if m.video != nil { open } else { notice } }
                .frame(width: size.width, height: size.height, alignment: .top)
                .background(.black)
                .clipShape(UnevenRoundedRectangle(bottomLeadingRadius: m.expanded ? 24 : 10, bottomTrailingRadius: m.expanded ? 24 : 10))
                .opacity(m.visible ? 1 : 0)
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
                .animation(.spring(response: 0.35, dampingFraction: 0.8), value: m.expanded)
                .animation(.spring(response: 0.45, dampingFraction: 0.85), value: m.visible)  // 노치에서 스르륵 나오고 접혀 들어간다
                .animation(.easeInOut(duration: 0.3), value: m.video == nil)  // 안내 ↔ 곡 바뀔 때 겹쳐서
                .contextMenu {  // 설치형 앱이라 끌 곳이 있어야 한다(펼쳤을 때만 마우스를 받으니 펼친 채 우클릭)
                    Toggle("로그인할 때 자동으로 열기", isOn: Binding(
                        get: { SMAppService.mainApp.status == .enabled },
                        set: { on in try? on ? SMAppService.mainApp.register() : SMAppService.mainApp.unregister() }))
                    Divider()
                    Button("유튜브 노치 종료") { NSApp.terminate(nil) }
                }
        }
        .foregroundStyle(.white)
    }

    /// 받아 둔 썸네일(다시 불러오지 않아 회색 칸이 번쩍이지 않는다). 영상이 바뀌면 겹쳐 바뀐다.
    func thumbnail(_ w: CGFloat, _ h: CGFloat, _ r: CGFloat) -> some View {
        let img = m.video.flatMap { m.thumbs[$0.id] }
        return ZStack {
            Color.white.opacity(0.12)
            if let img { Image(nsImage: img).resizable().scaledToFill().transition(.opacity) }
        }
        .frame(width: w, height: h)
        .clipShape(RoundedRectangle(cornerRadius: r))
        .animation(.easeInOut(duration: 0.35), value: img)
    }

    var closed: some View {
        HStack(spacing: 0) {
            // 곡 없이 연결만 안 됐을 땐 흐린 음표와 멈춘 막대: 여기에 마우스를 올리면 된다는 표시
            Group {
                if m.video == nil { Image(systemName: "music.note").font(.system(size: 12, weight: .semibold)).opacity(0.4) }
                else { thumbnail(notch.height - 8, notch.height - 8, 5) }
            }
            .frame(width: wing)
            Spacer(minLength: notch.width)
            Bars(levels: m.levels, playing: m.video?.playing == true).opacity(m.video == nil ? 0.3 : 0.7).frame(width: wing)
        }
        .frame(height: notch.height)
    }

    var open: some View {
        VStack(spacing: 10) {
            if let v = m.video {
                thumbnail(160, 90, 10)
                VStack(spacing: 3) {
                    Text(m.song.name).font(.system(size: 14, weight: .bold))
                    Text((v.ad ? "광고 중 · " : "") + v.title).font(.system(size: 10)).opacity(0.5)
                }
                .lineLimit(1)
                progress
                LyricsView(m: m)
                controls(v)
            }
        }
        .padding(.top, notch.height + 10)
        .padding(.horizontal, 22)
        .padding(.bottom, 14)
    }

    /// 곡 없이 연결만 안 됐을 때: 작은 안내
    var notice: some View {
        Text(m.problem ?? "").font(.system(size: 11)).multilineTextAlignment(.center).opacity(0.8)
            .frame(maxWidth: .infinity).padding(.top, notch.height + 6)
    }

    /// 지금 곡 안에서의 진행 바와 시간
    var progress: some View {
        let s = m.song, now = m.t - s.start
        return HStack(spacing: 8) {
            Text(clock(now))
            GeometryReader { g in
                Capsule().fill(.white.opacity(0.2))
                    .overlay(alignment: .leading) { Capsule().frame(width: g.size.width * min(1, max(0, now / max(s.length, 1)))) }
            }
            .frame(height: 3)
            Text(clock(s.length))
        }
        .font(.system(size: 10).monospacedDigit())
        .foregroundStyle(.white.opacity(0.6))
    }

    func controls(_ v: Video) -> some View {
        HStack(spacing: 44) {
            Button { m.press(prevVideo) } label: { Image(systemName: "backward.fill") }
            Button { m.press(playPause) } label: { Image(systemName: v.playing ? "pause.fill" : "play.fill").frame(width: 24) }
            Button { m.press(nextVideo) } label: { Image(systemName: "forward.fill") }
        }
        .font(.system(size: 20))
        .buttonStyle(.plain)
    }
}

// MARK: 잠금 화면

let card = CGSize(width: 440, height: 250)
let cardTop: CGFloat = 0.30  // 화면 위에서 카드 윗변까지(화면 높이 비율). 시계와 겹치면 키우고, 비밀번호 칸과 겹치면 줄이기

/// 잠금 화면 시계 아래 카드: 썸네일·곡·진행 바·가사 세 줄·재생 버튼
struct LockCard: View {
    @ObservedObject var m: Model
    var body: some View {
        let n = NotchView(m: m)
        TimelineView(.periodic(from: .now, by: 0.25)) { _ in
            if let v = m.video {
                VStack(spacing: 12) {
                    HStack(spacing: 12) {
                        n.thumbnail(96, 54, 8)
                        VStack(alignment: .leading, spacing: 3) {
                            Text(m.song.name).font(.system(size: 15, weight: .bold))
                            Text((v.ad ? "광고 중 · " : "") + v.title).font(.system(size: 11)).opacity(0.6)
                        }
                        .lineLimit(1)
                        Spacer(minLength: 0)
                    }
                    n.progress
                    LyricsView(m: m)
                    n.controls(v)
                }
                .padding(18)
                .background(.black.opacity(0.35), in: RoundedRectangle(cornerRadius: 22))  // 밝은 배경화면 위에서도 글자가 읽히게
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
            }
        }
        .foregroundStyle(.white)
    }
}

/// 키 창·메인 창이 절대 되지 않는 패널: 잠금 화면에서도 키보드 입력(비밀번호)은 받지 않고 클릭만 받는다
final class Card: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

/// 잠금 화면 위에도 보이게: 비공개 SkyLight API로 잠금 화면(층 300)보다 위인 층 400에 공간을 만들어
/// 카드를 옮긴다(Lakr233/SkyLightWindow, MIT와 같은 방식).
/// ponytail: 비공개 API라 macOS가 바뀌면 깨질 수 있다. 함수를 못 찾으면 카드만 안 뜨고 나머지는 그대로.
struct LockScreen {
    typealias Create = @convention(c) (Int32, Int32, Int32) -> Int32
    typealias SetLevel = @convention(c) (Int32, Int32, Int32) -> Int32
    typealias Show = @convention(c) (Int32, CFArray) -> Int32
    typealias Add = @convention(c) (Int32, Int32, CFArray, Int32) -> Int32
    let cid: Int32, space: Int32, show: Show, add: Add

    init?() {
        let h = dlopen("/System/Library/PrivateFrameworks/SkyLight.framework/Versions/A/SkyLight", RTLD_NOW)
        guard let main = dlsym(h, "SLSMainConnectionID"), let create = dlsym(h, "SLSSpaceCreate"), let level = dlsym(h, "SLSSpaceSetAbsoluteLevel"),
              let show = dlsym(h, "SLSShowSpaces"), let add = dlsym(h, "SLSSpaceAddWindowsAndRemoveFromSpaces") else { return nil }
        cid = unsafeBitCast(main, to: (@convention(c) () -> Int32).self)()
        space = unsafeBitCast(create, to: Create.self)(cid, 1, 0)
        _ = unsafeBitCast(level, to: SetLevel.self)(cid, space, 400)
        self.show = unsafeBitCast(show, to: Show.self)
        self.add = unsafeBitCast(add, to: Add.self)
    }

    /// 공간을 띄우고 창을 넣는다. 화면이 잠들었다 깨면 macOS가 공간을 다시 짜서 빠질 수 있으니 여러 번 불러도 된다.
    @discardableResult func attach(_ w: NSWindow) -> (Int32, Int32) {
        (show(cid, [space] as CFArray), add(cid, space, [w.windowNumber] as CFArray, 7))
    }
}

func isLocked() -> Bool { (CGSessionCopyCurrentDictionary() as? [String: Any])?["CGSSessionScreenIsLocked"] as? Bool ?? false }

/// 가사 전체를 세 줄 창으로 본다. 한 줄씩 넘어갈 때만 위로 미끄러지고(Apple Music처럼),
/// 탐색·건너뛰기로 크게 뛰면 수십 줄을 훑지 않고 그 자리로 바로 간다(LiveLyrics 방식).
struct LyricsView: View {
    @ObservedObject var m: Model
    @State private var shown = 0  // 직전에 보여 준 줄

    var body: some View {
        let s = m.song, l = m.lyrics[s.name] ?? Lyrics()
        let rows = ["♪"] + l.lines.map { $0.isEmpty ? "♪" : $0 }  // 맨 앞 ♪ = 첫 줄 전 전주
        let i = lineIndex(l, at: m.t - s.start, length: s.length) + 1, rowH: CGFloat = 22
        VStack(spacing: 0) {
            ForEach(rows.indices, id: \.self) { j in
                Text(rows[j])
                    .font(.system(size: 15, weight: .semibold))
                    .lineLimit(1)
                    .scaleEffect(j == i ? 1 : 0.82)
                    .opacity(j == i ? 1 : abs(j - i) == 1 ? 0.4 : 0)
                    .frame(maxWidth: .infinity, minHeight: rowH, maxHeight: rowH)
            }
        }
        .offset(y: CGFloat(1 - i) * rowH)  // i번째 줄이 가운데 칸에
        .frame(height: rowH * 3, alignment: .top)
        .clipped()
        .animation(i == shown + 1 ? .spring(response: 0.5, dampingFraction: 0.9) : nil, value: i)
        .onAppear { shown = i }
        .onChange(of: i) { _, new in shown = new }
        .id("\(s.name)#\(rows.count)")  // 곡이 바뀌거나 가사가 막 도착하면 그 자리에서 시작
    }
}

/// 다른 앱을 쓰는 중에도 버튼이 첫 클릭에 눌리게
final class Host<V: View>: NSHostingView<V> {
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
}

let app = NSApplication.shared
app.setActivationPolicy(.accessory)
let model = Model()

let panel = NSPanel(contentRect: NSRect(x: screen.frame.midX - big.width / 2, y: screen.frame.maxY - big.height, width: big.width, height: big.height),
                    styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
panel.level = .statusBar  // 메뉴 막대 위
panel.backgroundColor = .clear
panel.isOpaque = false
panel.hasShadow = false
panel.ignoresMouseEvents = true  // 접혀 있을 땐 클릭을 아래로 통과시키고, 마우스는 위치만 본다
panel.collectionBehavior = [.canJoinAllSpaces, .stationary, .fullScreenAuxiliary, .ignoresCycle]
let host = Host(rootView: NotchView(m: model))
host.sizingOptions = []
panel.contentView = host
panel.orderFrontRegardless()

// F7·F8·F9 미디어 키: 이 앱이 '지금 재생 중' 앱으로 나서서 받고, 띄워 둔 유튜브 탭의 버튼을 대신 누른다
let remote = MPRemoteCommandCenter.shared()
remote.togglePlayPauseCommand.addTarget { _ in model.press(playPause); return .success }
remote.playCommand.addTarget { _ in if model.video?.playing == false { model.press(playPause) }; return .success }
remote.pauseCommand.addTarget { _ in if model.video?.playing == true { model.press(playPause) }; return .success }
remote.nextTrackCommand.addTarget { _ in model.press(nextVideo); return .success }
remote.previousTrackCommand.addTarget { _ in model.press(prevVideo); return .success }

// 잠금 화면 카드: 잠겨 있을 때만 띄운다
let cardPanel = Card(contentRect: NSRect(x: screen.frame.midX - card.width / 2, y: screen.frame.maxY - screen.frame.height * cardTop - card.height,
                                         width: card.width, height: card.height),
                     styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
cardPanel.backgroundColor = .clear
cardPanel.isOpaque = false
cardPanel.hasShadow = false
cardPanel.canBecomeVisibleWithoutLogin = true
cardPanel.level = NSWindow.Level(rawValue: Int(Int32.max - 2))
cardPanel.collectionBehavior = [.canJoinAllSpaces, .stationary, .fullScreenAuxiliary, .ignoresCycle]
let cardHost = Host(rootView: LockCard(m: model))
cardHost.sizingOptions = []
cardPanel.contentView = cardHost
let lockScreen = LockScreen()

/// 잠겼으면 카드를 띄워 잠금 화면 위 공간에 넣고(깨어난 뒤 빠졌을 수 있어 매번), 풀렸으면 숨긴다
func syncCard() {
    if isLocked() {
        if !cardPanel.isVisible { cardPanel.orderFrontRegardless() }
        lockScreen?.attach(cardPanel)
    } else if cardPanel.isVisible {
        cardPanel.orderOut(nil)
    }
}
// 잠김·풀림·깨어남 알림은 잠금 화면이 다 그려지기 전에 오기도 해서 조금씩 늦춰 몇 번 더, 그리고 2초마다 맞춘다
func syncSoon() { for d in [0, 0.5, 1.5, 3] { DispatchQueue.main.asyncAfter(deadline: .now() + d) { syncCard() } } }
for name in ["com.apple.screenIsLocked", "com.apple.screenIsUnlocked"] {
    DistributedNotificationCenter.default().addObserver(forName: .init(name), object: nil, queue: .main) { _ in syncSoon() }
}
for n in [NSWorkspace.screensDidWakeNotification, NSWorkspace.didWakeNotification] {
    NSWorkspace.shared.notificationCenter.addObserver(forName: n, object: nil, queue: .main) { _ in syncSoon() }
}
Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { _ in syncCard() }

model.poll()
// 화면이 잠기거나 노치가 가려져도 macOS가 타이머를 늦추지(App Nap) 않게. 잠자기는 막지 않는다.
let activity = ProcessInfo.processInfo.beginActivity(options: .userInitiatedAllowingIdleSystemSleep, reason: "잠금 화면 가사 갱신")

Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { _ in model.poll() }  // 다른 스레드에서 읽으니 자주 봐도 화면은 안 멈춘다
Timer.scheduledTimer(withTimeInterval: 5, repeats: true) { _ in model.save() }
Timer.scheduledTimer(withTimeInterval: 0.25, repeats: true) { _ in model.publish() }
Timer.scheduledTimer(withTimeInterval: 0.125, repeats: true) { _ in model.listen() }
Timer.scheduledTimer(withTimeInterval: 0.1, repeats: true) { _ in
    let s = model.size
    let r = NSRect(x: screen.frame.midX - s.width / 2, y: screen.frame.maxY - s.height, width: s.width, height: s.height + 1)
    let hover = model.visible && r.contains(NSEvent.mouseLocation)
    if hover != model.expanded { model.expanded = hover }
    panel.ignoresMouseEvents = !hover  // 펼쳐졌을 때만 버튼을 누를 수 있게
}
app.run()

// YouTube Lyric Notch for Windows: 브라우저(Chrome·Edge·Firefox·웨일 …)에서 틀어 둔 유튜브 노래의 곡과 싱크 가사를 띄운다.
// Windows에는 노치가 없어서, 화면 위 가운데 작은 '아일랜드'가 그 자리를 대신한다(접혀 있으면 클릭이 통과하고,
// 마우스를 잠깐 올려 두면 펼쳐짐). 아니면 화면에 떠 있는 가사 창. 트레이 아이콘에서 설정·종료.
// 곡·재생 위치는 Windows 미디어 세션(SMTC)에서 읽는다(브라우저 설정 필요 없음). 영상 id는 제목·채널로 찾고,
// 그다음(설명란·댓글 타임라인 → 곡 → 가사)은 Core.cs, macOS 판과 같은 규칙.
// 빌드: dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using Windows.Media.Control;
using Forms = System.Windows.Forms;

namespace LyricNotch;

/// 설정(%LOCALAPPDATA%\YouTubeLyricNotch\settings.json)
public class Prefs
{
    public string Mode { get; set; } = "island";   // "island" = 화면 위 아일랜드 / "float" = 떠 있는 가사 창
    public double Opacity { get; set; } = 0.3;      // 떠 있는 가사 창 배경 진하기
    public double Delay { get; set; } = 0.5;        // 가사 타이밍: 플리 타임라인이 실제 노래보다 조금 이른 경우가 많아(측정 0.55초) 기본 0.5초 늦게
    public double? FloatX { get; set; }
    public double? FloatY { get; set; }
    public static Prefs Now = Store.Load<Prefs>("settings.json") ?? new Prefs();
    public static void Save() => Store.Save(Now, "settings.json");
}

/// 지금 재생 중인 유튜브 곡과 가사
public class Model
{
    public string Title = "", Channel = "";
    public string? Id;                 // 음악으로 판별된 영상만
    public bool Playing;
    public double Position, Duration;  // 미디어 세션이 마지막으로 알려 준 위치(초)와 그 시각
    public DateTimeOffset UpdatedAt = DateTimeOffset.Now;
    public List<Track> Tracks = new();
    public (string Name, string? Author)? Single;
    public GlobalSystemMediaTransportControlsSession? Session;
    public Dictionary<string, Lyrics> LyricsByName = Store.Load<Dictionary<string, Lyrics>>("lyrics.json") ?? new();
    public Dictionary<string, Info> InfoById = Store.Load<Dictionary<string, Info>>("info.json") ?? new();
    public Dictionary<string, string> IdByTitle = Store.Load<Dictionary<string, string>>("ids.json") ?? new();
    readonly HashSet<string> asked = new(), loading = new();
    readonly Dictionary<string, int> tries = new();
    DateTime lastFetch = DateTime.MinValue;
    bool dirty, busy;
    GlobalSystemMediaTransportControlsSessionManager? manager;

    public bool Visible => Id != null;

    /// 영상 시간: 미디어 세션의 위치 + 그 뒤로 흐른 시간(재생 중일 때)
    public double T
    {
        get
        {
            var t = Position + (Playing ? Math.Max(0, (DateTimeOffset.Now - UpdatedAt).TotalSeconds) : 0);
            return Duration > 0 ? Math.Min(t, Duration) : t;
        }
    }

    public (string Name, double Start, double Length) Song
    {
        get
        {
            if (Tracks.Count == 0) return (Single?.Name ?? Title, 0, Duration);
            var t = T;
            var i = Math.Max(0, Tracks.FindLastIndex(x => x.Start <= t));
            var end = i + 1 < Tracks.Count ? Tracks[i + 1].Start : Duration;
            return (Tracks[i].Name, Tracks[i].Start, end - Tracks[i].Start);
        }
    }

    static bool IsBrowser(string aumid)
    {
        var a = aumid.ToLowerInvariant();
        return new[] { "chrome", "msedge", "edge", "firefox", "308046b0af4a39cb", "brave", "opera", "whale", "vivaldi" }.Any(a.Contains);
    }

    /// 0.5초마다: 미디어 세션에서 브라우저 재생 정보를 읽고, 새 영상이면 id·설명란·댓글을 받아 곡과 가사를 정한다
    public async Task Poll()
    {
        if (busy) return;
        busy = true;
        try
        {
            manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var sessions = manager.GetSessions().Where(s => IsBrowser(s.SourceAppUserModelId)).ToList();
            var session = sessions.FirstOrDefault(s => s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                          ?? sessions.FirstOrDefault();
            if (session == null) { Id = null; return; }
            var props = await session.TryGetMediaPropertiesAsync();
            var tl = session.GetTimelineProperties();
            Session = session;
            Playing = session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            Position = tl.Position.TotalSeconds;
            // 위치를 알려 준 시각. 브라우저가 이상한 값(1601년 등)을 주면 지금으로
            UpdatedAt = tl.LastUpdatedTime.Year > 2000 && tl.LastUpdatedTime <= DateTimeOffset.Now ? tl.LastUpdatedTime : DateTimeOffset.Now;
            Duration = (tl.EndTime - tl.StartTime).TotalSeconds;
            if (props.Title != Title || props.Artist != Channel) { Title = props.Title ?? ""; Channel = props.Artist ?? ""; Id = null; }
            await Resolve();
        }
        catch { /* 미디어 세션이 잠깐 사라지거나 바뀌는 중: 다음 번에 다시 */ }
        finally { busy = false; }
    }

    /// 제목 → 영상 id → 설명란·댓글·분류 → 음악이면 곡과 가사
    async Task Resolve()
    {
        if (Title.Length == 0) { Id = null; return; }
        var key = Title + "\n" + Channel;
        if (!IdByTitle.TryGetValue(key, out var id))
        {
            if (!asked.Add("t:" + key)) return;
            id = await Task.Run(() => Core.FindVideo(Title, Channel));
            if (id == null) { _ = Forget("t:" + key); return; }  // 광고 등 유튜브 영상이 아님
            IdByTitle[key] = id; dirty = true;
        }
        if (asked.Add(id))
        {
            var fresh = await Task.Run(() => Core.FetchInfo(id));
            if (fresh != null) { InfoById[id] = fresh; dirty = true; } else _ = Forget(id);
        }
        if (!InfoById.TryGetValue(id, out var meta)) return;
        var songs = Core.Identify(Title, meta.Desc, meta.Comments, meta.Author, Duration);
        // 음악만: 분류가 음악이거나, 제목이 플리이고 타임라인이 있으면
        if (!Core.IsMusic(Title, meta, songs)) { Id = null; return; }
        Id = id;
        Tracks = songs.List ?? new();
        Single = songs.One != null ? (songs.One, songs.Author) : null;
        var s = Song;
        if (songs.None) { if (!LyricsByName.ContainsKey(s.Name)) LyricsByName[s.Name] = Lyrics.MakeNote("타임라인을 못 찾았어요"); return; }
        Want(s.Name, Single?.Author, s.Length);
        // 플리의 나머지 곡 가사도 지금 곡 다음부터 차례로 미리 찾아 둔다(요청 제한을 피하려고 한 번에 한 곡, 1초씩 쉬어 가며)
        if (loading.Count > 0 || (DateTime.Now - lastFetch).TotalSeconds < 1 || Tracks.Count == 0) return;
        var i = Math.Max(0, Tracks.FindLastIndex(x => x.Start <= T));
        foreach (var k in Enumerable.Range(i + 1, Tracks.Count - i - 1).Concat(Enumerable.Range(0, i)))
        {
            if (LyricsByName.ContainsKey(Tracks[k].Name)) continue;
            var end = k + 1 < Tracks.Count ? Tracks[k + 1].Start : Duration;
            Want(Tracks[k].Name, null, end - Tracks[k].Start);
            break;
        }
    }

    async Task Forget(string key) { await Task.Delay(30_000); asked.Remove(key); }

    void Want(string name, string? author, double length)
    {
        if (LyricsByName.ContainsKey(name)) return;
        // "광고 스킵", "intro" 같은 타임라인 메모는 곡이 아니다
        if (System.Text.RegularExpressions.Regex.IsMatch(name, @"(?i)^\s*(광고|ad\b|intro|인트로|outro|아웃트로)")) { LyricsByName[name] = Lyrics.MakeNote("♪"); return; }
        LyricsByName[name] = Lyrics.MakeNote("가사 찾는 중…");
        loading.Add(name);
        _ = Task.Run(() => Core.FetchLyrics(name, author, length)).ContinueWith(task =>
        {
            var l = task.Result;
            loading.Remove(name);
            lastFetch = DateTime.Now;
            // 서버에 못 닿았으면 30초 뒤 다시, 세 번까지
            var n = l == null ? tries.GetValueOrDefault(name) + 1 : 0;
            tries[name] = n;
            LyricsByName[name] = l ?? Lyrics.MakeNote(n < 3 ? "가사 서버에 연결 못 함, 30초 뒤 다시" : "가사를 못 찾았어요");
            if (l is { Note: false }) dirty = true;
            if (l == null && n < 3) _ = Task.Delay(30_000).ContinueWith(_ => LyricsByName.Remove(name), TaskScheduler.FromCurrentSynchronizationContext());
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    public void Save()
    {
        if (!dirty) return;
        dirty = false;
        Store.Save(LyricsByName.Where(kv => !kv.Value.Note).ToDictionary(kv => kv.Key, kv => kv.Value), "lyrics.json");
        Store.Save(InfoById, "info.json");
        Store.Save(IdByTitle, "ids.json");
    }

    public void Press(string what)
    {
        _ = what switch
        {
            "prev" => Session?.TrySkipPreviousAsync().AsTask(),
            "next" => Session?.TrySkipNextAsync().AsTask(),
            _ => Session?.TryTogglePlayPauseAsync().AsTask(),
        };
    }
}

/// 브라우저 소리 크기(Windows 오디오 세션의 음량 미터): 막대가 실제 소리를 따라 움직이게
public class Meter
{
    MMDevice? device;
    DateTime refreshed = DateTime.MinValue;
    double lo = 1, hi = 0;
    static readonly string[] Browsers = { "chrome", "msedge", "firefox", "brave", "opera", "whale", "vivaldi" };

    /// 0~1. 최근 소리 범위에 맞춰 늘린다(조용한 곡도 꽉 차게): 범위는 바로 넓히고 천천히 좁힌다
    public double Level()
    {
        try
        {
            if (device == null || (DateTime.Now - refreshed).TotalSeconds > 3)
            {
                device = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                device.AudioSessionManager.RefreshSessions();
                refreshed = DateTime.Now;
            }
            double peak = 0;
            var sessions = device.AudioSessionManager.Sessions;
            for (var i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                string name;
                try { name = Process.GetProcessById((int)s.GetProcessID).ProcessName.ToLowerInvariant(); } catch { continue; }
                if (Browsers.Any(name.Contains)) peak = Math.Max(peak, s.AudioMeterInformation.MasterPeakValue);
            }
            if (peak <= 0) return 0;
            lo = peak < lo ? peak : lo + (peak - lo) * 0.02;
            hi = peak > hi ? peak : hi + (peak - hi) * 0.02;
            return Math.Clamp((peak - lo) / Math.Max(hi - lo, 0.1), 0, 1);
        }
        catch { device = null; return 0; }
    }
}

/// 가사 세 줄 창: 한 줄씩 넘어갈 때만 위로 미끄러지고, 탐색·건너뛰기로 크게 뛰면 그 자리로 바로 간다
public class LyricsPanel : Canvas
{
    const double RowH = 24;
    readonly StackPanel stack = new();
    readonly TranslateTransform shift = new();
    string key = "";
    int shown = -1;

    public LyricsPanel(double width)
    {
        Width = width; Height = RowH * 3; ClipToBounds = true;
        stack.Width = width; stack.RenderTransform = shift;
        Children.Add(stack);
    }

    public void Update(Lyrics? l, string song, double t, double length)
    {
        l ??= new Lyrics();
        var k = $"{song}#{l.Lines.Count}#{l.Note}";
        if (k != key)  // 곡이 바뀌거나 가사가 막 도착: 줄을 새로 깔고 그 자리에서 시작
        {
            key = k; shown = -1;
            stack.Children.Clear();
            foreach (var line in new[] { "♪" }.Concat(l.Lines.Select(x => x.Length == 0 ? "♪" : x)))
                stack.Children.Add(new TextBlock
                {
                    Text = line, Height = RowH, FontSize = 15, Foreground = Brushes.White, TextAlignment = TextAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
                });
        }
        var i = Core.LineIndex(l, t, length) + 1;
        if (i == shown) return;
        var y = (1 - i) * RowH;  // i번째 줄이 가운데 칸에
        if (shown >= 0 && i == shown + 1)
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, TimeSpan.FromSeconds(0.45)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        else { shift.BeginAnimation(TranslateTransform.YProperty, null); shift.Y = y; }
        for (var j = 0; j < stack.Children.Count; j++)
        {
            var row = (TextBlock)stack.Children[j];
            row.FontWeight = j == i ? FontWeights.SemiBold : FontWeights.Normal;
            row.BeginAnimation(OpacityProperty, new DoubleAnimation(j == i ? 1 : Math.Abs(j - i) == 1 ? 0.4 : 0, TimeSpan.FromSeconds(0.3)));
        }
        shown = i;
    }
}

static class Win
{
    const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);

    /// Alt+Tab에 안 나오고, 눌러도 다른 앱의 포커스를 뺏지 않는 창. clickThrough면 클릭이 아래 창으로 통과한다.
    public static void Style(Window w, bool clickThrough)
    {
        var h = new WindowInteropHelper(w).Handle;
        if (h == IntPtr.Zero) return;
        var s = GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLong(h, GWL_EXSTYLE, clickThrough ? s | WS_EX_TRANSPARENT : s & ~WS_EX_TRANSPARENT);
    }

    public static string Clock(double s) { var n = Math.Max(0, (int)s); return $"{n / 60}:{n % 60:00}"; }

    public static Window Overlay(double w, double h) => new()
    {
        WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true,
        ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize, Width = w, Height = h, ShowActivated = false,
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Malgun Gothic"),
    };

    public static TextBlock Icon(string glyph, double size) => new()
    {
        Text = glyph, FontSize = size, Foreground = Brushes.White, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
    };

    public static Button Plain(UIElement content, Action click)
    {
        var b = new Button { Content = content, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, Padding = new Thickness(6), Focusable = false };
        b.Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
        b.Click += (_, _) => click();
        return b;
    }
}

/// 화면 위 가운데 아일랜드: 접히면 썸네일·곡 이름·막대, 마우스를 잠깐 올려 두면 곡·진행 바·가사·재생 버튼
public class Island
{
    static readonly Size Small = new(320, 38), Big = new(380, 330);
    public readonly Window Window = Win.Overlay(Big.Width + 20, Big.Height + 20);
    readonly Model m;
    readonly Border pill = new() { Background = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, ClipToBounds = true };
    readonly Grid small = new(), big = new();
    readonly Image thumbSmall = new() { Width = 26, Height = 26, Stretch = Stretch.UniformToFill }, thumbBig = new() { Width = 160, Height = 90, Stretch = Stretch.UniformToFill };
    readonly TextBlock nameSmall = new() { Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
    readonly TextBlock nameBig = new() { Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock sub = new() { Foreground = new SolidColorBrush(Color.FromArgb(128, 255, 255, 255)), FontSize = 10, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock now = new() { Foreground = Brushes.Gray, FontSize = 10 }, len = new() { Foreground = Brushes.Gray, FontSize = 10 };
    readonly Border track = new() { Height = 3, CornerRadius = new CornerRadius(1.5), Background = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)), Margin = new Thickness(8, 0, 8, 0) };
    readonly Border fill = new() { Height = 3, CornerRadius = new CornerRadius(1.5), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left };
    readonly TextBlock play = Win.Icon("", 20);
    readonly LyricsPanel lyrics = new(336);
    readonly Rectangle[] bars = Enumerable.Range(0, 4).Select(_ => new Rectangle { Width = 3, Height = 3, RadiusX = 1.5, RadiusY = 1.5, Fill = Brushes.White, Margin = new Thickness(1, 0, 1, 0), VerticalAlignment = VerticalAlignment.Center }).ToArray();
    bool expanded;
    DateTime? hoverSince;

    public Island(Model model, Action openSettings)
    {
        m = model;
        // 접힌 모습
        small.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        small.ColumnDefinitions.Add(new ColumnDefinition());
        small.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tb = new Border { CornerRadius = new CornerRadius(6), ClipToBounds = true, Child = thumbSmall, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        thumbSmall.Clip = new RectangleGeometry(new Rect(0, 0, 26, 26), 6, 6);
        var barPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8 };
        foreach (var b in bars) barPanel.Children.Add(b);
        Grid.SetColumn(nameSmall, 1); Grid.SetColumn(barPanel, 2);
        small.Children.Add(tb); small.Children.Add(nameSmall); small.Children.Add(barPanel);
        small.Height = Small.Height;

        // 펼친 모습
        thumbBig.Clip = new RectangleGeometry(new Rect(0, 0, 160, 90), 10, 10);
        var progress = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        progress.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        progress.ColumnDefinitions.Add(new ColumnDefinition());
        progress.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        track.Child = fill; track.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(track, 1); Grid.SetColumn(len, 2);
        progress.Children.Add(now); progress.Children.Add(track); progress.Children.Add(len);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        controls.Children.Add(Win.Plain(Win.Icon("", 20), () => m.Press("prev")));
        controls.Children.Add(new Border { Width = 28 });
        controls.Children.Add(Win.Plain(play, () => m.Press("toggle")));
        controls.Children.Add(new Border { Width = 28 });
        controls.Children.Add(Win.Plain(Win.Icon("", 20), () => m.Press("next")));
        var column = new StackPanel { Margin = new Thickness(22, 16, 22, 12) };
        thumbBig.HorizontalAlignment = HorizontalAlignment.Center;
        lyrics.Margin = new Thickness(0, 8, 0, 0);
        foreach (var e in new UIElement[] { thumbBig, new Border { Height = 8 }, nameBig, sub, progress, lyrics, controls }) column.Children.Add(e);
        var gear = Win.Plain(Win.Icon("", 14), openSettings);
        gear.HorizontalAlignment = HorizontalAlignment.Left; gear.VerticalAlignment = VerticalAlignment.Top; gear.Margin = new Thickness(10, 8, 0, 0); gear.Opacity = 0.7;
        big.Children.Add(column); big.Children.Add(gear);
        big.Visibility = Visibility.Collapsed;

        var content = new Grid();
        content.Children.Add(small); content.Children.Add(big);
        pill.Child = content;
        pill.Width = Small.Width; pill.Height = Small.Height; pill.CornerRadius = new CornerRadius(Small.Height / 2);
        pill.Opacity = 0;
        Window.Content = new Grid { Children = { pill } };
        var area = SystemParameters.WorkArea;
        Window.Left = area.Left + (area.Width - Window.Width) / 2;
        Window.Top = area.Top + 6;
        Window.SourceInitialized += (_, _) => Win.Style(Window, clickThrough: true);
    }

    void Resize(Size s, double radius)
    {
        var d = new Duration(TimeSpan.FromSeconds(0.32));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        pill.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(s.Width, d) { EasingFunction = ease });
        pill.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation(s.Height, d) { EasingFunction = ease });
        pill.CornerRadius = new CornerRadius(radius);
    }

    /// 0.1초마다: 보일지, 마우스가 올라왔는지(0.25초 머물면 펼침, 벗어나면 접음), 화면 내용
    public void Tick(bool show, double level, double t)
    {
        pill.Opacity = show ? 1 : 0;
        // 마우스 위치(물리 픽셀 → WPF 단위)
        var dpi = VisualTreeHelper.GetDpi(Window);
        var p = Forms.Control.MousePosition;
        var x = p.X / dpi.DpiScaleX - Window.Left;
        var y = p.Y / dpi.DpiScaleY - Window.Top;
        var size = expanded ? Big : Small;
        var left = (Window.Width - size.Width) / 2;
        var inside = show && x >= left && x <= left + size.Width && y >= 0 && y <= size.Height + 4;
        if (inside && !expanded) hoverSince ??= DateTime.Now; else if (!inside) hoverSince = null;
        var want = expanded ? inside : hoverSince != null && (DateTime.Now - hoverSince.Value).TotalSeconds >= 0.25;  // 탭을 쓰다 스치는 건 무시
        if (want != expanded)
        {
            expanded = want;
            small.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
            big.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            Resize(expanded ? Big : Small, expanded ? 22 : Small.Height / 2);
            Win.Style(Window, clickThrough: !expanded);  // 펼쳤을 때만 버튼을 누를 수 있게
        }
        if (!show) return;

        var s = m.Song;
        var thumb = m.Id == null ? null : Thumbs.Get(m.Id);
        thumbSmall.Source = thumb; thumbBig.Source = thumb;
        nameSmall.Text = s.Name; nameBig.Text = s.Name; sub.Text = m.Title;
        var into = t - s.Start;
        now.Text = Win.Clock(into); len.Text = Win.Clock(s.Length);
        fill.Width = Math.Max(0, track.ActualWidth * Math.Clamp(into / Math.Max(s.Length, 1), 0, 1));
        play.Text = m.Playing ? "" : "";
        if (expanded) lyrics.Update(m.LyricsByName.GetValueOrDefault(s.Name), s.Name, into - Prefs.Now.Delay, s.Length);
        // 막대: 브라우저 소리 크기를 따라, 칸마다 조금씩 다르게
        var sec = DateTime.Now.TimeOfDay.TotalSeconds;
        for (var k = 0; k < bars.Length; k++)
        {
            var h = m.Playing ? 3 + 13 * level * (0.55 + 0.45 * Math.Abs(Math.Sin(sec * (5 + k * 1.3) + k * 2))) : 3;
            bars[k].Height = h;
        }
    }
}

/// 화면에 떠 있는 가사 창: 반투명이라 시선을 덜 끌고, 끌어서 옮길 수 있다(위치는 기억)
public class Floating
{
    public readonly Window Window = Win.Overlay(460, 92);
    readonly Border bg = new() { CornerRadius = new CornerRadius(18), Padding = new Thickness(18, 10, 18, 10) };
    readonly LyricsPanel lyrics = new(424);
    readonly Model m;

    public Floating(Model model)
    {
        m = model;
        lyrics.Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 0, Opacity = 0.8 };  // 밝은 화면 위에서도 글자가 읽히게
        bg.Child = lyrics;
        Window.Content = bg;
        var area = SystemParameters.WorkArea;
        Window.Left = Prefs.Now.FloatX ?? area.Left + (area.Width - Window.Width) / 2;
        Window.Top = Prefs.Now.FloatY ?? area.Top + 50;
        Window.MouseLeftButtonDown += (_, _) => Window.DragMove();
        Window.LocationChanged += (_, _) => { Prefs.Now.FloatX = Window.Left; Prefs.Now.FloatY = Window.Top; Prefs.Save(); };
        Window.SourceInitialized += (_, _) => Win.Style(Window, clickThrough: false);
    }

    public void Tick(bool show, double t)
    {
        if (show != Window.IsVisible) { if (show) Window.Show(); else Window.Hide(); }
        if (!show) return;
        bg.Background = new SolidColorBrush(Color.FromArgb((byte)(Prefs.Now.Opacity * 255), 0, 0, 0));
        var s = m.Song;
        lyrics.Update(m.LyricsByName.GetValueOrDefault(s.Name), s.Name, t - s.Start - Prefs.Now.Delay, s.Length);
    }
}

/// 썸네일: 영상마다 한 번만 받아 둔다
static class Thumbs
{
    static readonly Dictionary<string, BitmapImage> cache = new();
    public static BitmapImage Get(string id)
    {
        if (cache.TryGetValue(id, out var b)) return b;
        b = new BitmapImage();
        b.BeginInit(); b.UriSource = new Uri($"https://i.ytimg.com/vi/{id}/mqdefault.jpg"); b.CacheOption = BitmapCacheOption.OnLoad; b.EndInit();
        return cache[id] = b;
    }
}

/// 설정 창(하나만 만들어 두고 다시 띄운다)
public class SettingsWindow : Window
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunName = "YouTubeLyricNotch";

    public SettingsWindow()
    {
        Title = "YouTube Lyric Notch 설정"; Width = 400; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Malgun Gothic");
        var p = Prefs.Now;
        var panel = new StackPanel { Margin = new Thickness(20) };
        TextBlock Label(string s, bool caption = false) => new() { Text = s, Margin = new Thickness(0, caption ? 2 : 14, 0, 4), FontSize = caption ? 11 : 13, Foreground = caption ? Brushes.Gray : Brushes.Black, TextWrapping = TextWrapping.Wrap, FontWeight = caption ? FontWeights.Normal : FontWeights.SemiBold };

        panel.Children.Add(Label("가사 보기"));
        var island = new RadioButton { Content = "화면 위 아일랜드에 마우스를 올려서 보기", IsChecked = p.Mode != "float", Margin = new Thickness(0, 2, 0, 2) };
        var floating = new RadioButton { Content = "화면에 떠 있는 가사 창", IsChecked = p.Mode == "float", Margin = new Thickness(0, 2, 0, 2) };
        panel.Children.Add(island); panel.Children.Add(floating);

        var floatBox = new StackPanel { Visibility = p.Mode == "float" ? Visibility.Visible : Visibility.Collapsed };
        floatBox.Children.Add(Label("배경 진하기"));
        var opacity = new Slider { Minimum = 0, Maximum = 0.8, Value = p.Opacity };
        opacity.ValueChanged += (_, e) => { p.Opacity = e.NewValue; Prefs.Save(); };
        floatBox.Children.Add(opacity);
        floatBox.Children.Add(Label("가사 창은 끌어서 원하는 곳으로 옮길 수 있어요. 0이면 배경 없이 글자만 보여요.", caption: true));
        panel.Children.Add(floatBox);
        island.Checked += (_, _) => { p.Mode = "island"; Prefs.Save(); floatBox.Visibility = Visibility.Collapsed; };
        floating.Checked += (_, _) => { p.Mode = "float"; Prefs.Save(); floatBox.Visibility = Visibility.Visible; };

        var delayLabel = Label($"가사 타이밍 {p.Delay:+0.0;-0.0;0.0}초");
        panel.Children.Add(delayLabel);
        var delay = new Slider { Minimum = -1, Maximum = 2, Value = p.Delay, TickFrequency = 0.1, IsSnapToTickEnabled = true };
        delay.ValueChanged += (_, e) => { p.Delay = Math.Round(e.NewValue, 1); Prefs.Save(); delayLabel.Text = $"가사 타이밍 {p.Delay:+0.0;-0.0;0.0}초"; };
        panel.Children.Add(delay);
        panel.Children.Add(Label("가사가 노래보다 먼저 넘어가면 늘리고, 늦게 넘어가면 줄여 주세요.", caption: true));

        var startup = new CheckBox { Content = "Windows를 시작할 때 자동으로 열기", Margin = new Thickness(0, 16, 0, 0), IsChecked = Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue(RunName) != null };
        startup.Checked += (_, _) => Registry.CurrentUser.CreateSubKey(RunKey)?.SetValue(RunName, $"\"{Environment.ProcessPath}\"");
        startup.Unchecked += (_, _) => Registry.CurrentUser.CreateSubKey(RunKey)?.DeleteValue(RunName, false);
        panel.Children.Add(startup);
        panel.Children.Add(Label("트레이 아이콘이 숨겨져 있어도 앱을 다시 실행하면 이 설정 창이 열려요.", caption: true));

        var quit = new Button { Content = "YouTube Lyric Notch 종료", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 16, 0, 0) };
        quit.Click += (_, _) => Application.Current.Shutdown();
        panel.Children.Add(quit);
        Content = panel;
        Closing += (_, e) => { e.Cancel = true; Hide(); };  // 닫아도 다시 쓸 수 있게 숨기기만
    }
}

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--selftest"))
        {
            try { Selftest.Run(); Store.Save("ok", "selftest.json"); Environment.Exit(0); }
            catch (Exception e) { Store.Save(e.Message, "selftest.json"); Environment.Exit(1); }
        }
        // 한 번만 실행. 이미 실행 중이면 그쪽에 설정 창을 띄우라고 알리고 끝낸다(트레이 아이콘이 숨겨져 있어도 설정을 열 수 있게)
        using var single = new Mutex(true, "YouTubeLyricNotch.Single", out var first);
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, "YouTubeLyricNotch.ShowSettings");
        if (!first) { show.Set(); return; }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var model = new Model();
        var meter = new Meter();
        SettingsWindow? settings = null;
        void OpenSettings() { settings ??= new SettingsWindow(); settings.Show(); settings.Activate(); }
        var island = new Island(model, OpenSettings);
        var floating = new Floating(model);
        island.Window.Show();

        // 트레이: 설정, 종료
        var tray = new Forms.NotifyIcon { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!), Text = "YouTube Lyric Notch", Visible = true };
        tray.ContextMenuStrip = new Forms.ContextMenuStrip();
        tray.ContextMenuStrip.Items.Add("설정…", null, (_, _) => OpenSettings());
        tray.ContextMenuStrip.Items.Add("YouTube Lyric Notch 종료", null, (_, _) => app.Shutdown());
        tray.DoubleClick += (_, _) => OpenSettings();
        app.Exit += (_, _) => { tray.Visible = false; tray.Dispose(); model.Save(); };

        new Thread(() => { while (show.WaitOne()) app.Dispatcher.Invoke(OpenSettings); }) { IsBackground = true }.Start();

        var poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.5) };
        poll.Tick += async (_, _) => await model.Poll();
        poll.Start();
        var tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.1) };
        tick.Tick += (_, _) =>
        {
            var visible = model.Visible;
            var t = model.T;
            island.Tick(visible, visible && model.Playing ? meter.Level() : 0, t);  // 아일랜드는 두 방식 모두에서 보인다(설정 ⚙️을 열 수 있게)
            floating.Tick(visible && Prefs.Now.Mode == "float", t);
        };
        tick.Start();
        var save = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        save.Tick += (_, _) => model.Save();
        save.Start();
        _ = model.Poll();
        app.Run();
    }
}

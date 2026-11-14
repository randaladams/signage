using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace SignagePlayer;

/// <summary>
/// The full-screen slideshow. Same behaviour as the Android/Pi players: offline-first from the saved playlist,
/// a check-in every poll_sec, photos cross-fade, videos play muted, "keep screens in step" from the server clock.
/// Not always-on-top: on an everyday PC, Esc (or Alt+F4) hides it to the tray icon and it keeps syncing.
/// </summary>
sealed class PlayerWindow : Window
{
    private readonly Grid _root = new() { Background = Brushes.Black };
    private readonly Image[] _layers;
    private int _front;
    // Position only moves when a new frame arrives, and a clip can open on a still picture lasting several seconds.
    private readonly DispatcherTimer _videoWatch = new() { Interval = TimeSpan.FromSeconds(12) };
    private readonly TextBlock _message;
    private readonly TextBlock _status;
    private readonly Border _diagPanel;
    private readonly TextBlock _diagText;

    private readonly DispatcherTimer _tick = new();
    private readonly DispatcherTimer _cursorTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _diagTimer = new() { Interval = TimeSpan.FromSeconds(15) };

    private Playlist? _playlist;
    private string? _currentId;
    private int _seqIndex = -1;
    private long _seqCount;
    private string? _videoKey;
    private bool _videoSyncMode;

    // state shared with the sync loop and diagnostics
    private long _clockOffsetMs;
    internal long LastOkMs;
    internal (long at, string msg)? LastError;
    internal long NextSyncMs;
    internal string ScreenName = "";
    internal Playlist? CurrentPlaylist => _playlist;
    internal long ClockOffsetMs => Interlocked.Read(ref _clockOffsetMs);
    internal int ScreenW = 1920, ScreenH = 1080;
    internal bool VideoFailedOnce;

    private readonly CancellationTokenSource _stop = new();
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _diagOpenedMs;
    private bool _quitting;
    private Store.Settings _settings = Store.LoadSettings();

    public event Action? HiddenToTray;
    public event Action? QuitRequested;

    public bool SlideshowVisible => IsVisible && WindowState != WindowState.Minimized;

    public PlayerWindow()
    {
        Title = Config.DisplayName;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.Black;
        ShowInTaskbar = true;
        Content = _root;

        _layers = new Image[2];
        for (var i = 0; i < 2; i++)
        {
            _layers[i] = new Image { Stretch = Stretch.Uniform, Opacity = 0 };
            RenderOptions.SetBitmapScalingMode(_layers[i], BitmapScalingMode.HighQuality);
            _root.Children.Add(_layers[i]);
        }
        // Opaque backdrop: hides videos being prepared (z -2) from photos' letterbox areas.
        var backdrop = new Border { Background = Brushes.Black };
        Panel.SetZIndex(backdrop, -1);
        _root.Children.Add(backdrop);
        _videoWatch.Tick += (_, _) => OnVideoWatch();

        _message = new TextBlock
        {
            Foreground = Brushes.White, FontSize = 40, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(80), Visibility = Visibility.Collapsed, FontFamily = new FontFamily("Segoe UI"),
        };
        var messageBox = new Viewbox { Child = _message, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
        Panel.SetZIndex(messageBox, 10);   // above the videos, which are stacked at 0/1
        _root.Children.Add(messageBox);

        _status = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)), FontSize = 16,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(24, 12, 24, 12),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 4, ShadowDepth = 0, Color = Colors.Black },
        };
        Panel.SetZIndex(_status, 11);
        _root.Children.Add(_status);

        _diagText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(235, 235, 235)), FontFamily = new FontFamily("Consolas"), FontSize = 20,
        };
        _diagPanel = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(245, 12, 16, 24)), Visibility = Visibility.Collapsed,
            Child = new Viewbox { Child = _diagText, Stretch = Stretch.Uniform, Margin = new Thickness(40) },
        };
        Panel.SetZIndex(_diagPanel, 12);
        _root.Children.Add(_diagPanel);

        ContextMenu = BuildContextMenu();

        _tick.Tick += (_, _) => { _tick.Stop(); ShowNext(); };
        _cursorTimer.Tick += (_, _) => { _cursorTimer.Stop(); if (!_diagPanelVisible) Cursor = Cursors.None; };
        _diagTimer.Tick += (_, _) => RefreshDiag();
        MouseMove += (_, _) => { Cursor = null; _cursorTimer.Stop(); _cursorTimer.Start(); };
        KeyDown += OnKey;
        Closing += (_, e) =>
        {
            if (_quitting) return;
            e.Cancel = true;        // Alt+F4 hides to the tray; Quit is in the tray menu
            HideToTray();
        };
        IsVisibleChanged += (_, _) => OnVisibilityChanged();
        StateChanged += (_, _) => OnVisibilityChanged();
        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(PlaceOnMonitor);
        SystemEvents.PowerModeChanged += (_, e) => { if (e.Mode == PowerModes.Resume) Wake(); };
        NetworkChange.NetworkAddressChanged += (_, _) => Wake();
    }

    // ------------------------------------------------------------------ start / show / hide

    public void Start()
    {
        PlaceOnMonitor();
        Show();
        Activate();
        Cursor = Cursors.None;

        // Developer check (not for screens): SIGNAGE_TEST_VIDEO=<a.mp4;b.jpg;...> plays those files in order (one
        // video = a lone loop) with no server, 5 s per photo, logging each start; watch the screen from outside.
        if (Environment.GetEnvironmentVariable("SIGNAGE_TEST_VIDEO") is { Length: > 0 } testFiles)
        {
            _testMode = true;
            Log.Info($"TEST mode: {testFiles}");
            var test = new Playlist { Name = "test", Sync = false, IntervalSec = 5 };
            var n = 0;
            foreach (var f in testFiles.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var video = f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);
                var id = (++n).ToString("x").PadLeft(32, 'e');
                File.Copy(f, Store.MediaPath(id, video), true);
                if (video && Environment.GetEnvironmentVariable("SIGNAGE_TEST_NOFIX") == null) Mp4Fix.FixInPlace(Store.MediaPath(id, video));
                test.Items.Add(new Item(id, null, null, video, 9000));
            }
            ApplyPlaylist(test);
            return;
        }

        if (string.IsNullOrEmpty(Config.ServerUrl))
        {
            ShowMessage("This build has no server address.\nDownload the player again from your signage server's /get/windows link.");
            return;
        }
        // Offline-first: start from the saved playlist immediately.
        if (Store.LoadManifest() is { } j)
        {
            try { ApplyPlaylist(Playlist.Parse(j)); }
            catch (Exception e) { Log.Warn("Bad saved playlist", e); }
        }
        if (_playlist == null) ShowMessage("Starting…");
        _ = Task.Run(SyncLoop);
    }

    public void ShowSlideshow()
    {
        PlaceOnMonitor();
        Show();
        WindowState = WindowState.Maximized;
        Activate();
        Cursor = Cursors.None;
    }

    public void HideToTray()
    {
        HideDiag();
        Hide();
        HiddenToTray?.Invoke();
    }

    public void Quit()
    {
        _quitting = true;
        _stop.Cancel();
        StopVideo();
        SysInfo.KeepAwake(false);
        Close();
    }

    private void OnVisibilityChanged()
    {
        if (SlideshowVisible)
        {
            SysInfo.KeepAwake(true);
            ScheduleNext(0);
        }
        else
        {
            // Hidden: nothing to draw, let the PC sleep as normal. Syncing carries on.
            SysInfo.KeepAwake(false);
            _tick.Stop();
            StopVideo();
            _currentId = null;
        }
    }

    /// <summary>Full screen on the chosen monitor (or the main one), covering the taskbar.</summary>
    public void PlaceOnMonitor()
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        var s = screens.FirstOrDefault(x => x.DeviceName == _settings.Monitor) ?? System.Windows.Forms.Screen.PrimaryScreen ?? screens[0];
        ScreenW = s.Bounds.Width;
        ScreenH = s.Bounds.Height;
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        var wasMax = WindowState == WindowState.Maximized;
        if (wasMax) WindowState = WindowState.Normal;
        SetWindowPos(hwnd, IntPtr.Zero, s.Bounds.Left, s.Bounds.Top, s.Bounds.Width, s.Bounds.Height, SwpNoZOrder | SwpNoActivate);
        WindowState = WindowState.Maximized;
    }

    public int MonitorCount => System.Windows.Forms.Screen.AllScreens.Length;

    public void NextMonitor()
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        if (screens.Length < 2) return;
        var i = Array.FindIndex(screens, x => x.DeviceName == (_settings.Monitor ?? System.Windows.Forms.Screen.PrimaryScreen?.DeviceName));
        _settings.Monitor = screens[(i + 1) % screens.Length].DeviceName;
        Store.SaveSettings(_settings);
        PlaceOnMonitor();
        _currentId = null;
        ScheduleNext(0);
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    private const uint SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010;

    private ContextMenu BuildContextMenu()
    {
        var m = new ContextMenu();
        MenuItem Item(string text, Action a)
        {
            var mi = new MenuItem { Header = text };
            mi.Click += (_, _) => a();
            m.Items.Add(mi);
            return mi;
        }
        Item("Diagnostics (F1)", ShowDiag);
        var next = Item("Show on the next monitor", NextMonitor);
        Item("Hide to the tray (Esc)", HideToTray);
        m.Items.Add(new Separator());
        Item("Quit Signage Player", () => QuitRequested?.Invoke());
        m.Opened += (_, _) => { Cursor = null; next.IsEnabled = MonitorCount > 1; };
        return m;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                if (_diagPanelVisible) HideDiag(); else HideToTray();
                e.Handled = true;
                break;
            case Key.F1 or Key.D or Key.I:
                if (_diagPanelVisible) RefreshDiag(); else ShowDiag();
                e.Handled = true;
                break;
        }
    }

    // ------------------------------------------------------------------ sync (background)

    private void Wake() => _wake.TrySetResult();

    private async Task SyncLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            long waitSec;
            try
            {
                waitSec = await SyncOnce(_stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                Log.Warn("Sync failed", e);
                SetStatus(_playlist != null ? "Offline – showing saved images" : "Can't reach server – retrying");
                LastError = (Now(), Short(e));
                waitSec = 30;
            }
            NextSyncMs = Now() + waitSec * 1000;
            var wake = _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(waitSec), _stop.Token), wake.Task);
                if (wake.Task.IsCompleted) await Task.Delay(2000, _stop.Token);   // let a new network settle
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static string Short(Exception e) => (e.InnerException ?? e).Message;

    internal Dictionary<string, string> DeviceHeaders()
    {
        var h = new Dictionary<string, string>
        {
            ["X-App-Version"] = Config.Version,
            // pause: we keep media saved and blank the screen ourselves; video: we play MP4 clips.
            ["X-Capabilities"] = "pause,video",
            ["X-Device-Info"] = SysInfo.DeviceInfo(ScreenW, ScreenH),
        };
        if (SysInfo.NetworkHeader() is { } n) h["X-Network"] = n;
        return h;
    }

    /// <summary>One check-in with the server. Returns seconds until the next one.</summary>
    private async Task<long> SyncOnce(CancellationToken ct)
    {
        var token = Store.Token;
        if (token == null)
        {
            var r0 = await Net.Call(HttpMethod.Post, "/v1/register", null, DeviceHeaders(), ct);
            if (r0.Code != 200)
            {
                SetStatus($"Pairing unavailable ({r0.Code}) – retrying");
                LastError = (Now(), $"register: HTTP {r0.Code}");
                return 30;
            }
            using var d0 = JsonDocument.Parse(r0.Body);
            var t = Playlist.Str(d0.RootElement, "token");
            var code = Playlist.Str(d0.RootElement, "code") ?? "";
            if (t == null || t.Length != 64) throw new InvalidDataException("Bad pairing reply");
            Store.Token = t;
            Log.Info($"Registered with {Config.ServerUrl}; pairing code {code}");
            Ui(() => ShowPairing(code));
            return 5;
        }

        var r = await Net.Call(HttpMethod.Get, "/v1/manifest", token, DeviceHeaders(), ct);
        if (r.Code == 401)
        {
            // Removed in the admin page: wipe saved copies and ask to be paired again.
            Log.Info("Pairing key rejected (screen removed); re-pairing");
            Ui(() =>
            {
                StopVideo();
                _playlist = null;
                _currentId = null;
                foreach (var l in _layers) { l.Source = null; l.Opacity = 0; }
                ShowMessage("Re-pairing…");
            });
            Store.ClearAll();
            return 1;
        }
        if (r.Code != 200)
        {
            SetStatus($"Server error {r.Code}");
            LastError = (Now(), $"server answered HTTP {r.Code}");
            return 60;
        }

        using var doc = JsonDocument.Parse(r.Body);
        var j = doc.RootElement;
        Interlocked.Exchange(ref _clockOffsetMs, Playlist.Long(j, "server_time", Now()) - Now());
        LastOkMs = Now();
        ScreenName = Playlist.Str(j, "name") ?? ScreenName;
        var pollSec = Math.Clamp(Playlist.Long(j, "poll_sec", 120), 5, 3600);

        if (Playlist.Str(j, "status") == "pending")
        {
            var code = Playlist.Str(j, "code") ?? "";
            Ui(() => ShowPairing(code));
            return pollSec;
        }

        // Download anything new before switching, so the screen never shows a broken image.
        var pl = Playlist.Parse(j);
        var keep = new HashSet<string>();
        var failed = 0;
        foreach (var o in j.GetProperty("images").EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            var id = Playlist.Str(o, "id");
            var url = Playlist.Str(o, "url");
            if (id == null || !Playlist.IsId(id) || url == null || !url.StartsWith("/v1/")) continue;
            var item = pl.Items.First(i => i.Id == id);
            keep.Add(Path.GetFileName(Store.MediaPath(item)));
            if (Store.Has(item)) continue;
            try
            {
                await Net.Download(url, token, Store.MediaPath(item), ct);
                // Before it can be shown: rewrite the recorder's fragmented MP4 so Windows plays it properly.
                if (item.IsVideo) await Task.Run(() => Mp4Fix.FixInPlace(Store.MediaPath(item)), ct);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Log.Warn($"Download failed: {id}", e);
                failed++;
            }
        }
        Store.SaveManifest(r.Body);
        if (_playlist?.Signature != pl.Signature)
            Log.Info($"Screen \"{ScreenName}\": playlist \"{pl.Name}\", {pl.Items.Count} item(s){(pl.Paused ? ", paused" : "")}{(failed > 0 ? $", {failed} download(s) failed" : "")}");
        Ui(() =>
        {
            ApplyPlaylist(pl);
            Store.Prune(keep);   // on the UI thread: never deletes the clip that's playing (it's still in the list)
        });
        SetStatus(failed > 0 ? $"{failed} item(s) still downloading" : "");

        var command = Playlist.Str(j, "command");
        if (command != null) Log.Info($"Command from the admin page: {command}");
        switch (command)
        {
            case "restart":
                Ui(() => App.Restart());
                break;
            case "reload":
                Ui(() => { StopVideo(); _currentId = null; _seqIndex = -1; ScheduleNext(0); });
                break;
            case "diag":
                Ui(() => { if (SlideshowVisible) ShowDiag(); });
                break;
            // "reboot" / "shutdown" are for dedicated Raspberry Pi screens; a Windows PC may be someone's desk PC.
        }
        return failed > 0 ? Math.Min(pollSec, 30) : pollSec;
    }

    // ------------------------------------------------------------------ display (UI thread)

    private void Ui(Action a) => Dispatcher.BeginInvoke(() => { if (!_quitting) a(); });

    private void SetStatus(string s) => Ui(() => _status.Text = s);

    private void ShowMessage(string s)
    {
        _message.Inlines.Clear();
        _message.Text = s;
        _message.Visibility = Visibility.Visible;
    }

    private void HideMessage() => _message.Visibility = Visibility.Collapsed;

    private void ShowPairing(string code)
    {
        _playlist = null;
        _tick.Stop();
        StopVideo();
        foreach (var l in _layers) l.Opacity = 0;
        var spaced = code.Length == 6 ? code[..3] + " " + code[3..] : code;
        _message.Text = "";
        _message.Inlines.Clear();
        _message.Inlines.Add(new System.Windows.Documents.Run("Pair this screen\n\n"));
        _message.Inlines.Add(new System.Windows.Documents.Run(spaced) { FontSize = 128, FontWeight = FontWeights.Bold });
        _message.Inlines.Add(new System.Windows.Documents.Run(
            "\n\nIn the Signage admin page, go to\nScreens → Add a screen and enter this code.") { FontSize = 26 });
        _message.Visibility = Visibility.Visible;
    }

    private void ApplyPlaylist(Playlist pl)
    {
        var changed = _playlist?.Signature != pl.Signature;
        _playlist = pl;
        if (changed)
        {
            _seqIndex = -1;
            StopVideo();
            ScheduleNext(0);
        }
    }

    private long ServerNow() => Now() + ClockOffsetMs;

    private List<Item> VisibleItems(Playlist pl)
    {
        var today = pl.Today(ServerNow());
        return pl.Items.Where(i =>
            (i.StartDate == null || string.CompareOrdinal(i.StartDate, today) <= 0) &&
            (i.EndDate == null || string.CompareOrdinal(i.EndDate, today) >= 0) &&
            Store.Has(i)).ToList();
    }

    private void ScheduleNext(long delayMs)
    {
        _tick.Stop();
        _tick.Interval = TimeSpan.FromMilliseconds(Math.Max(1, delayMs));
        _tick.Start();
    }

    private void FadeOutPhotos()
    {
        foreach (var l in _layers) l.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(500)));
    }

    private void ShowNext()
    {
        if (_quitting || !SlideshowVisible) return;
        var pl = _playlist;
        if (pl == null) return;
        if (pl.Paused)
        {
            // Paused from the admin page: black screen, or the message if one was set.
            StopVideo();
            _currentId = null;
            FadeOutPhotos();
            if (pl.PauseMessage.Length > 0) ShowMessage(pl.PauseMessage); else HideMessage();
            ScheduleNext(15_000);
            return;
        }
        var items = VisibleItems(pl);
        if (items.Count == 0)
        {
            StopVideo();
            _currentId = null;
            FadeOutPhotos();
            ShowMessage(pl.Items.Count == 0 ? $"No photos or videos on “{pl.Name}” yet" : "Downloading…");
            ScheduleNext(15_000);
            return;
        }
        HideMessage();

        int index;
        long delay, offsetMs;
        string playKey;
        if (pl.Sync)
        {
            // Every screen on this playlist computes the same position from the (server-corrected) clock:
            // the playlist repeats with photos lasting the interval and videos their own length.
            var durs = items.Select(i => ItemMs(i, pl)).ToList();
            var cycle = durs.Sum();
            var t = ServerNow();
            var pos = t % cycle;
            var i = 0;
            while (i < items.Count - 1 && pos >= durs[i]) { pos -= durs[i]; i++; }
            index = i;
            offsetMs = pos;
            delay = durs[i] - pos + 30;
            playKey = $"{t / cycle}:{i}";
        }
        else
        {
            _seqIndex = (_seqIndex + 1) % items.Count;
            _seqCount++;
            index = _seqIndex;
            offsetMs = 0;
            delay = ItemMs(items[index], pl);
            playKey = $"s{_seqCount}";
        }
        var item = items[index];
        if (item.IsVideo)
        {
            if (items.Count == 1)
            {
                // Only this clip to show: loop it seamlessly as it ends (like the Pi and Fire Stick players),
                // rather than waiting for the clock, whose duration_ms can be longer than the converted clip.
                var loopKey = "loop:" + item.Id;
                if (_videoKey != loopKey) PlayVideo(item, offsetMs, loopKey, pl.Sync, loop: true);
            }
            else if (playKey != _videoKey)
            {
                PlayVideo(item, offsetMs, playKey, pl.Sync, loop: false);
            }
        }
        else
        {
            StopCurrentVideo(underPhoto: true);   // keeps a video that's being prepared for later
            if (item.Id != _currentId || _layers[_front].Opacity < 1) Display(item);
        }
        // Get the next video ready in a hidden copy, so it starts instantly (a lone clip's "next" is itself).
        PrepareSoon(items.Count == 1 ? item : items[(index + 1) % items.Count]);
        ScheduleNext(delay);
    }

    private static long ItemMs(Item i, Playlist pl) => i.IsVideo ? Math.Max(1000L, i.DurationMs) : pl.IntervalSec * 1000L;

    // ---- video
    //
    // The admin page's browser recorder makes *fragmented* MP4s. Windows (Media Foundation) plays those badly: frozen on
    // the first frame until the second keyframe (~3.8 s), no length, no seeking, no rewinding. So every downloaded
    // video is first rewritten as a plain MP4 (Mp4Fix, no re-encoding); then all of that works. On top of that, what
    // measured clean on the real screen (sampled from outside; an in-app screenshot probe hid the problems):
    //   - every play uses a brand-new MediaElement (a reused one often never draws its first frame);
    //   - the next video is prepared ahead while the current item shows, behind an opaque black backdrop (z: prepared
    //     -2, backdrop -1, photos 0, playing video 1, overlays 10+): played ~1 s, paused, rewound to 0 ("rewind"), then
    //     swapped on top. A clip opened paused never draws; other methods froze or went black 2.5-3.8 s per switch;
    //   - a lone clip loops on MediaEnded (like the Pi and Fire Stick players) into its prepared copy;
    //   - a video that must start before it's prepared waits ~1 s showing the previous picture.
    private MediaElement? _cur;           // on screen
    private Item? _curItem;
    private MediaElement? _next;          // prepared, hidden
    private string? _nextPath;
    private string? _wantNextPath;
    private bool _nextReady;
    private long _videoOffset;
    private bool _videoLoop;
    private int _watchRetries;
    private bool _testMode;

    /// <summary>How a hidden copy is prepared (measured on the real screen, 2 clips, ~8 switches each):
    ///   "rewind" (plain MP4s, i.e. after Mp4Fix): play ~1 s, pause, jump back to 0. No black, no freeze.
    ///   "slow" (fallback for files still fragmented): play at 1/20 speed ~1 s, pause (~50 ms in).
    ///   "play": pause after ~1 s at normal speed. "pause": open paused. Both froze or went black 2.5-3.8 s per switch,
    ///   as did "slow" on plain files. SIGNAGE_PRELOAD forces one (testing).</summary>
    private static readonly string? PreloadOverride = Environment.GetEnvironmentVariable("SIGNAGE_PRELOAD") is { Length: > 0 } m ? m : null;
    private string _nextMode = "rewind";

    // A video that has to start but wasn't prepared is prepared first and shown once ready (~1 s), instead of being
    // opened on screen (1-4 s of black). Meanwhile the previous picture stays up.
    private (Item item, long offsetMs, string key, bool sync, bool loop, long askedAt)? _startWhenReady;

    private MediaElement NewVideoElement(int z)
    {
        var v = new MediaElement
        {
            LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual,
            IsMuted = true, Volume = 0, Stretch = Stretch.Uniform, ScrubbingEnabled = true,
        };
        Panel.SetZIndex(v, z);
        v.MediaOpened += OnVideoOpened;
        v.MediaEnded += OnVideoEnded;
        v.MediaFailed += OnVideoFailed;
        _root.Children.Add(v);
        return v;
    }

    /// <summary>Stops a video element, releases its file and removes it (a moment later, never inside its own event).</summary>
    private void Discard(MediaElement? v)
    {
        if (v == null) return;
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                v.Stop();
                v.Close();
                v.Source = null;
            }
            catch
            {
            }
            _root.Children.Remove(v);
        });
    }

    private void PlayVideo(Item item, long offsetMs, string key, bool syncMode, bool loop)
    {
        var path = Store.MediaPath(item);
        var old = _cur;
        var prepared = _next != null && _nextPath == path;
        if (prepared)
        {
            _cur = _next!;
            _next = null;
            _nextPath = null;
            Panel.SetZIndex(_cur, 1);
            if (PreloadOverride != null)
                Log.Info($"DEV prepared copy of {item.Id[..6]} ({_nextMode}) takes over at {_cur.Position.TotalMilliseconds:0} ms (ready: {_nextReady})");
            _cur.SpeedRatio = 1;
            // "Keep in step" joining mid-clip: jump to the shared position (only works on the rewritten files).
            if (offsetMs > 500 && _nextMode == "rewind") _cur.Position = TimeSpan.FromMilliseconds(offsetMs);
            _cur.Play();
        }
        else if (!_openDirectly)
        {
            // Not prepared yet: prepare it hidden and start it when ready (see _startWhenReady).
            if (_next == null || _nextPath != path) Prepare(path);
            _wantNextPath = path;
            _startWhenReady = (item, offsetMs, key, syncMode, loop, Now());
            _videoKey = key;   // so the slideshow doesn't ask again meanwhile
            _videoSyncMode = syncMode;
            StartWatch();
            return;
        }
        else
        {
            DiscardNext();
            _cur = NewVideoElement(1);
            _cur.Source = new Uri(path);
            _cur.Play();
        }
        _startWhenReady = null;
        _nextReady = false;
        if (old != _cur) Discard(old);
        _curItem = item;
        _videoKey = key;
        _currentId = item.Id;
        _videoOffset = prepared ? 0 : offsetMs;
        _videoSyncMode = syncMode;
        _videoLoop = loop;
        _watchRetries = 0;
        if (_testMode) Log.Info($"TEST play {item.Id[..6]} {(prepared ? "prepared copy" : "opened now")}");
        HideMessage();
        foreach (var l in _layers) { l.BeginAnimation(OpacityProperty, null); l.Opacity = 0; l.Source = null; }
        StartWatch();
    }

    /// <summary>Prepares the upcoming video (if it is one) shortly, once the current item has started.</summary>
    private void PrepareSoon(Item upcoming)
    {
        if (!upcoming.IsVideo)
        {
            if (_startWhenReady == null) { _wantNextPath = null; DiscardNext(); }   // keep one still being prepared to start
            return;
        }
        var path = Store.MediaPath(upcoming);
        _wantNextPath = path;
        if (_next != null && _nextPath == path) return;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        t.Tick += (_, _) =>
        {
            if (_startWhenReady != null) return;   // the current video is still being prepared; try again shortly
            t.Stop();
            if (_wantNextPath != path || (_next != null && _nextPath == path)) return;
            Prepare(path);
        };
        t.Start();
    }

    private void Prepare(string path)
    {
        DiscardNext();
        var v = NewVideoElement(-2);
        v.Source = new Uri(path);
        try { _nextMode = PreloadOverride ?? (Mp4Fix.IsFragmented(path) ? "slow" : "rewind"); }
        catch { _nextMode = "slow"; }
        if (_nextMode == "pause") v.Pause();
        else
        {
            if (_nextMode == "slow") v.SpeedRatio = 0.05;   // 1 s of preparing = 50 ms into the clip
            v.Play();
        }
        _next = v;
        _nextPath = path;
        _nextReady = false;
    }

    private void DiscardNext()
    {
        Discard(_next);
        _next = null;
        _nextPath = null;
        _nextReady = false;
    }

    private void OnVideoOpened(object? sender, RoutedEventArgs e)
    {
        if (sender == _next)
        {
            var v = _next;
            if (_nextMode == "pause") { OnNextReady(); return; }
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                if (v == null || v != _next) return;
                v.Pause();
                if (_nextMode == "rewind") v.Position = TimeSpan.Zero;   // works on the rewritten (plain) MP4s
                OnNextReady();
            };
            t.Start();
            return;
        }
        if (_cur == null || sender != _cur) return;
        if (_videoOffset > 500) _cur.Position = TimeSpan.FromMilliseconds(_videoOffset);   // usually ignored (see above)
        _videoOffset = 0;
    }

    private bool _openDirectly;   // fallback when preparing didn't work

    private void OnNextReady()
    {
        _nextReady = true;
        if (_startWhenReady is not { } s || _nextPath != Store.MediaPath(s.item)) return;
        _startWhenReady = null;
        // In step: it was asked for a moment ago, so join that much further in.
        var offset = s.sync && s.offsetMs > 0 ? s.offsetMs + (Now() - s.askedAt) : 0;
        PlayVideo(s.item, offset, s.key, s.sync, s.loop);
    }

    /// <summary>Preparing failed or never finished: open the waiting video directly on screen instead.</summary>
    private void StartWaitingDirectly()
    {
        if (_startWhenReady is not { } s) return;
        _startWhenReady = null;
        _openDirectly = true;
        try { PlayVideo(s.item, s.offsetMs, s.key, s.sync, s.loop); }
        finally { _openDirectly = false; }
    }

    private void OnVideoEnded(object? sender, RoutedEventArgs e)
    {
        if (sender != _cur) return;
        if (_testMode) Log.Info($"TEST ended (next ready: {_nextReady})");
        if (_videoLoop && _curItem != null)
        {
            var item = _curItem;
            PlayVideo(item, 0, _videoKey ?? "loop:" + item.Id, _videoSyncMode, loop: true);
            PrepareSoon(item);
            return;
        }
        // Normal mode: move on as soon as the clip ends. In-step mode keeps to the shared clock.
        if (!_videoSyncMode) ScheduleNext(0);
    }

    // Watchdog: occasionally a freshly opened clip never starts. Position only moves when a new frame arrives and a
    // clip can open on a still picture lasting several seconds, so wait 12 s; then open it again, then skip it.
    private void StartWatch()
    {
        _videoWatch.Stop();
        _videoWatch.Start();
    }

    private void OnVideoWatch()
    {
        _videoWatch.Stop();
        if (_startWhenReady != null)
        {
            Log.Warn("A video took too long to prepare; opening it directly");
            StartWaitingDirectly();
            return;
        }
        if (_videoKey == null || _cur == null || _curItem == null) return;
        if (_cur.Position > TimeSpan.Zero) return;
        Log.Warn($"Video {_currentId} didn't start; {(_watchRetries == 0 ? "opening it again" : "skipping it")}");
        if (_watchRetries++ == 0)
        {
            var old = _cur;
            _cur = NewVideoElement(1);
            _cur.Source = new Uri(Store.MediaPath(_curItem));
            _cur.Play();
            Discard(old);
            _videoWatch.Start();
        }
        else
        {
            StopVideo();
            _currentId = null;
            ScheduleNext(_videoSyncMode ? 1000 : 0);
        }
    }

    private void OnVideoFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        if (sender == _next)
        {
            DiscardNext();
            if (_startWhenReady != null) StartWaitingDirectly();   // will report the failure itself
            return;
        }
        if (sender != _cur) return;
        Log.Warn($"Video {_currentId} failed", e.ErrorException);
        VideoFailedOnce = true;
        LastError = (Now(), "video: " + e.ErrorException.Message);
        StopVideo();
        if (!_videoSyncMode) ScheduleNext(0);
    }

    /// <summary>Takes the playing video off screen; a video prepared for later is kept. With underPhoto, its last
    /// frame stays just below the photos (above the backdrop) while the next photo fades in, then it is removed.</summary>
    private void StopCurrentVideo(bool underPhoto = false)
    {
        _videoWatch.Stop();
        if (underPhoto && _cur != null)
        {
            var leaving = _cur;
            Panel.SetZIndex(leaving, -1);   // same as the backdrop but added later, so drawn over it
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
            t.Tick += (_, _) => { t.Stop(); Discard(leaving); };
            t.Start();
        }
        else Discard(_cur);
        _cur = null;
        _curItem = null;
        _videoKey = null;
        _videoLoop = false;
        _startWhenReady = null;
    }

    /// <summary>Removes all video, including anything prepared.</summary>
    private void StopVideo()
    {
        StopCurrentVideo();
        _wantNextPath = null;
        DiscardNext();
    }

    // ---- photos

    private void Display(Item item)
    {
        var path = Store.MediaPath(item);
        var w = ScreenW;
        Task.Run(() =>
        {
            BitmapImage? bmp = null;
            try
            {
                // Read into memory so the file isn't kept open (it may be deleted when the playlist changes).
                var bytes = File.ReadAllBytes(path);
                bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new MemoryStream(bytes);
                bmp.DecodePixelWidth = Math.Min(w, 3840);
                bmp.EndInit();
                bmp.Freeze();
            }
            catch (Exception e)
            {
                Log.Warn($"Could not decode {item.Id}; will download it again", e);
                bmp = null;
                Store.TryDelete(path);
            }
            Ui(() =>
            {
                if (bmp == null) return;
                Crossfade(bmp);
                _currentId = item.Id;
            });
        });
    }

    private void Crossfade(BitmapSource bmp)
    {
        var incoming = _layers[1 - _front];
        var outgoing = _layers[_front];
        incoming.Source = bmp;
        incoming.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(900)));
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(900));
        fade.Completed += (_, _) => { if (outgoing != _layers[_front]) outgoing.Source = null; };
        outgoing.BeginAnimation(OpacityProperty, fade);
        _front = 1 - _front;
    }

    // ------------------------------------------------------------------ diagnostics page

    private bool _diagPanelVisible => _diagPanel.Visibility == Visibility.Visible;

    public void ShowDiag()
    {
        if (!SlideshowVisible) ShowSlideshow();
        _diagOpenedMs = Now();
        _diagText.Text = "DIAGNOSTICS\n\nRunning checks…".PadRight(Diagnostics.Cols);
        _diagPanel.Visibility = Visibility.Visible;
        Cursor = null;
        RefreshDiag();
        _diagTimer.Start();
    }

    public void HideDiag()
    {
        _diagTimer.Stop();
        _diagPanel.Visibility = Visibility.Collapsed;
    }

    private void RefreshDiag()
    {
        if (!_diagPanelVisible) return;
        if (Now() - _diagOpenedMs > 5 * 60_000) { HideDiag(); return; }
        Task.Run(async () =>
        {
            string text;
            try { text = await Diagnostics.Run(this); }
            catch (Exception e) { text = "Diagnostics failed: " + e.Message; }
            Ui(() => { if (_diagPanelVisible) _diagText.Text = text; });
        });
    }
}

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
    private readonly MediaElement[] _videos;
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
        _videos = new MediaElement[2];
        for (var i = 0; i < 2; i++)
        {
            var v = new MediaElement
            {
                LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual,
                IsMuted = true, Volume = 0, Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed,
                ScrubbingEnabled = true,   // so a paused, preloaded clip shows its first frame
            };
            v.MediaOpened += OnVideoOpened;
            v.MediaEnded += OnVideoEnded;
            v.MediaFailed += OnVideoFailed;
            _root.Children.Add(v);
            _videos[i] = v;
        }
        _videoWatch.Tick += (_, _) => OnVideoWatch();

        _message = new TextBlock
        {
            Foreground = Brushes.White, FontSize = 40, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(80), Visibility = Visibility.Collapsed, FontFamily = new FontFamily("Segoe UI"),
        };
        _root.Children.Add(new Viewbox { Child = _message, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly });

        _status = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)), FontSize = 16,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(24, 12, 24, 12),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 4, ShadowDepth = 0, Color = Colors.Black },
        };
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

        // Developer check (not for screens): SIGNAGE_TEST_VIDEO=<file.mp4> loops that one clip with no server,
        // logging every restart, to measure gaps.
        if (Environment.GetEnvironmentVariable("SIGNAGE_TEST_VIDEO") is { Length: > 0 } testVideo)
        {
            _testMode = true;
            Log.Info($"TEST mode: looping {testVideo}");
            var id = new string('0', 32);
            var dest = Store.MediaPath(id, true);
            File.Copy(testVideo, dest, true);
            ApplyPlaylist(new Playlist { Name = "test", Sync = false, Items = { new Item(id, null, null, true, 9000) } });
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
            keep.Add(id);
            var item = pl.Items.First(i => i.Id == id);
            if (Store.Has(item)) continue;
            try
            {
                await Net.Download(url, token, Store.MediaPath(item), ct);
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
            StopVideo();
            if (item.Id != _currentId || _layers[_front].Opacity < 1) Display(item);
        }
        ScheduleNext(delay);
    }

    private static long ItemMs(Item i, Playlist pl) => i.IsVideo ? Math.Max(1000L, i.DurationMs) : pl.IntervalSec * 1000L;

    // ---- video

    // The browser-recorded MP4s have no length in their header, so Windows can't seek in them or rewind them once
    // they've ended (rewinding stalls on the first frame). Every play therefore opens the file afresh, and there
    // are two video elements: while one plays, a lone looping clip is opened and paused on its first frame in the
    // other, which takes over the moment the first ends (measured ~25-70 ms; reopening takes ~0.6 s of black).
    private long _videoOffset;
    private bool _videoLoop;
    private string? _videoPath;
    private int _vcur;
    private bool _preloaded;          // the other element holds _videoPath, paused on its first frame
    private int _watchRetries;
    private bool _testMode;
    private readonly System.Diagnostics.Stopwatch _testClock = System.Diagnostics.Stopwatch.StartNew();
    private long _testEndedAt;
    private MediaElement CurVideo => _videos[_vcur];
    private MediaElement OtherVideo => _videos[1 - _vcur];

    private void PlayVideo(Item item, long offsetMs, string key, bool syncMode, bool loop)
    {
        _videoKey = key;
        _currentId = item.Id;
        _videoOffset = offsetMs;
        _videoSyncMode = syncMode;
        _videoLoop = loop;
        _videoPath = Store.MediaPath(item);
        _watchRetries = 0;
        if (_testMode) Log.Info($"TEST play {key} loop={loop}");
        HideMessage();
        foreach (var l in _layers) { l.BeginAnimation(OpacityProperty, null); l.Opacity = 0; l.Source = null; }
        Unload(OtherVideo);
        _preloaded = false;
        _preloading = null;
        Open(CurVideo, play: true);
        Show(CurVideo);
        StartWatch();
    }

    private void Open(MediaElement v, bool play)
    {
        Unload(v);   // setting the same Source again would not reopen the file
        v.Source = new Uri(_videoPath!);
        if (play) v.Play(); else v.Pause();   // Pause opens it and shows the first frame
    }

    private static void Unload(MediaElement v)
    {
        try
        {
            v.Stop();
            v.Close();
            v.Source = null;   // releases the file
        }
        catch
        {
        }
    }

    private void Show(MediaElement v)
    {
        // Both stay fully opaque and visible (WPF stops drawing a transparent video and then takes seconds to show
        // it again); the playing one is simply stacked on top of the waiting one.
        foreach (var x in _videos)
        {
            x.Visibility = Visibility.Visible;
            x.Opacity = 1;
            Panel.SetZIndex(x, x == v ? 1 : 0);
        }
    }

    private void OnVideoOpened(object? sender, RoutedEventArgs e)
    {
        if (sender != CurVideo) { OnPreloadOpened((MediaElement)sender!); return; }
        if (_videoOffset > 500) CurVideo.Position = TimeSpan.FromMilliseconds(_videoOffset);   // usually ignored (see above)
        _videoOffset = 0;
        // Get the next loop ready in the other element.
        if (_videoLoop && !_preloaded) Dispatcher.BeginInvoke(PreloadNextLoop, DispatcherPriority.Background);
    }

    // A clip opened paused never draws its first frame, and these files often start with a still picture stored as a
    // single frame (the next new frame can be seconds later), so a paused-preloaded copy showed black for ~4 s.
    // Instead the hidden copy plays (muted, underneath the visible one) until it has opened and drawn its first
    // frame, then pauses there, ready to take over.
    private void PreloadNextLoop()
    {
        if (!_videoLoop || _videoPath == null || _preloaded) return;
        var v = OtherVideo;
        Open(v, play: true);
        _preloading = v;
    }

    private MediaElement? _preloading;

    private void OnPreloadOpened(MediaElement v)
    {
        if (v != _preloading) return;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (v != _preloading || v == CurVideo) return;
            v.Pause();
            _preloading = null;
            _preloaded = true;
        };
        t.Start();
    }

    private void OnVideoEnded(object? sender, RoutedEventArgs e)
    {
        if (sender != CurVideo) return;
        if (_testMode)
        {
            _testEndedAt = _testClock.ElapsedMilliseconds;
            Log.Info($"TEST ended (preloaded copy ready: {_preloaded})");
            // Log what is actually on screen (average brightness of the window) every 100 ms for 4 s.
            var samples = new List<string>();
            var probe = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            probe.Tick += (_, _) =>
            {
                var ms = _testClock.ElapsedMilliseconds - _testEndedAt;
                samples.Add($"{ms}:{ScreenBrightness():0}");
                if (ms < 4500) return;
                probe.Stop();
                var black = samples.Count(s => s.EndsWith(":0"));
                Log.Info($"TEST restart: {black * 100} ms black of the first 4.5 s ({string.Join(" ", samples.Take(5))} ...)");
            };
            probe.Start();
        }
        if (_videoLoop)
        {
            var finished = CurVideo;
            if (_preloaded || _preloading == OtherVideo)
            {
                _preloading = null;
                _vcur = 1 - _vcur;          // the waiting copy takes over straight away
                CurVideo.Play();
                Show(CurVideo);
            }
            else
            {
                Open(CurVideo, play: true);
            }
            _preloaded = false;
            _watchRetries = 0;
            StartWatch();
            // A moment later (not inside this event), unload the finished copy and preload the next loop into it.
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                if (!_videoLoop || finished == CurVideo) return;
                Unload(finished);
                PreloadNextLoop();
            };
            t.Start();
            return;
        }
        // Normal mode: move on as soon as the clip ends. In-step mode keeps to the shared clock.
        if (!_videoSyncMode) ScheduleNext(0);
    }

    private double ScreenBrightness()
    {
        var rtb = new RenderTargetBitmap(64, 36, 96, 96, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) dc.DrawRectangle(new VisualBrush(_root), null, new Rect(0, 0, 64, 36));
        rtb.Render(dv);
        var px = new byte[64 * 36 * 4];
        rtb.CopyPixels(px, 64 * 4, 0);
        double sum = 0;
        for (var i = 0; i < px.Length; i += 4) sum += (px[i] + px[i + 1] + px[i + 2]) / 3.0;
        return sum / (64 * 36);
    }

    // Watchdog: occasionally a freshly opened clip never starts. If it hasn't moved after 12 s, open it again;
    // if that fails too, give up on this play and move on.
    private void StartWatch()
    {
        _videoWatch.Stop();
        _videoWatch.Start();
    }

    private void OnVideoWatch()
    {
        _videoWatch.Stop();
        if (_videoKey == null || _videoPath == null) return;
        if (CurVideo.Position > TimeSpan.Zero) return;
        Log.Warn($"Video {_currentId} didn't start; {(_watchRetries == 0 ? "opening it again" : "skipping it")}");
        if (_watchRetries++ == 0)
        {
            _preloaded = false;
            _preloading = null;
            Unload(OtherVideo);
            Open(CurVideo, play: true);
            Show(CurVideo);
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
        if (sender != CurVideo) { Unload((MediaElement)sender!); _preloaded = false; return; }
        Log.Warn($"Video {_currentId} failed", e.ErrorException);
        VideoFailedOnce = true;
        LastError = (Now(), "video: " + e.ErrorException.Message);
        StopVideo();
        if (!_videoSyncMode) ScheduleNext(0);
    }

    private void StopVideo()
    {
        _videoWatch.Stop();
        if (_videoKey == null && _videos.All(v => v.Visibility != Visibility.Visible)) return;
        _videoKey = null;
        _videoLoop = false;
        _preloaded = false;
        _preloading = null;
        _videoPath = null;
        foreach (var v in _videos)
        {
            Unload(v);
            v.Visibility = Visibility.Collapsed;
        }
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

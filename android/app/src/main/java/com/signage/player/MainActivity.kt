package com.signage.player

import android.app.Activity
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Color
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.text.SpannableString
import android.text.Spanned
import android.text.style.RelativeSizeSpan
import android.text.style.StyleSpan
import android.util.DisplayMetrics
import android.util.Log
import android.view.Gravity
import android.view.View
import android.view.WindowManager
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.TextView
import android.widget.VideoView
import android.content.Context
import android.net.ConnectivityManager
import android.net.wifi.WifiManager
import org.json.JSONObject
import java.net.Inet4Address
import java.net.NetworkInterface
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors

class MainActivity : Activity() {

    private lateinit var store: Store
    private lateinit var layers: Array<ImageView>
    private var front = 0
    private lateinit var message: TextView
    private lateinit var statusLine: TextView
    private lateinit var videoView: VideoView
    private var videoKey: String? = null   // which play of which clip is on screen (clips can repeat)
    private var seqCount = 0L

    private val main = Handler(Looper.getMainLooper())
    private lateinit var decoder: ExecutorService
    private var syncThread: Thread? = null
    @Volatile private var running = false

    private var playlist: Playlist? = null
    private var currentId: String? = null
    private var sequentialIndex = -1
    @Volatile private var clockOffsetMs = 0L
    private var screenW = 1920
    private var screenH = 1080

    private val tick = Runnable { showNext() }

    // ------------------------------------------------------------------ lifecycle

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        store = Store(this)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON or WindowManager.LayoutParams.FLAG_FULLSCREEN)

        val dm = DisplayMetrics()
        @Suppress("DEPRECATION")
        windowManager.defaultDisplay.getRealMetrics(dm)
        screenW = maxOf(dm.widthPixels, 640)
        screenH = maxOf(dm.heightPixels, 360)

        val root = FrameLayout(this)
        root.setBackgroundColor(Color.BLACK)
        layers = Array(2) {
            ImageView(this).apply {
                scaleType = ImageView.ScaleType.FIT_CENTER
                alpha = 0f
                root.addView(this, FrameLayout.LayoutParams(-1, -1))
            }
        }
        // Videos play here (hardware decoded, muted). VideoView keeps the clip's aspect ratio.
        videoView = VideoView(this).apply { visibility = View.GONE }
        root.addView(videoView, FrameLayout.LayoutParams(-1, -1, Gravity.CENTER))
        message = TextView(this).apply {
            setTextColor(Color.WHITE)
            textSize = 28f
            gravity = Gravity.CENTER
            setPadding(80, 80, 80, 80)
            visibility = View.GONE
        }
        root.addView(message, FrameLayout.LayoutParams(-1, -1))
        statusLine = TextView(this).apply {
            setTextColor(Color.argb(170, 255, 255, 255))
            textSize = 12f
            setShadowLayer(4f, 0f, 0f, Color.BLACK)
            setPadding(24, 12, 24, 12)
        }
        root.addView(statusLine, FrameLayout.LayoutParams(-2, -2, Gravity.BOTTOM or Gravity.END))
        setContentView(root)
    }

    override fun onStart() {
        super.onStart()
        App.cancelLaunch(this)
        hideSystemUi()
        running = true
        decoder = Executors.newSingleThreadExecutor()

        if (BuildConfig.SERVER_URL.isEmpty()) {
            showMessage("This build has no server address.\nRebuild the app from GitHub Actions.")
            return
        }
        // Offline-first: start from the cached playlist immediately.
        store.loadManifest()?.let { j ->
            try {
                applyPlaylist(Playlist.parse(j))
            } catch (e: Exception) {
                Log.w(App.TAG, "Bad cached manifest", e)
            }
        }
        if (playlist == null) showMessage("Starting…")
        startSyncLoop()
    }

    override fun onStop() {
        super.onStop()
        running = false
        syncThread?.interrupt()
        syncThread = null
        main.removeCallbacks(tick)
        stopVideo()
        decoder.shutdownNow()
        // If someone pressed Home and walked away, come back to the slideshow after 10 minutes.
        App.scheduleLaunch(this, 10 * 60_000L)
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) hideSystemUi()
    }

    @Suppress("DEPRECATION")
    private fun hideSystemUi() {
        window.decorView.systemUiVisibility = (View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY
            or View.SYSTEM_UI_FLAG_FULLSCREEN
            or View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
            or View.SYSTEM_UI_FLAG_LAYOUT_STABLE
            or View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN
            or View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION)
    }

    // ------------------------------------------------------------------ sync (background thread)

    private fun startSyncLoop() {
        val t = Thread({
            while (running) {
                val waitSec = try {
                    syncOnce()
                } catch (e: InterruptedException) {
                    break
                } catch (e: Exception) {
                    Log.w(App.TAG, "Sync failed", e)
                    setStatus(if (playlist != null) "Offline – showing saved images" else "Can't reach server – retrying")
                    30L
                }
                try {
                    Thread.sleep(waitSec * 1000)
                } catch (e: InterruptedException) {
                    break
                }
            }
        }, "sync")
        t.isDaemon = true
        syncThread = t
        t.start()
    }

    /** How this stick is connected (Wi-Fi/Ethernet, network name, signal 0-100, IP), shown in the admin page. */
    @Suppress("DEPRECATION")
    private fun networkHeader(): String? = try {
        val cm = getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
        val ni = cm.activeNetworkInfo
        if (ni == null || !ni.isConnected) {
            null
        } else {
            val type = when (ni.type) {
                ConnectivityManager.TYPE_WIFI -> "wifi"
                ConnectivityManager.TYPE_ETHERNET -> "ethernet"
                else -> "other"
            }
            val o = JSONObject().put("type", type)
            if (type == "wifi") {
                val wm = applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
                val info = wm.connectionInfo
                if (info != null) {
                    val ssid = info.ssid?.trim('"')
                    if (!ssid.isNullOrEmpty() && ssid != "<unknown ssid>") o.put("ssid", ssid)
                    o.put("signal", WifiManager.calculateSignalLevel(info.rssi, 101))
                }
            }
            // First IPv4 address on the matching interface (wlan* / eth*), else any non-loopback one.
            val prefix = if (type == "wifi") "wlan" else if (type == "ethernet") "eth" else ""
            val addrs = NetworkInterface.getNetworkInterfaces()?.toList().orEmpty()
                .filter { it.isUp && !it.isLoopback }
                .sortedByDescending { prefix.isNotEmpty() && it.name.startsWith(prefix) }
                .flatMap { it.inetAddresses.toList() }
                .filterIsInstance<Inet4Address>()
            addrs.firstOrNull()?.hostAddress?.let { o.put("ip", it) }
            o.toString().replace(Regex("[^\\x20-\\x7E]"), "?")   // HTTP headers must be ASCII
        }
    } catch (_: Exception) {
        null
    }

    private fun deviceHeaders(): Map<String, String> = buildMap {
        putAll(baseHeaders())
        networkHeader()?.let { put("X-Network", it) }
    }

    private fun baseHeaders(): Map<String, String> = mapOf(
        "X-App-Version" to BuildConfig.VERSION_NAME,
        // pause: we keep images cached and blank the screen ourselves when the admin pauses all screens.
        // video: we can play MP4 clips.
        "X-Capabilities" to "pause,video",
        // HTTP headers must be plain ASCII
        "X-Device-Info" to "${Build.MANUFACTURER} ${Build.MODEL} | Android ${Build.VERSION.RELEASE} | ${screenW}x$screenH"
            .replace(Regex("[^\\x20-\\x7E]"), "?"),
    )

    /** One check-in with the server. Returns seconds until the next one. */
    private fun syncOnce(): Long {
        val token = store.token
        if (token == null) {
            val r = Net.call("POST", "/v1/register", null, deviceHeaders())
            if (r.code != 200) {
                setStatus("Pairing unavailable (${r.code}) – retrying")
                return 30
            }
            val j = JSONObject(r.body)
            store.token = j.getString("token")
            ui { showPairing(j.getString("code")) }
            return 5
        }

        val r = Net.call("GET", "/v1/manifest", token, deviceHeaders())
        if (r.code == 401) {
            // Removed in the admin page: wipe local copies and ask to be paired again.
            store.clearAll()
            ui {
                stopVideo()
                playlist = null
                currentId = null
                layers.forEach { it.setImageDrawable(null); it.alpha = 0f }
                showMessage("Re-pairing…")
            }
            return 1
        }
        if (r.code != 200) {
            setStatus("Server error ${r.code}")
            return 60
        }

        val j = JSONObject(r.body)
        clockOffsetMs = j.optLong("server_time", System.currentTimeMillis()) - System.currentTimeMillis()
        val pollSec = j.optLong("poll_sec", 120).coerceIn(5, 3600)

        if (j.optString("status") == "pending") {
            ui { showPairing(j.optString("code")) }
            return pollSec
        }

        // Download anything new before switching, so the screen never shows a broken image.
        val images = j.getJSONArray("images")
        val keep = HashSet<String>()
        var failed = 0
        for (i in 0 until images.length()) {
            if (!running) throw InterruptedException()
            val o = images.getJSONObject(i)
            val id = o.getString("id")
            keep.add(id)
            val f = store.mediaFile(id, o.optString("kind", "image") == "video")
            if (!f.exists() || f.length() == 0L) {
                try {
                    Net.download(o.getString("url"), token, f)
                } catch (e: Exception) {
                    Log.w(App.TAG, "Download failed: $id", e)
                    failed++
                }
            }
        }
        store.saveManifest(r.body)
        store.prune(keep)
        val pl = Playlist.parse(j)
        ui { applyPlaylist(pl) }
        setStatus(if (failed > 0) "$failed image(s) still downloading" else "")

        when (j.optString("command", "")) {
            "restart" -> ui { App.restartNow(this) }
            "reload" -> ui { stopVideo(); currentId = null; sequentialIndex = -1; scheduleNext(0) }
        }
        return if (failed > 0) minOf(pollSec, 30) else pollSec
    }

    // ------------------------------------------------------------------ display (main thread)

    private fun ui(block: () -> Unit) {
        main.post { if (running) block() }
    }

    private fun setStatus(s: String) = main.post { statusLine.text = s }

    private fun showMessage(s: CharSequence) {
        message.text = s
        message.visibility = View.VISIBLE
    }

    private fun hideMessage() {
        message.visibility = View.GONE
    }

    private fun showPairing(code: String) {
        playlist = null
        main.removeCallbacks(tick)
        stopVideo()
        layers.forEach { it.alpha = 0f }
        val title = "Pair this screen\n\n"
        val spaced = code.chunked(3).joinToString(" ")
        val hint = "\n\nIn the Signage admin page, go to\nScreens → Add a screen and enter this code."
        val s = SpannableString(title + spaced + hint)
        s.setSpan(RelativeSizeSpan(3.2f), title.length, title.length + spaced.length, Spanned.SPAN_EXCLUSIVE_EXCLUSIVE)
        s.setSpan(StyleSpan(android.graphics.Typeface.BOLD), title.length, title.length + spaced.length, Spanned.SPAN_EXCLUSIVE_EXCLUSIVE)
        s.setSpan(RelativeSizeSpan(0.6f), title.length + spaced.length, s.length, Spanned.SPAN_EXCLUSIVE_EXCLUSIVE)
        showMessage(s)
    }

    private fun applyPlaylist(pl: Playlist) {
        val changed = playlist?.signature != pl.signature
        playlist = pl
        if (changed) {
            sequentialIndex = -1
            stopVideo()
            scheduleNext(0)
        }
    }

    private fun now() = System.currentTimeMillis() + clockOffsetMs

    private fun visibleItems(pl: Playlist): List<Item> {
        val today = pl.today(now())
        return pl.items.filter { item ->
            (item.startDate == null || item.startDate <= today) &&
                (item.endDate == null || item.endDate >= today) &&
                store.mediaFile(item).let { it.exists() && it.length() > 0 }
        }
    }

    private fun scheduleNext(delayMs: Long) {
        main.removeCallbacks(tick)
        main.postDelayed(tick, delayMs.coerceAtLeast(0))
    }

    private fun showNext() {
        if (!running) return
        val pl = playlist ?: return
        if (pl.paused) {
            // Paused from the admin page: black screen, or the message if one was set.
            stopVideo()
            currentId = null
            layers.forEach { it.animate().alpha(0f).setDuration(500) }
            if (pl.pauseMessage.isNotEmpty()) showMessage(pl.pauseMessage) else hideMessage()
            scheduleNext(15_000)
            return
        }
        val items = visibleItems(pl)
        if (items.isEmpty()) {
            stopVideo()
            currentId = null
            layers.forEach { it.animate().alpha(0f).setDuration(500) }
            showMessage(if (pl.items.isEmpty()) "No photos or videos on “${pl.name}” yet" else "Downloading…")
            scheduleNext(15_000)
            return
        }
        hideMessage()

        val index: Int
        val delay: Long
        val offsetMs: Long
        val playKey: String
        if (pl.sync) {
            // Every screen on this playlist computes the same position from the (server-corrected)
            // clock: the playlist repeats with photos lasting the interval and videos their length.
            val durs = items.map { itemMs(it, pl) }
            val cycle = durs.sum()
            val t = now()
            var pos = t % cycle
            var i = 0
            while (i < items.size - 1 && pos >= durs[i]) {
                pos -= durs[i]
                i++
            }
            index = i
            offsetMs = pos
            delay = durs[i] - pos + 30
            playKey = "${t / cycle}:$i"
        } else {
            sequentialIndex = (sequentialIndex + 1) % items.size
            seqCount++
            index = sequentialIndex
            offsetMs = 0
            delay = itemMs(items[index], pl)
            playKey = "s$seqCount"
        }
        val item = items[index]
        if (item.isVideo) {
            if (items.size == 1) {
                // Only this clip to show: loop it in place instead of restarting it every time.
                val loopKey = "loop:${item.id}"
                if (videoKey != loopKey) playVideo(item, offsetMs, loopKey, loop = true)
            } else if (playKey != videoKey) {
                playVideo(item, offsetMs, playKey, loop = false)
            }
        } else {
            stopVideo()
            if (item.id != currentId || layers[front].alpha < 1f) display(item)
        }
        scheduleNext(delay)
    }

    private fun itemMs(item: Item, pl: Playlist): Long =
        if (item.isVideo) maxOf(1000L, item.durationMs) else pl.intervalSec * 1000L

    private fun playVideo(item: Item, offsetMs: Long, key: String, loop: Boolean) {
        videoKey = key
        currentId = item.id
        hideMessage()
        layers.forEach { it.animate().cancel(); it.alpha = 0f; it.setImageDrawable(null) }
        videoView.visibility = View.VISIBLE
        videoView.setOnPreparedListener { mp ->
            mp.setVolume(0f, 0f)   // signage plays silently
            mp.isLooping = loop
            if (offsetMs > 500) videoView.seekTo(offsetMs.toInt())
            videoView.start()
        }
        videoView.setOnCompletionListener {
            // Normal mode: move on as soon as the clip ends. In-step mode keeps to the shared clock.
            if (playlist?.sync == false) scheduleNext(0)
        }
        videoView.setOnErrorListener { _, what, extra ->
            Log.w(App.TAG, "Video ${item.id} failed ($what/$extra)")
            stopVideo()
            if (playlist?.sync == false) scheduleNext(0)
            true
        }
        videoView.setVideoPath(store.mediaFile(item).path)
    }

    private fun stopVideo() {
        if (videoKey == null && videoView.visibility != View.VISIBLE) return
        videoKey = null
        try {
            videoView.stopPlayback()
        } catch (_: Exception) {
        }
        videoView.visibility = View.GONE
    }

    private fun display(item: Item) {
        val file = store.mediaFile(item)
        val reqW = screenW
        val reqH = screenH
        try {
            decoder.execute {
                val bmp = decode(file.path, reqW, reqH)
                main.post {
                    if (!running) return@post
                    if (bmp == null) {
                        Log.w(App.TAG, "Could not decode ${item.id}; will re-download")
                        file.delete()
                    } else {
                        crossfade(bmp)
                        currentId = item.id
                    }
                }
            }
        } catch (_: java.util.concurrent.RejectedExecutionException) {
        }
    }

    private fun decode(path: String, reqW: Int, reqH: Int): Bitmap? = try {
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeFile(path, bounds)
        var sample = 1
        while (bounds.outWidth / (sample * 2) >= reqW || bounds.outHeight / (sample * 2) >= reqH) sample *= 2
        BitmapFactory.decodeFile(path, BitmapFactory.Options().apply {
            inSampleSize = sample
            inPreferredConfig = Bitmap.Config.RGB_565
        })
    } catch (e: OutOfMemoryError) {
        null
    } catch (e: Exception) {
        null
    }

    private fun crossfade(bmp: Bitmap) {
        val incoming = layers[1 - front]
        val outgoing = layers[front]
        incoming.animate().cancel()
        outgoing.animate().cancel()
        incoming.setImageBitmap(bmp)
        incoming.alpha = 0f
        incoming.animate().alpha(1f).setDuration(900)
        outgoing.animate().alpha(0f).setDuration(900).withEndAction { outgoing.setImageDrawable(null) }
        front = 1 - front
    }
}

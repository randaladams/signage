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

    // diagnostics page (Menu button on the remote, or "Show diagnostics" in the admin page)
    private lateinit var diagView: TextView
    @Volatile private var lastOkMs = 0L
    @Volatile private var lastError: Pair<Long, String>? = null
    @Volatile private var nextSyncMs = 0L
    @Volatile private var screenName = ""
    private var diagOpenedMs = 0L
    private val diagRefresh = Runnable { refreshDiag() }

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
        diagView = TextView(this).apply {
            setTextColor(Color.rgb(235, 235, 235))
            setBackgroundColor(Color.argb(245, 12, 16, 24))
            typeface = android.graphics.Typeface.MONOSPACE
            textSize = 15f
            setPadding(64, 48, 64, 48)
            visibility = View.GONE
        }
        root.addView(diagView, FrameLayout.LayoutParams(-1, -1))
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

    // Remote: Menu (☰) opens/closes diagnostics; Back closes it while it's open.
    override fun onKeyDown(keyCode: Int, event: android.view.KeyEvent?): Boolean {
        if (keyCode == android.view.KeyEvent.KEYCODE_MENU) {
            if (diagView.visibility == View.VISIBLE) hideDiag() else showDiag()
            return true
        }
        if (keyCode == android.view.KeyEvent.KEYCODE_BACK && diagView.visibility == View.VISIBLE) {
            hideDiag()
            return true
        }
        return super.onKeyDown(keyCode, event)
    }

    private fun showDiag() {
        diagOpenedMs = System.currentTimeMillis()
        diagView.text = "Diagnostics\n\nRunning checks…"
        diagView.visibility = View.VISIBLE
        diagView.bringToFront()
        main.removeCallbacks(diagRefresh)
        refreshDiag()
    }

    private fun hideDiag() {
        main.removeCallbacks(diagRefresh)
        diagView.visibility = View.GONE
    }

    /** Runs the checks off the main thread, shows them, and repeats every 15 s for up to 5 minutes. */
    private fun refreshDiag() {
        if (diagView.visibility != View.VISIBLE) return
        if (System.currentTimeMillis() - diagOpenedMs > 5 * 60_000L) { hideDiag(); return }
        Thread({
            val text = try { runDiagnostics() } catch (e: Exception) { "Diagnostics failed: ${e.message}" }
            main.post {
                if (diagView.visibility == View.VISIBLE) diagView.text = text
                main.postDelayed(diagRefresh, 15_000)
            }
        }, "diag").start()
    }

    @Suppress("DEPRECATION")
    private fun runDiagnostics(): String {
        val sb = StringBuilder()
        val fmt = java.text.SimpleDateFormat("EEE HH:mm:ss", java.util.Locale.US)
        val t = { ms: Long -> if (ms > 0) fmt.format(java.util.Date(ms)) else "never" }
        fun line(ok: Boolean?, label: String, detail: String) {
            sb.append(if (ok == true) " OK  " else if (ok == false) "FAIL " else " ??  ")
                .append(label.padEnd(26)).append(detail).append('\n')
        }
        sb.append("DIAGNOSTICS").append(if (screenName.isNotEmpty()) " - $screenName" else "")
            .append("\nMenu or Back closes · refreshes every 15 s · closes by itself after 5 min\n\n")
        sb.append("Connection checks (the first FAIL is where it breaks)\n")

        val cm = getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
        val ni = cm.activeNetworkInfo
        val net = networkHeader()?.let { JSONObject(it) }
        if (ni == null || !ni.isConnected) {
            line(false, "Network (cable or Wi-Fi)", "not connected")
        } else if (net?.optString("type") == "wifi") {
            val sig = net.optInt("signal", -1)
            line(true, "Wi-Fi connected", "${net.optString("ssid", "?")}, signal $sig%" + if (sig in 0..39) " (weak)" else "")
        } else {
            line(true, "Network connected", net?.optString("type") ?: ni.typeName)
        }
        val ip = net?.optString("ip", "") ?: ""
        line(ip.isNotEmpty(), "IP address", ip.ifEmpty { "none (DHCP failed?)" })

        var gateway: java.net.InetAddress? = null
        if (Build.VERSION.SDK_INT >= 23) {
            cm.activeNetwork?.let { n -> cm.getLinkProperties(n)?.routes?.firstOrNull { it.isDefaultRoute }?.gateway?.let { gateway = it } }
        }
        if (gateway == null && net?.optString("type") == "wifi") {
            val g = (applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager).dhcpInfo.gateway
            if (g != 0) gateway = java.net.InetAddress.getByAddress(byteArrayOf((g and 0xff).toByte(), (g shr 8 and 0xff).toByte(), (g shr 16 and 0xff).toByte(), (g shr 24 and 0xff).toByte()))
        }
        val gw = gateway
        if (gw == null) line(null, "Router (gateway)", "unknown")
        else {
            val start = System.currentTimeMillis()
            val ok = try { gw.isReachable(2000) } catch (_: Exception) { false }
            line(if (ok) true else null, "Router reachable", "${gw.hostAddress}" + if (ok) " (${System.currentTimeMillis() - start} ms)" else " (no reply; many routers ignore this)")
        }

        val host = java.net.URL(BuildConfig.SERVER_URL).host
        var dnsOk = false
        try {
            val start = System.currentTimeMillis()
            val a = java.net.InetAddress.getByName(host)
            dnsOk = true
            line(true, "Internet names (DNS)", "$host -> ${a.hostAddress} (${System.currentTimeMillis() - start} ms)")
        } catch (e: Exception) {
            line(false, "Internet names (DNS)", e.message ?: "failed")
        }

        var serverOk = false
        if (dnsOk) {
            try {
                val start = System.currentTimeMillis()
                val r = Net.call("GET", "/", null, emptyMap())
                serverOk = r.code == 200
                line(serverOk, "Signage server", if (serverOk) "HTTPS OK (${System.currentTimeMillis() - start} ms)" else "HTTP ${r.code}")
            } catch (e: Exception) {
                line(false, "Signage server", e.message ?: "failed")
            }
        } else line(false, "Signage server", "skipped (DNS failed)")

        val token = store.token
        when {
            token == null -> line(false, "Pairing key", "none: this screen is not paired")
            !serverOk -> line(null, "Pairing key", "can't check without the server")
            else -> try {
                val r = Net.call("GET", "/v1/manifest", token, deviceHeaders())
                when (r.code) {
                    200 -> {
                        val paired = JSONObject(r.body).optString("status") == "paired"
                        line(paired, "Pairing key", if (paired) "accepted (paired)" else "waiting to be paired in the admin page")
                    }
                    401 -> line(false, "Pairing key", "rejected: screen was removed; it will show a new pairing code")
                    else -> line(null, "Pairing key", "HTTP ${r.code}")
                }
            } catch (e: Exception) {
                line(null, "Pairing key", e.message ?: "failed")
            }
        }

        sb.append('\n')
        val pl = playlist
        fun info(label: String, value: String) { sb.append(label.padEnd(22)).append(value).append('\n') }
        info("Screen", "${screenName.ifEmpty { "(not paired)" }} · playlist ${pl?.name ?: "-"}")
        info("Player", "${BuildConfig.VERSION_NAME} · ${Build.MANUFACTURER} ${Build.MODEL} · Android ${Build.VERSION.RELEASE}")
        info("Last check-in OK", t(lastOkMs) + if (nextSyncMs > 0) "  (next ${t(nextSyncMs)})" else "")
        lastError?.let { info("Last error", "${t(it.first)}: ${it.second.take(120)}") }
        if (pl != null) {
            val have = pl.items.count { store.mediaFile(it).let { f -> f.exists() && f.length() > 0 } }
            info("Content", "$have of ${pl.items.size} items saved on this screen" + if (pl.paused) " · PAUSED" else "")
        }
        val am = getSystemService(Context.ACTIVITY_SERVICE) as android.app.ActivityManager
        val mi = android.app.ActivityManager.MemoryInfo().also { am.getMemoryInfo(it) }
        info("Memory", "${mi.availMem / 1_000_000} MB free of ${mi.totalMem / 1_000_000} MB")
        info("Storage", "${filesDir.usableSpace / 1_000_000} MB free")
        val up = android.os.SystemClock.elapsedRealtime() / 1000
        info("Uptime", "${up / 86400}d ${up % 86400 / 3600}h ${up % 3600 / 60}m")
        info("Clock", "${java.text.SimpleDateFormat("yyyy-MM-dd HH:mm:ss", java.util.Locale.US).format(java.util.Date())} · ${clockOffsetMs / 1000} s from server")
        info("Server", BuildConfig.SERVER_URL)
        info("Checked", t(System.currentTimeMillis()))
        return sb.toString()
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
                    lastError = System.currentTimeMillis() to (e.message ?: e.javaClass.simpleName)
                    30L
                }
                nextSyncMs = System.currentTimeMillis() + waitSec * 1000
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
            lastError = System.currentTimeMillis() to "server answered HTTP ${r.code}"
            return 60
        }

        val j = JSONObject(r.body)
        clockOffsetMs = j.optLong("server_time", System.currentTimeMillis()) - System.currentTimeMillis()
        lastOkMs = System.currentTimeMillis()
        screenName = j.optString("name", screenName)
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
            "diag" -> ui { showDiag() }
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

package com.signage.player

import android.content.Context
import org.json.JSONObject
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.TimeZone

class Item(
    val id: String,
    val startDate: String?,
    val endDate: String?,
    val isVideo: Boolean = false,
    val durationMs: Long = 0,
)

class Playlist(
    val name: String,
    val intervalSec: Int,
    val sync: Boolean,
    val tz: String,
    val items: List<Item>,
    val paused: Boolean,
    val pauseMessage: String,
) {
    /** Changes when anything that affects what's on screen changes. */
    val signature: String = "$intervalSec|$sync|$tz|$paused|$pauseMessage|" +
        items.joinToString(",") { "${it.id}:${it.startDate}:${it.endDate}:${it.isVideo}:${it.durationMs}" }

    fun today(nowMs: Long): String {
        val f = SimpleDateFormat("yyyy-MM-dd", Locale.US)
        f.timeZone = TimeZone.getTimeZone(tz)
        return f.format(Date(nowMs))
    }

    companion object {
        fun parse(j: JSONObject): Playlist {
            val pl = j.getJSONObject("playlist")
            val arr = j.getJSONArray("images")
            val items = ArrayList<Item>(arr.length())
            for (i in 0 until arr.length()) {
                val o = arr.getJSONObject(i)
                items.add(
                    Item(
                        id = o.getString("id"),
                        startDate = o.optStringOrNull("start_date"),
                        endDate = o.optStringOrNull("end_date"),
                        isVideo = o.optString("kind", "image") == "video",
                        durationMs = o.optLong("duration_ms", 0),
                    )
                )
            }
            return Playlist(
                name = pl.optString("name", ""),
                intervalSec = pl.optInt("interval_sec", 10).coerceIn(3, 3600),
                sync = pl.optBoolean("sync", true),
                tz = j.optString("tz", "UTC"),
                items = items,
                paused = j.optBoolean("paused", false),
                pauseMessage = j.optString("pause_message", "").take(120),
            )
        }

        private fun JSONObject.optStringOrNull(key: String): String? =
            if (isNull(key)) null else optString(key, "").ifEmpty { null }
    }
}

/** Local cache: the token, the last good manifest and the image files. Lets the screen run offline. */
class Store(private val ctx: Context) {
    private val prefs = ctx.getSharedPreferences("signage", Context.MODE_PRIVATE)
    private val imageDir = File(ctx.filesDir, "images").apply { mkdirs() }
    private val manifestFile = File(ctx.filesDir, "manifest.json")

    var token: String?
        get() = prefs.getString("token", null)
        set(v) {
            prefs.edit().putString("token", v).commit()
        }

    fun mediaFile(id: String, isVideo: Boolean): File = File(imageDir, if (isVideo) "$id.mp4" else "$id.jpg")

    fun mediaFile(item: Item): File = mediaFile(item.id, item.isVideo)

    fun saveManifest(json: String) {
        val tmp = File(ctx.filesDir, "manifest.json.tmp")
        tmp.writeText(json)
        tmp.renameTo(manifestFile)
    }

    fun loadManifest(): JSONObject? = try {
        if (manifestFile.exists()) JSONObject(manifestFile.readText()) else null
    } catch (_: Exception) {
        null
    }

    /** Deletes cached images that are no longer in the playlist. */
    fun prune(keep: Set<String>) {
        imageDir.listFiles()?.forEach { f ->
            val id = f.name.substringBefore('.')
            if (id !in keep || f.name.endsWith(".part")) f.delete()
        }
    }

    /** Wipes everything (used when the screen is removed in the admin page). */
    fun clearAll() {
        imageDir.listFiles()?.forEach { it.delete() }
        manifestFile.delete()
        token = null
    }
}

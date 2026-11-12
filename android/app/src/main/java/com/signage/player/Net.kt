package com.signage.player

import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL

class HttpResult(val code: Int, val body: String)

/** Minimal HTTPS client: no third-party libraries to keep the app small and dependable. */
object Net {
    private const val CONNECT_TIMEOUT = 15_000
    private const val READ_TIMEOUT = 60_000

    private fun open(path: String, method: String, token: String?, headers: Map<String, String>): HttpURLConnection {
        val url = URL(BuildConfig.SERVER_URL + path)
        val c = url.openConnection() as HttpURLConnection
        c.requestMethod = method
        c.connectTimeout = CONNECT_TIMEOUT
        c.readTimeout = READ_TIMEOUT
        c.useCaches = false
        c.instanceFollowRedirects = false
        c.setRequestProperty("Accept", "application/json")
        if (token != null) c.setRequestProperty("Authorization", "Bearer $token")
        for ((k, v) in headers) c.setRequestProperty(k, v)
        return c
    }

    fun call(method: String, path: String, token: String?, headers: Map<String, String>): HttpResult {
        val c = open(path, method, token, headers)
        try {
            if (method == "POST") {
                c.doOutput = true
                c.setFixedLengthStreamingMode(0)
                c.outputStream.close()
            }
            val code = c.responseCode
            val stream = if (code in 200..299) c.inputStream else c.errorStream
            val body = stream?.bufferedReader()?.use { it.readText() } ?: ""
            return HttpResult(code, body)
        } finally {
            c.disconnect()
        }
    }

    /** Downloads to a temp file and renames, so a half-finished download is never shown. */
    fun download(path: String, token: String, dest: File) {
        val c = open(path, "GET", token, emptyMap())
        val tmp = File(dest.parentFile, dest.name + ".part")
        try {
            val code = c.responseCode
            if (code != 200) throw IOException("HTTP $code for $path")
            val expected = c.getHeaderField("Content-Length")?.toLongOrNull() ?: -1L
            c.inputStream.use { input -> FileOutputStream(tmp).use { out -> input.copyTo(out, 64 * 1024) } }
            if (tmp.length() == 0L || (expected > 0 && tmp.length() != expected)) {
                throw IOException("Incomplete download for $path")
            }
            if (!tmp.renameTo(dest)) throw IOException("Could not save $path")
        } finally {
            tmp.delete()
            c.disconnect()
        }
    }
}

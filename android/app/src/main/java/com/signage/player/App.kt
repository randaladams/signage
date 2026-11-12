package com.signage.player

import android.app.AlarmManager
import android.app.Application
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.Process
import android.os.SystemClock
import android.util.Log

class App : Application() {
    override fun onCreate() {
        super.onCreate()
        // On any crash: log it, schedule a relaunch and exit quietly (no "app has stopped" dialog on the TV).
        Thread.setDefaultUncaughtExceptionHandler { _, error ->
            Log.e(TAG, "Crash, restarting in 5s", error)
            try {
                scheduleLaunch(this, 5_000)
            } catch (_: Throwable) {
            }
            Process.killProcess(Process.myPid())
            System.exit(10)
        }
    }

    companion object {
        const val TAG = "Signage"

        private fun launchIntent(ctx: Context): PendingIntent {
            val intent = Intent(ctx, MainActivity::class.java)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP)
            var flags = PendingIntent.FLAG_UPDATE_CURRENT
            if (Build.VERSION.SDK_INT >= 23) flags = flags or PendingIntent.FLAG_IMMUTABLE
            return PendingIntent.getActivity(ctx, 1, intent, flags)
        }

        /** Bring the slideshow back after [delayMs] (used after crashes, restarts and if someone leaves the app). */
        fun scheduleLaunch(ctx: Context, delayMs: Long) {
            val am = ctx.getSystemService(Context.ALARM_SERVICE) as AlarmManager
            am.set(AlarmManager.ELAPSED_REALTIME, SystemClock.elapsedRealtime() + delayMs, launchIntent(ctx))
        }

        fun cancelLaunch(ctx: Context) {
            val am = ctx.getSystemService(Context.ALARM_SERVICE) as AlarmManager
            am.cancel(launchIntent(ctx))
        }

        fun restartNow(ctx: Context) {
            scheduleLaunch(ctx, 2_000)
            Process.killProcess(Process.myPid())
            System.exit(0)
        }
    }
}

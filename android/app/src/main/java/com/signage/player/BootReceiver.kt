package com.signage.player

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent

/** Opens the slideshow when the Fire Stick powers on (and after the app is updated). */
class BootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val launch = Intent(context, MainActivity::class.java)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP)
        try {
            context.startActivity(launch)
        } catch (_: Exception) {
            // Some Fire OS builds block activity starts this early; retry shortly via alarm.
            App.scheduleLaunch(context, 15_000)
        }
    }
}

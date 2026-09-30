package dev.resourcemanager.android

import android.app.Notification
import android.app.NotificationManager
import android.app.Service
import android.content.Intent
import androidx.lifecycle.Lifecycle
import androidx.test.core.app.ActivityScenario
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.*
import org.junit.Test

class AutoSharingTest {
  @Test
  fun openStartsSharingAndReopenRestoresActualServiceAndNotification() {
    val app =
        InstrumentationRegistry.getInstrumentation().targetContext.applicationContext
            as RmApplication
    fun await(description: String, condition: () -> Boolean) {
      val deadline = System.currentTimeMillis() + 10000
      while (!condition() && System.currentTimeMillis() < deadline) Thread.sleep(50)
      assertTrue(description, condition())
    }
    val notifications = app.getSystemService(NotificationManager::class.java)
    fun foregroundNotification() =
        notifications.activeNotifications.any {
          it.id == 1 && it.notification.flags and Notification.FLAG_FOREGROUND_SERVICE != 0
        }
    app.stopSharing()
    await("old service stopped") { app.sharingService == null }
    val id = app.identity.deviceId
    val key = app.identity.publicKey
    try {
      ActivityScenario.launch(MainActivity::class.java).use { scenario ->
        await("opening automatically starts the actual service") {
          app.sharing.value && foregroundNotification()
        }
        assertTrue(app.sharingWanted.value)
        app.stopService(Intent(app, SharingService::class.java))
        await("service loss clears actual state, while intent remains") {
          app.sharingService == null && !app.sharing.value && app.serviceState.value == "已停止"
        }
        assertTrue(app.sharingWanted.value)
        scenario.moveToState(Lifecycle.State.CREATED)
        scenario.moveToState(Lifecycle.State.RESUMED)
        await("re-entering restores service and foreground notification") {
          app.sharing.value && foregroundNotification()
        }
        scenario.onActivity { app.sharingService!!.stopForeground(Service.STOP_FOREGROUND_REMOVE) }
        await("notification removed in the isolated fixture") { !foregroundNotification() }
        scenario.moveToState(Lifecycle.State.CREATED)
        scenario.moveToState(Lifecycle.State.RESUMED)
        await("existing service restores its foreground notification on re-entry") {
          app.sharing.value && foregroundNotification()
        }
        scenario.onActivity { app.stopSharing() }
        await("explicit stop stays stopped while the app remains visible") {
          !app.sharing.value && app.sharingService == null
        }
        assertFalse(app.sharingWanted.value)
      }
      ActivityScenario.launch(MainActivity::class.java).use {
        await("a fresh user open starts even after a previous explicit stop") {
          app.sharing.value && foregroundNotification()
        }
      }
      assertEquals(id, app.identity.deviceId)
      assertEquals(key, app.identity.publicKey)
    } finally {
      app.stopSharing()
      await("test service cleanup") { app.sharingService == null && !app.sharing.value }
    }
  }
}

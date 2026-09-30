package dev.resourcemanager.android

import android.content.Intent
import android.provider.DocumentsContract
import androidx.test.core.app.ActivityScenario
import androidx.test.platform.app.InstrumentationRegistry
import dev.resourcemanager.protocol.*
import org.junit.Assert.*
import org.junit.Test

class StorageAndLifecycleTest {
  @Test
  fun safDescendantReferenceCopyAndPersistence() {
    val app =
        InstrumentationRegistry.getInstrumentation().targetContext.applicationContext
            as RmApplication
    val ready = java.util.concurrent.CountDownLatch(1)
    val result = java.util.concurrent.atomic.AtomicInteger(0)
    app.sendOrderedBroadcast(
        Intent("dev.resourcemanager.android.test.GRANT")
            .setComponent(
                android.content.ComponentName(
                    "dev.resourcemanager.android.test",
                    "dev.resourcemanager.android.FixtureBootstrap",
                )
            )
            .addFlags(Intent.FLAG_INCLUDE_STOPPED_PACKAGES),
        null,
        object : android.content.BroadcastReceiver() {
          override fun onReceive(context: android.content.Context, intent: Intent) {
            result.set(resultCode)
            ready.countDown()
          }
        },
        null,
        0,
        null,
        null,
    )
    assertTrue(ready.await(10, java.util.concurrent.TimeUnit.SECONDS))
    assertEquals("Test grant provider must run", 1, result.get())
    val uri =
        DocumentsContract.buildTreeDocumentUri(
            "dev.resourcemanager.android.test.documents",
            "root",
        )
    app.contentResolver.takePersistableUriPermission(uri, Intent.FLAG_GRANT_READ_URI_PERMISSION)
    assertTrue(
        app.contentResolver.persistedUriPermissions.any { it.uri == uri && it.isReadPermission }
    )
    val child = DocumentsContract.buildDocumentUriUsingTree(uri, "root/child")
    val reference = app.files.import(child, true, false, "default", "")
    app.store.save(reference)
    val reloaded = Store(app).publication(reference.id)!!
    val entries = Files(app).tree(reloaded)
    assertTrue(entries.any { it.relativePath == "empty" && it.isDirectory })
    assertTrue(entries.any { it.relativePath == "中文.txt" })
    assertFalse(entries.any { it.relativePath.startsWith("child/") })
    val copy = app.files.import(child, true, true, "default", "")
    assertEquals(
        "SAF fixture 中文 😀",
        app.files.stream(app.files.resolve(copy, "中文.txt")).bufferedReader().use {
          it.readText()
        },
    )
    app.store.save(copy)
    app.files.cleanUnusedCopies { app.store.publications().map { it.id }.toSet() }
    assertTrue(app.files.root(copy).exists())
    app.store.remove("publication", copy.id)
    assertTrue(app.files.cleanUnusedCopies { app.store.publications().map { it.id }.toSet() } > 0)
    assertTrue(GrantedDocument.from(app, child).exists())
    app.store.remove("publication", reference.id)
  }

  @Test
  fun activityAndSharingStartStopWithoutLosingIdentity() {
    val app =
        InstrumentationRegistry.getInstrumentation().targetContext.applicationContext
            as RmApplication
    val id = app.identity.deviceId
    ActivityScenario.launch(MainActivity::class.java).use { scenario ->
      scenario.onActivity { app.startSharing() }
      val deadline = System.currentTimeMillis() + 10000
      while (!app.sharing.value && System.currentTimeMillis() < deadline) Thread.sleep(50)
      assertTrue(app.status.value, app.sharing.value)
      val network = app.network.wifi.value
      app.network.wifi.value = null
      val disconnected = System.currentTimeMillis() + 7000
      while (
          app.serviceState.value != "等待 Wi-Fi 网络" && System.currentTimeMillis() < disconnected
      ) Thread.sleep(50)
      assertEquals("等待 Wi-Fi 网络", app.serviceState.value)
      assertFalse(app.sharing.value)
      app.network.wifi.value = network
      val recovered = System.currentTimeMillis() + 7000
      while (!app.sharing.value && System.currentTimeMillis() < recovered) Thread.sleep(50)
      assertTrue("sharing recovers after Wi-Fi return", app.sharing.value)
      scenario.recreate()
      assertEquals(id, AndroidIdentity(app).deviceId)
      // Simulate the system destroying and recreating the service without a user stop.
      app.stopService(Intent(app, SharingService::class.java))
      val destroyed = System.currentTimeMillis() + 10000
      while (app.serviceState.value != "已停止" && System.currentTimeMillis() < destroyed) Thread
          .sleep(50)
      assertTrue(app.sharingWanted.value)
      androidx.core.content.ContextCompat.startForegroundService(
          app,
          Intent(app, SharingService::class.java),
      )
      val restarted = System.currentTimeMillis() + 10000
      while (!app.sharing.value && System.currentTimeMillis() < restarted) Thread.sleep(50)
      assertTrue("service rebuild honors persisted sharing intent", app.sharing.value)
      assertEquals(id, AndroidIdentity(app).deviceId)
      scenario.onActivity { app.stopSharing() }
      val stopDeadline = System.currentTimeMillis() + 10000
      while (app.sharing.value && System.currentTimeMillis() < stopDeadline) Thread.sleep(50)
      assertFalse(app.sharing.value)
      assertEquals(id, AndroidIdentity(app).deviceId)
      androidx.core.content.ContextCompat.startForegroundService(
          app,
          Intent(app, SharingService::class.java),
      )
      Thread.sleep(500)
      assertFalse("explicit user stop cannot be restarted", app.sharing.value)
      assertFalse(app.sharingWanted.value)
    }
  }
}

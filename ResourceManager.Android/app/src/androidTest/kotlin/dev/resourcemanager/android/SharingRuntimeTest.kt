package dev.resourcemanager.android

import android.net.Uri
import androidx.lifecycle.Lifecycle
import androidx.test.core.app.ActivityScenario
import androidx.test.platform.app.InstrumentationRegistry
import dev.resourcemanager.protocol.*
import java.io.File
import org.junit.Assert.*
import org.junit.Test

class SharingRuntimeTest {
    @Test
    fun backgroundHostForDozeAndReconnect() {
        org.junit.Assume.assumeTrue(
            InstrumentationRegistry.getArguments().getString("background") == "true"
        )
        val app =
            InstrumentationRegistry.getInstrumentation().targetContext.applicationContext
                as RmApplication
        val file =
            File(app.cacheDir, "background-fixture.txt").apply {
                writeText("Android -> Windows 中文 😀")
            }
        app.store.save(
            Publication(
                "android-live",
                file.name,
                Uri.fromFile(file).toString(),
                false,
                "Reference",
            )
        )
        val key = app.identity.publicKey
        ActivityScenario.launch(MainActivity::class.java).use { scenario ->
            scenario.onActivity { app.startSharing() }
            val until = System.currentTimeMillis() + 10000
            while (!app.sharing.value && System.currentTimeMillis() < until) Thread.sleep(50)
            assertTrue(app.serviceState.value, app.sharing.value)
            scenario.moveToState(Lifecycle.State.CREATED)
            File(app.filesDir, "background-stop").delete()
            File(app.filesDir, "background-ready").writeText("ready")
            val end = System.currentTimeMillis() + 300000
            while (
                System.currentTimeMillis() < end && !File(app.filesDir, "background-stop").exists()
            ) Thread.sleep(100)
            assertEquals(key, AndroidIdentity(app).publicKey)
            app.stopSharing()
            val stopped = System.currentTimeMillis() + 10000
            while (app.serviceState.value != "已停止" && System.currentTimeMillis() < stopped) Thread
                .sleep(50)
            assertFalse(app.sharing.value)
            assertFalse(app.getSharedPreferences("settings", 0).getBoolean("sharingWanted", true))
            File(app.filesDir, "background-ready").delete()
            File(app.filesDir, "background-stop").delete()
        }
    }
}

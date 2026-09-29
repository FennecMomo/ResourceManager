package dev.resourcemanager.android

import android.content.Intent
import android.net.Uri
import android.provider.DocumentsContract
import androidx.test.platform.app.InstrumentationRegistry
import androidx.test.core.app.ActivityScenario
import org.junit.Test
import org.junit.Assert.*
import dev.resourcemanager.protocol.*

class StorageAndLifecycleTest {
    @Test fun safDescendantReferenceCopyAndPersistence() {
        val app=InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as RmApplication
        val ready=java.util.concurrent.CountDownLatch(1)
        val result=java.util.concurrent.atomic.AtomicInteger(0)
        app.sendOrderedBroadcast(Intent("dev.resourcemanager.android.test.GRANT").setComponent(android.content.ComponentName("dev.resourcemanager.android.test","dev.resourcemanager.android.FixtureBootstrap")).addFlags(Intent.FLAG_INCLUDE_STOPPED_PACKAGES),null,object: android.content.BroadcastReceiver() {
            override fun onReceive(context: android.content.Context,intent: Intent) {result.set(resultCode);ready.countDown()}
        },null,0,null,null)
        assertTrue(ready.await(10,java.util.concurrent.TimeUnit.SECONDS))
        assertEquals("Test grant provider must run",1,result.get())
        val uri=DocumentsContract.buildTreeDocumentUri("dev.resourcemanager.android.test.documents","root")
        app.contentResolver.takePersistableUriPermission(uri,Intent.FLAG_GRANT_READ_URI_PERMISSION)
        assertTrue(app.contentResolver.persistedUriPermissions.any {it.uri==uri && it.isReadPermission})
        val child=DocumentsContract.buildDocumentUriUsingTree(uri,"root/child")
        val reference=app.files.import(child,true,false,"default","")
        app.store.save(reference)
        val reloaded=Store(app).publication(reference.id)!!
        val entries=Files(app).tree(reloaded)
        assertTrue(entries.any {it.relativePath=="empty" && it.isDirectory})
        assertTrue(entries.any {it.relativePath=="中文.txt"})
        assertFalse(entries.any {it.relativePath.startsWith("child/")})
        val copy=app.files.import(child,true,true,"default","")
        assertEquals("SAF fixture 中文 😀",app.files.stream(app.files.resolve(copy,"中文.txt")).bufferedReader().use {it.readText()})
        app.store.save(copy)
        app.files.cleanUnusedCopies {app.store.publications().map {it.id}.toSet()}
        assertTrue(app.files.root(copy).exists())
        app.store.remove("publication",copy.id)
        assertTrue(app.files.cleanUnusedCopies {app.store.publications().map {it.id}.toSet()} > 0)
        assertTrue(GrantedDocument.from(app,child).exists())
        app.store.remove("publication",reference.id)
    }
    @Test fun activityAndSharingStartStopWithoutLosingIdentity() {
        val app=InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as RmApplication
        val id=app.identity.deviceId
        ActivityScenario.launch(MainActivity::class.java).use { scenario ->
            scenario.onActivity {app.startSharing()}
            val deadline=System.currentTimeMillis()+10000
            while(!app.sharing.value && System.currentTimeMillis()<deadline) Thread.sleep(50)
            assertTrue(app.status.value,app.sharing.value)
            scenario.onActivity {app.stopSharing()}
            val stopDeadline=System.currentTimeMillis()+10000
            while(app.sharing.value && System.currentTimeMillis()<stopDeadline) Thread.sleep(50)
            assertFalse(app.sharing.value);assertEquals(id,AndroidIdentity(app).deviceId)
        }
    }
}

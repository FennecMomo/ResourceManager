package dev.resourcemanager.android

import androidx.test.platform.app.InstrumentationRegistry
import dev.resourcemanager.protocol.*
import org.junit.Test
import org.junit.Assert.*
import java.io.File
import android.net.Uri
import java.util.UUID

class NodeInstrumentedTest {
    @Test fun keystoreCioCatalogRangeAndPrivatePermissions() {
        val app = InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as RmApplication
        val bytes = "Android 实际文件 + 😀 0123456789".toByteArray()
        assertTrue(Proof.verify(app.identity.publicKey,app.identity.sign(bytes),bytes))
        assertEquals(app.identity.deviceId,AndroidIdentity(app).deviceId)
        val file = File(app.cacheDir,"interop-fixture.txt").apply { writeBytes(bytes) }
        val id = UUID.randomUUID().toString()
        val p = Publication(id,file.name,Uri.fromFile(file).toString(),false,"Reference")
        app.store.save(p)
        val server = PeerServer(app)
        try {
            server.start()
            val identity = JvmIdentity("test-${UUID.randomUUID()}")
            val peers = mutableMapOf<String,Peer>()
            val client = PeerClient(identity,{Hello(identity.deviceId,"Instrumented 中文 😀")},{peers[it]},{peers[it.hello.deviceId]=it})
            val remote = client.connect("127.0.0.1",PORT)
            assertTrue(client.get<Catalog>(remote,"/api/v1/resource-catalog").resources.any { it.id == id })
            var tag = ""
            client.request(remote,"/api/v1/resources/$id/content").use { response -> assertEquals(200,response.code);tag=response.header("ETag")!!;assertArrayEquals(bytes,response.body!!.bytes()) }
            client.request(remote,"/api/v1/resources/$id/content",range="bytes=3-",etag=tag).use { response -> assertEquals(206,response.code);assertArrayEquals(bytes.drop(3).toByteArray(),response.body!!.bytes()) }
            val group = LocalGroup(Group("private-test","private",createdUtc=java.time.Instant.now().toString()),"Private")
            app.store.save(group);app.store.save(p.copy(groupId=group.group.id))
            client.request(remote,"/api/v1/resources/$id/content").use { assertEquals(404,it.code) }
            assertFalse(client.get<Catalog>(remote,"/api/v1/resource-catalog").resources.any { it.id==id })
            val request = ChatRequest(messageId=UUID.randomUUID().toString(),senderDeviceId=identity.deviceId,recipientDeviceId=app.identity.deviceId,sentUtc=java.time.Instant.now().toString(),kind="Text",text="你好 Android")
            repeat(2) { attempt -> client.request(remote,"/api/v1/chat/messages",wire.encodeToString(request).toByteArray()).use { response ->
                assertEquals(200,response.code);val receipt=wire.decodeFromString<ChatReceipt>(response.body!!.string());assertTrue(receipt.accepted);assertEquals(attempt==1,receipt.duplicate)
            } }
            client.request(remote,"/api/v1/chat/messages",wire.encodeToString(request.copy(text="changed")).toByteArray()).use {assertEquals(409,it.code)}
        } finally { server.stop();app.store.remove("publication",id);file.delete() }
    }

    @Test fun actualAndroidToDesktop() {
        val args = InstrumentationRegistry.getArguments()
        org.junit.Assume.assumeTrue(args.getString("desktop") == "true")
        val app = InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as RmApplication
        val peer = app.client.connect("10.0.2.2",47642)
        val catalog=app.client.get<Catalog>(peer,"/api/v1/resource-catalog")
        assertEquals(2,catalog.resources.size)
        val file=catalog.resources.single {it.kind=="File"}
        var tag=""
        val bytes=app.client.request(peer,"/api/v1/resources/${file.id}/content").use {assertEquals(200,it.code);tag=it.header("ETag")!!;it.body!!.bytes()}
        assertTrue(bytes.decodeToString().contains("Android 互通"))
        val transfer=Transfer(UUID.randomUUID().toString(),peer.hello.deviceId,file,state="Paused")
        app.store.save(transfer)
        val parts=File(app.filesDir,"transfers/${transfer.id}/parts").apply {mkdirs()}
        val token=Proof.hash("file".toByteArray())
        File(parts,"$token.part").writeBytes(bytes.take(5).toByteArray());File(parts,"$token.etag").writeText(tag)
        app.sharing.value=true
        try {
            app.downloads.resume(Store(app).transfers().single {it.id==transfer.id})
            val deadline=System.currentTimeMillis()+15000
            while(app.store.transfers().single {it.id==transfer.id}.state !in listOf("Completed","Failed") && System.currentTimeMillis()<deadline) Thread.sleep(50)
            val result=app.store.transfers().single {it.id==transfer.id}
            assertEquals(result.error,"Completed",result.state)
            assertArrayEquals(bytes,app.downloads.export(result).readBytes())
        } finally {app.downloads.pauseAll();app.sharing.value=false}
    }

    @Test fun serveForDesktop() {
        org.junit.Assume.assumeTrue(InstrumentationRegistry.getArguments().getString("serve") == "true")
        val app = InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as RmApplication
        val source=File(app.cacheDir,"android-live.txt").apply {writeText("Android -> Windows 中文 😀")}
        val p=Publication("android-live",source.name,Uri.fromFile(source).toString(),false,"Reference")
        app.store.save(p);val server=PeerServer(app)
        try {server.start();Thread.sleep(60000)} finally {server.stop();app.store.remove("publication",p.id);source.delete()}
    }
}

package dev.resourcemanager.protocol

import kotlin.test.*

class LiveDesktopTest {
    @Test fun handshakeCatalogRangeAndPermissions() {
        val configured = System.getenv("RM_INTEROP_PORT")?.toIntOrNull()
        org.junit.jupiter.api.Assumptions.assumeTrue(configured != null,"Set RM_INTEROP_PORT to run the real desktop-node test")
        val port = configured!!
        val identity = JvmIdentity("android-live-${System.nanoTime()}")
        val peers = mutableMapOf<String,Peer>()
        val client = PeerClient(identity,{Hello(identity.deviceId,"Android 中文 + 😀")},{peers[it]},{peers[it.hello.deviceId]=it})
        val peer = client.connect("127.0.0.1",port)
        assertEquals("互通测试电脑 中文 + 😀",peer.hello.nickname)
        val catalog = client.get<Catalog>(peer,"/api/v1/resource-catalog")
        assertEquals(2,catalog.resources.size,"Private group must not leak")
        val file = catalog.resources.single { it.kind == "File" }
        var tag = ""
        val bytes = client.request(peer,"/api/v1/resources/${file.id}/content").use {
            assertEquals(200,it.code); tag=it.header("ETag")!!; it.body!!.bytes()
        }
        client.request(peer,"/api/v1/resources/${file.id}/content",range="bytes=3-",etag=tag).use {
            assertEquals(206,it.code); assertContentEquals(bytes.drop(3).toByteArray(),it.body!!.bytes())
        }
        client.request(peer,"/api/v1/resources/${file.id}/content",range="bytes=3-",etag="\"changed\"").use { assertEquals(200,it.code);assertContentEquals(bytes,it.body!!.bytes()) }
        val folder = catalog.resources.single { it.kind == "Folder" }
        val tree = client.get<List<RemoteFile>>(peer,"/api/v1/resources/${folder.id}/tree")
        assertTrue(tree.any { it.relativePath=="empty" && it.isDirectory })
        assertTrue(tree.any { it.relativePath=="子文件.txt" })
    }
}

package dev.resourcemanager.android

import android.net.Uri
import androidx.test.platform.app.InstrumentationRegistry
import dev.resourcemanager.protocol.*
import java.io.File
import java.util.UUID
import org.junit.Assert.*
import org.junit.Test

class NodeInstrumentedTest {
  @Test
  fun keystoreCioCatalogRangeAndPrivatePermissions() {
    val app =
        InstrumentationRegistry.getInstrumentation().targetContext.applicationContext
            as RmApplication
    val bytes = "Android 实际文件 + 😀 0123456789".toByteArray()
    assertTrue(Proof.verify(app.identity.publicKey, app.identity.sign(bytes), bytes))
    assertEquals(app.identity.deviceId, AndroidIdentity(app).deviceId)
    val file = File(app.cacheDir, "interop-fixture.txt").apply { writeBytes(bytes) }
    val id = UUID.randomUUID().toString()
    val p = Publication(id, file.name, Uri.fromFile(file).toString(), false, "Reference")
    app.store.save(p)
    val server = PeerServer(app)
    val originals = app.nickname to app.identity.publicKey
    val privateIds = mutableListOf<String>()
    val folder = File(app.cacheDir, "private-folder-${UUID.randomUUID()}")
    try {
      server.start()
      val identity = JvmIdentity("test-${UUID.randomUUID()}")
      val peers = mutableMapOf<String, Peer>()
      val client =
          PeerClient(
              identity,
              { Hello(identity.deviceId, "Instrumented 中文 😀") },
              { peers[it] },
              { peers[it.hello.deviceId] = it },
          )
      val remote = client.connect("127.0.0.1", PORT)
      app.nickname = "昵称修改 中文 😀"
      val refreshed = client.connect("127.0.0.1", PORT, force = true)
      assertEquals(remote.hello.deviceId, refreshed.hello.deviceId)
      assertEquals(remote.key, refreshed.key)
      assertEquals(originals.second, AndroidIdentity(app).publicKey)
      assertTrue(
          client.get<Catalog>(remote, "/api/v1/resource-catalog").resources.any { it.id == id }
      )
      var tag = ""
      client.request(remote, "/api/v1/resources/$id/content").use { response ->
        assertEquals(200, response.code)
        tag = response.header("ETag")!!
        assertArrayEquals(bytes, response.body!!.bytes())
      }
      client.request(remote, "/api/v1/resources/$id/content", range = "bytes=3-", etag = tag).use {
          response ->
        assertEquals(206, response.code)
        assertArrayEquals(bytes.drop(3).toByteArray(), response.body!!.bytes())
      }
      val group =
          LocalGroup(
              Group("private-test", "private", createdUtc = java.time.Instant.now().toString()),
              "Private",
          )
      app.store.save(group)
      app.store.save(p.copy(groupId = group.group.id))
      client.request(remote, "/api/v1/resources/$id/content").use { assertEquals(404, it.code) }
      assertFalse(
          client.get<Catalog>(remote, "/api/v1/resource-catalog").resources.any { it.id == id }
      )
      val request =
          ChatRequest(
              messageId = UUID.randomUUID().toString(),
              senderDeviceId = identity.deviceId,
              recipientDeviceId = app.identity.deviceId,
              sentUtc = java.time.Instant.now().toString(),
              kind = "Text",
              text = "你好 Android",
          )
      repeat(2) { attempt ->
        client
            .request(remote, "/api/v1/chat/messages", wire.encodeToString(request).toByteArray())
            .use { response ->
              assertEquals(200, response.code)
              val receipt = wire.decodeFromString<ChatReceipt>(response.body!!.string())
              assertTrue(receipt.accepted)
              assertEquals(attempt == 1, receipt.duplicate)
            }
      }
      client
          .request(
              remote,
              "/api/v1/chat/messages",
              wire.encodeToString(request.copy(text = "changed")).toByteArray(),
          )
          .use { assertEquals(409, it.code) }
      folder.mkdirs()
      File(folder, "empty").mkdirs()
      File(folder, "中文.txt").writeBytes(bytes)
      listOf(file to false, folder to true).forEach { (source, isFolder) ->
        val copied =
            app.files.import(
                Uri.fromFile(source),
                isFolder,
                true,
                "default",
                "",
                identity.deviceId,
                app.store::save,
            )
        privateIds += copied.id
        app.enqueue(identity.deviceId, "PrivateResource", resourceId = copied.id)
        assertTrue(
            app.store.messages().any {
              it.outgoing && it.request.resourceId == copied.id && it.state == "Queued"
            }
        )
        assertFalse(
            client.get<Catalog>(remote, "/api/v1/resource-catalog").resources.any {
              it.id == copied.id
            }
        )
        val prefix = "/api/v1/chat/resources/${copied.id}"
        val resource = client.get<Resource>(remote, prefix, true)
        assertEquals(if (isFolder) "Folder" else "File", resource.kind)
        val tree = client.get<List<RemoteFile>>(remote, "$prefix/tree", true)
        if (isFolder) assertTrue(tree.any { it.isDirectory && it.relativePath == "empty" })
        val path = if (isFolder) "?path=%E4%B8%AD%E6%96%87.txt" else ""
        client.request(remote, "$prefix/content$path", privateResource = true).use {
          assertEquals(200, it.code)
          assertEquals(Proof.hash(bytes), Proof.hash(it.body!!.bytes()))
        }
        client.request(remote, "/api/v1/resources/${copied.id}").use { assertEquals(404, it.code) }
        val stranger = JvmIdentity("stranger-${UUID.randomUUID()}")
        val other = PeerClient(stranger, { Hello(stranger.deviceId, "陌生设备") }, { null }, {})
        val otherRemote = other.connect("127.0.0.1", PORT)
        other.request(otherRemote, "$prefix/content$path", privateResource = true).use {
          assertEquals(404, it.code)
        }
      }
    } finally {
      server.stop()
      app.nickname = originals.first
      app.store.remove("publication", id)
      file.delete()
      privateIds.forEach { app.store.remove("publication", it) }
      folder.deleteRecursively()
    }
  }

  @Test
  fun actualAndroidToDesktop() {
    val args = InstrumentationRegistry.getArguments()
    org.junit.Assume.assumeTrue(args.getString("desktop") == "true")
    val app =
        InstrumentationRegistry.getInstrumentation().targetContext.applicationContext
            as RmApplication
    val peer = app.client.connect("10.0.2.2", 47642)
    val text =
        ChatRequest(
            messageId = UUID.randomUUID().toString(),
            senderDeviceId = app.identity.deviceId,
            recipientDeviceId = peer.hello.deviceId,
            sentUtc = java.time.Instant.now().toString(),
            kind = "Text",
            text = "Android -> Windows 中文 😀",
        )
    app.client.request(peer, "/api/v1/chat/messages", wire.encodeToString(text).toByteArray()).use {
      assertEquals(200, it.code)
      assertTrue(wire.decodeFromString<ChatReceipt>(it.body!!.string()).accepted)
    }
    val catalog = app.client.get<Catalog>(peer, "/api/v1/resource-catalog")
    assertEquals(2, catalog.resources.size)
    val file = catalog.resources.single { it.kind == "File" }
    var tag = ""
    val bytes =
        app.client.request(peer, "/api/v1/resources/${file.id}/content").use {
          assertEquals(200, it.code)
          tag = it.header("ETag")!!
          it.body!!.bytes()
        }
    assertTrue(bytes.decodeToString().contains("Android 互通"))
    val transfer =
        Transfer(UUID.randomUUID().toString(), peer.hello.deviceId, file, state = "Paused")
    app.store.save(transfer)
    val parts = File(app.filesDir, "transfers/${transfer.id}/parts").apply { mkdirs() }
    val token = Proof.hash("file".toByteArray())
    File(parts, "$token.part").writeBytes(bytes.take(5).toByteArray())
    File(parts, "$token.etag").writeText(tag)
    app.sharing.value = true
    try {
      app.downloads.resume(Store(app).transfers().single { it.id == transfer.id })
      val deadline = System.currentTimeMillis() + 15000
      while (
          app.store.transfers().single { it.id == transfer.id }.state !in
              listOf("Completed", "Failed") && System.currentTimeMillis() < deadline
      ) Thread.sleep(50)
      val result = app.store.transfers().single { it.id == transfer.id }
      assertEquals(result.error, "Completed", result.state)
      assertArrayEquals(bytes, app.downloads.export(result).readBytes())
    } finally {
      app.downloads.pauseAll()
      app.sharing.value = false
    }
  }

  @Test
  fun serveForDesktop() {
    org.junit.Assume.assumeTrue(InstrumentationRegistry.getArguments().getString("serve") == "true")
    val app =
        InstrumentationRegistry.getInstrumentation().targetContext.applicationContext
            as RmApplication
    val source =
        File(app.cacheDir, "android-live.txt").apply { writeText("Android -> Windows 中文 😀") }
    val p =
        Publication(
            "android-live",
            source.name,
            Uri.fromFile(source).toString(),
            false,
            "Reference",
        )
    app.store.save(p)
    val server = PeerServer(app)
    try {
      server.start()
      Thread.sleep(60000)
    } finally {
      server.stop()
      app.store.remove("publication", p.id)
      source.delete()
    }
  }
}

package dev.resourcemanager.android

import android.net.Uri
import androidx.test.platform.app.InstrumentationRegistry
import dev.resourcemanager.protocol.*
import java.io.File
import java.time.Instant
import org.junit.Assert.*
import org.junit.Test

/** Two explicit invocations with a test-only process stop between them. */
class ProcessRecoveryTest {
  @Test
  fun recoverPersistedIdentityQueuesPartsAndDraft() {
    val phase = InstrumentationRegistry.getArguments().getString("recovery")
    org.junit.Assume.assumeTrue(phase in listOf("seed", "verify"))
    val app =
        InstrumentationRegistry.getInstrumentation().targetContext.applicationContext
            as RmApplication
    val evidence = File(app.filesDir, "process-recovery-evidence")
    val chunks = File(app.filesDir, "transfers/process-recovery/parts")
    if (phase == "seed") {
      app.stopSharing()
      val peer =
          Peer(
              Hello("recovery-peer", "恢复测试节点"),
              "127.0.0.1",
              JvmIdentity("recovery-peer").publicKey,
          )
      app.store.savePeer(peer)
      val source = File(app.cacheDir, "process-recovery.txt").apply { writeText("保留发布与下载片段 😀") }
      val p =
          Publication(
              "process-recovery",
              source.name,
              Uri.fromFile(source).toString(),
              false,
              "Reference",
          )
      app.store.save(p)
      app.store.save(
          Transfer(
              "process-recovery",
              peer.hello.deviceId,
              app.files.describe(p),
              state = "Running",
              bytes = 4,
          )
      )
      app.store.save(
          Message(
              ChatRequest(
                  messageId = "process-recovery-msg",
                  senderDeviceId = app.identity.deviceId,
                  recipientDeviceId = peer.hello.deviceId,
                  sentUtc = Instant.now().toString(),
                  kind = "Text",
                  text = "等待恢复",
              ),
              true,
              "Sending",
          )
      )
      chunks.mkdirs()
      File(chunks, "test.part").writeText("part")
      File(chunks, "test.etag").writeText("\"stable\"")
      assertTrue(
          app.getSharedPreferences("drafts", 0)
              .edit()
              .putString(peer.hello.deviceId, "未发送草稿\n第二行")
              .commit()
      )
      evidence.writeText(
          listOf(app.identity.deviceId, app.identity.publicKey, peer.key).joinToString("\n")
      )
      return
    }
    val baseline = evidence.readLines()
    assertEquals(baseline[0], app.identity.deviceId)
    assertEquals(baseline[1], app.identity.publicKey)
    assertEquals(baseline[2], app.store.peer("recovery-peer")!!.key)
    assertNotNull(app.store.publication("process-recovery"))
    assertEquals("Paused", app.store.transfers().single { it.id == "process-recovery" }.state)
    assertEquals(
        "Queued",
        app.store.messages().single { it.request.messageId == "process-recovery-msg" }.state,
    )
    assertEquals("part", File(chunks, "test.part").readText())
    assertEquals("\"stable\"", File(chunks, "test.etag").readText())
    assertEquals(
        "未发送草稿\n第二行",
        app.getSharedPreferences("drafts", 0).getString("recovery-peer", null),
    )
    assertFalse(app.sharingWanted.value)
    assertFalse(app.sharing.value)
    app.store.remove("publication", "process-recovery")
    app.store.remove("transfer", "process-recovery")
    app.store.remove("message", "out:recovery-peer:process-recovery-msg")
    app.store.remove("peer", "recovery-peer")
    chunks.parentFile!!.deleteRecursively()
    evidence.delete()
  }
}

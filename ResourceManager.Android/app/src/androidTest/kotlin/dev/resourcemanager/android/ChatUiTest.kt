package dev.resourcemanager.android

import android.graphics.Bitmap
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.compose.runtime.*
import androidx.compose.ui.graphics.asAndroidBitmap
import androidx.compose.ui.semantics.SemanticsActions
import androidx.compose.ui.semantics.SemanticsProperties
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.text.AnnotatedString
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.test.platform.app.InstrumentationRegistry
import dev.resourcemanager.protocol.*
import java.io.File
import java.time.Instant
import org.junit.*
import org.junit.Assert.*

/** Semantic actions on an isolated Activity, never desktop input or a real phone. */
class ChatUiTest {
  @get:Rule val compose = createAndroidComposeRule<ComponentActivity>()
  private val app
    get() =
        InstrumentationRegistry.getInstrumentation().targetContext.applicationContext
            as RmApplication

  private fun render(name: String) {
    val output = File(app.filesDir, "ui-previews").apply { mkdirs() }
    compose
        .onRoot()
        .captureToImage()
        .asAndroidBitmap()
        .compress(Bitmap.CompressFormat.PNG, 100, File(output, "$name.png").outputStream())
  }

  @Test
  fun navigationDraftsComposerAndAttachment() {
    app.stopSharing()
    app.getSharedPreferences("drafts", 0).edit().clear().commit()
    val a = Peer(Hello("ui-a", "设计电脑"), "127.0.0.1", JvmIdentity("ui-a").publicKey)
    val b = Peer(Hello("ui-b", "办公电脑"), "127.0.0.2", JvmIdentity("ui-b").publicKey)
    app.store.peer("ui-a")?.let { app.store.remove("peer", "ui-a") }
    app.store.peer("ui-b")?.let { app.store.remove("peer", "ui-b") }
    app.store.savePeer(a)
    app.store.savePeer(b)
    app.store.save(
        Message(
            ChatRequest(
                messageId = "ui-hello",
                senderDeviceId = a.hello.deviceId,
                recipientDeviceId = app.identity.deviceId,
                sentUtc = Instant.now().toString(),
                kind = "Text",
                text = "项目资料已经准备好。",
            ),
            false,
            "Received",
        )
    )
    app.store.save(
        Message(
            ChatRequest(
                messageId = "ui-b-hello",
                senderDeviceId = b.hello.deviceId,
                recipientDeviceId = app.identity.deviceId,
                sentUtc = Instant.now().toString(),
                kind = "Text",
                text = "稍后同步文件。",
            ),
            false,
            "Received",
        )
    )
    app.changed()
    var requested by mutableStateOf<String?>(null)
    compose.setContent { ResourceTheme { AppUi(app, emptyList(), {}, {}, {}, requested) } }
    compose.waitUntil(10000) {
      compose.onAllNodesWithText("设计电脑").fetchSemanticsNodes().isNotEmpty()
    }
    render("messages")
    compose.runOnIdle { requested = "ui-a" }
    compose.onNode(hasSetTextAction()).performSemanticsAction(SemanticsActions.SetText) {
      it(AnnotatedString("第一行\n第二行\n第三行\n第四行\n第五行\n第六行"))
    }
    compose.onNodeWithContentDescription("发送").assertIsDisplayed()
    render("chat")
    compose.runOnIdle {
      compose.activity.window.setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE)
    }
    compose.onNode(hasSetTextAction()).performSemanticsAction(SemanticsActions.RequestFocus) {
      it()
    }
    compose.runOnIdle {
      WindowInsetsControllerCompat(compose.activity.window, compose.activity.window.decorView)
          .show(WindowInsetsCompat.Type.ime())
    }
    compose.waitUntil(5000) {
      compose.activity.window.decorView.rootWindowInsets?.let {
        WindowInsetsCompat.toWindowInsetsCompat(it).isVisible(WindowInsetsCompat.Type.ime())
      } == true
    }
    compose.onNodeWithContentDescription("发送").assertIsDisplayed()
    compose.onNodeWithContentDescription("发送附件").assertIsDisplayed()
    render("chat-keyboard")
    compose.runOnIdle {
      WindowInsetsControllerCompat(compose.activity.window, compose.activity.window.decorView)
          .hide(WindowInsetsCompat.Type.ime())
    }
    compose.runOnIdle { requested = "ui-b" }
    assertEquals(
        "",
        compose
            .onNode(hasSetTextAction())
            .fetchSemanticsNode()
            .config[SemanticsProperties.EditableText]
            .text,
    )
    compose.runOnIdle { requested = "ui-a" }
    compose.onNodeWithText("第一行\n第二行\n第三行\n第四行\n第五行\n第六行").assertIsDisplayed()
    compose.onNodeWithContentDescription("发送附件").performSemanticsAction(SemanticsActions.OnClick) {
      it()
    }
    compose.onNodeWithText("发送文件夹").assertIsDisplayed()
    compose.onNodeWithText("发送已发布资源").assertIsDisplayed()
    // Close by semantic back action, not key injection.
    compose.onNodeWithText("发送已发布资源").performSemanticsAction(SemanticsActions.OnClick) { it() }
    compose.onNodeWithText("关闭").performSemanticsAction(SemanticsActions.OnClick) { it() }
    compose.onNodeWithContentDescription("返回").performSemanticsAction(SemanticsActions.OnClick) {
      it()
    }
    compose.onNodeWithText("我的发布").performSemanticsAction(SemanticsActions.OnClick) { it() }
    render("publications")
    compose.onNodeWithText("传输").performSemanticsAction(SemanticsActions.OnClick) { it() }
    render("transfers")
    compose.onNodeWithContentDescription("设置").performSemanticsAction(SemanticsActions.OnClick) {
      it()
    }
    render("settings")
    compose.onNodeWithText("后台运行").assertIsDisplayed()
    assertEquals(a.key, app.store.peer("ui-a")!!.key)
  }
}

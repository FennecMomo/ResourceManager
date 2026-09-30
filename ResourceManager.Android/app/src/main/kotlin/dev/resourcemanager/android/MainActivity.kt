package dev.resourcemanager.android

import android.Manifest
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.PowerManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.core.content.FileProvider
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import dev.resourcemanager.protocol.*
import java.time.Instant
import java.util.UUID
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

class MainActivity : ComponentActivity() {
    private val app
        get() = application as RmApplication

    private var incoming by mutableStateOf<List<Uri>>(emptyList())
    private var requestedPeer by mutableStateOf<String?>(null)
    private val notifications =
        registerForActivityResult(ActivityResultContracts.RequestPermission()) {}

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        receiveShare(intent)
        setContent {
            ResourceTheme {
                Surface(Modifier.fillMaxSize()) {
                    AppUi(
                        app,
                        incoming,
                        {
                            incoming = emptyList()
                            intent = Intent(this, MainActivity::class.java)
                        },
                        ::share,
                        ::sharePublication,
                        requestedPeer,
                    )
                }
            }
        }
        if (Build.VERSION.SDK_INT >= 33)
            notifications.launch(Manifest.permission.POST_NOTIFICATIONS)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        receiveShare(intent)
    }

    @Suppress("DEPRECATION")
    private fun receiveShare(intent: Intent?) {
        requestedPeer = intent?.getStringExtra("peer")
        incoming =
            when (intent?.action) {
                    Intent.ACTION_SEND ->
                        listOfNotNull(intent.getParcelableExtra<Uri>(Intent.EXTRA_STREAM))
                    Intent.ACTION_SEND_MULTIPLE ->
                        intent.getParcelableArrayListExtra<Uri>(Intent.EXTRA_STREAM)?.toList()
                            ?: emptyList()
                    else -> emptyList()
                }
                .filter { it.scheme == "content" }
                .take(100)
    }

    private fun share(t: Transfer) = app.action {
        shareFile(app.downloads.export(t))
    }

    private fun sharePublication(p: Publication) = app.action { shareFile(app.files.export(p)) }

    private suspend fun shareFile(file: java.io.File) {
        val uri = FileProvider.getUriForFile(this, "$packageName.files", file)
        withContext(Dispatchers.Main) {
            startActivity(
                Intent.createChooser(
                    Intent(Intent.ACTION_SEND)
                        .setType(contentResolver.getType(uri) ?: "application/octet-stream")
                        .putExtra(Intent.EXTRA_STREAM, uri)
                        .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION),
                    "分享文件",
                )
            )
        }
    }
}

@Composable
fun ResourceTheme(content: @Composable () -> Unit) {
    val dark = isSystemInDarkTheme()
    MaterialTheme(
        colorScheme =
            if (dark)
                darkColorScheme(primary = Color(0xFFAFC6FF), secondaryContainer = Color(0xFF293B59))
            else
                lightColorScheme(
                    primary = Color(0xFF315DA8),
                    primaryContainer = Color(0xFFE8EFFF),
                    secondaryContainer = Color(0xFFEDF2FA),
                    background = Color(0xFFF8F9FC),
                    surface = Color.White,
                    surfaceContainer = Color(0xFFF0F4FA),
                    surfaceContainerLow = Color(0xFFF6F8FC),
                    surfaceContainerHigh = Color(0xFFE6EDF7),
                    onSurface = Color(0xFF172435),
                    onSurfaceVariant = Color(0xFF526079),
                ),
        content = content,
    )
}

private data class UiData(
    val peers: List<Peer> = emptyList(),
    val publications: List<Publication> = emptyList(),
    val groups: List<LocalGroup> = emptyList(),
    val messages: List<Message> = emptyList(),
    val transfers: List<Transfer> = emptyList(),
    val allowed: Set<String> = emptySet(),
)

@Composable
private fun Glyph(name: String, description: String? = null) {
    val id =
        when (name) {
            "chat" -> R.drawable.ic_chat
            "devices" -> R.drawable.ic_devices
            "folder" -> R.drawable.ic_folder
            "download" -> R.drawable.ic_download
            "settings" -> R.drawable.ic_settings
            "back" -> R.drawable.ic_back
            "send" -> R.drawable.ic_send
            else -> R.drawable.ic_add
        }
    Icon(painterResource(id), description)
}

@Composable
private fun Avatar(name: String, avatar: String? = null) {
    val bitmap by
        produceState<android.graphics.Bitmap?>(null, avatar) {
            value =
                withContext(Dispatchers.IO) {
                    runCatching {
                            avatar
                                ?.takeIf { it.length <= PeerClient.HELLO_LIMIT }
                                ?.let { value ->
                                    val bytes = java.util.Base64.getDecoder().decode(value)
                                    android.graphics.BitmapFactory.decodeByteArray(
                                        bytes,
                                        0,
                                        bytes.size,
                                    )
                                }
                        }
                        .getOrNull()
                }
        }
    Box(
        Modifier.size(44.dp).background(MaterialTheme.colorScheme.secondaryContainer, CircleShape),
        contentAlignment = Alignment.Center,
    ) {
        if (bitmap != null)
            Image(bitmap!!.asImageBitmap(), null, Modifier.fillMaxSize().clip(CircleShape))
        else
            Text(
                name.take(1).ifBlank { "?" },
                style = MaterialTheme.typography.titleMedium,
                color = MaterialTheme.colorScheme.primary,
            )
    }
}

@Composable
internal fun Empty(title: String, detail: String) {
    Column(
        Modifier.fillMaxWidth().padding(vertical = 48.dp, horizontal = 24.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        Glyph("chat")
        Text(title, style = MaterialTheme.typography.titleMedium)
        Text(
            detail,
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun AppUi(
    app: RmApplication,
    incoming: List<Uri>,
    clearIncoming: () -> Unit,
    share: (Transfer) -> Unit,
    sharePublication: (Publication) -> Unit,
    requestedPeer: String?,
) {
    val revision by app.revision.collectAsStateWithLifecycle()
    val status by app.status.collectAsStateWithLifecycle()
    val wanted by app.sharingWanted.collectAsStateWithLifecycle()
    val sharing by app.sharing.collectAsStateWithLifecycle()
    val service by app.serviceState.collectAsStateWithLifecycle()
    val busy by app.busy.collectAsStateWithLifecycle()
    val candidates by app.network.candidates.collectAsStateWithLifecycle()
    var page by rememberSaveable { mutableIntStateOf(0) }
    var selectedTab by rememberSaveable { mutableIntStateOf(0) }
    var peerId by rememberSaveable { mutableStateOf<String?>(null) }
    var resourceReturn by rememberSaveable { mutableIntStateOf(1) }
    var host by rememberSaveable { mutableStateOf("") }
    var nickname by rememberSaveable { mutableStateOf(app.nickname) }
    val drafts = remember {
        app.getSharedPreferences("drafts", android.content.Context.MODE_PRIVATE)
    }
    var chat by
        rememberSaveable(peerId) { mutableStateOf(drafts.getString(peerId ?: "", "") ?: "") }
    var search by rememberSaveable(page, peerId) { mutableStateOf("") }
    var note by rememberSaveable { mutableStateOf("") }
    var groupId by rememberSaveable { mutableStateOf("default") }
    var groupName by rememberSaveable { mutableStateOf("") }
    var copy by rememberSaveable { mutableStateOf(false) }
    var advanced by rememberSaveable { mutableStateOf(false) }
    var privateRecipient by rememberSaveable { mutableStateOf<String?>(null) }
    var pendingUris by rememberSaveable { mutableStateOf(arrayListOf<String>()) }
    var pendingFolder by rememberSaveable { mutableStateOf(false) }
    var addDevice by remember { mutableStateOf(false) }
    var attachment by remember { mutableStateOf(false) }
    var publicationPicker by remember { mutableStateOf(false) }
    var sourceBrowser by remember { mutableStateOf(false) }
    var incomingRecipient by rememberSaveable { mutableStateOf<String?>(null) }
    var exporting by remember { mutableStateOf<Transfer?>(null) }
    var storageBytes by remember { mutableLongStateOf(0) }
    var exempt by remember { mutableStateOf(false) }
    val context = androidx.compose.ui.platform.LocalContext.current
    val data by
        produceState(UiData(), revision, peerId) {
            value =
                withContext(Dispatchers.IO) {
                    UiData(
                        app.store.peers(),
                        app.store.publications(),
                        app.store.groups(),
                        app.store.messages(),
                        app.store.transfers(),
                        app.store
                            .publications()
                            .filter { peerId != null && app.store.allowed(it, peerId!!) }
                            .map { it.id }
                            .toSet(),
                    )
                }
        }
    val peer = data.peers.find { it.hello.deviceId == peerId }
    val public = data.publications.filter { it.recipient == null }
    val messages =
        data.messages.filter {
            if (it.outgoing) it.request.recipientDeviceId == peerId
            else it.request.senderDeviceId == peerId
        }
    fun openChat(id: String) {
        peerId = id
        page = 5
    }
    fun resources() {
        resourceReturn = page
        page = 6
    }
    fun back() {
        page = if (page == 6) resourceReturn else selectedTab
    }
    BackHandler(page > 3) { back() }
    LaunchedEffect(requestedPeer) { if (requestedPeer != null) openChat(requestedPeer) }
    LaunchedEffect(page, revision) {
        if (page == 4) {
            exempt =
                context
                    .getSystemService(PowerManager::class.java)
                    .isIgnoringBatteryOptimizations(context.packageName)
            storageBytes =
                withContext(Dispatchers.IO) {
                    app.filesDir.walkTopDown().filter { it.isFile }.sumOf { it.length() }
                }
        }
    }
    val lifecycle = androidx.lifecycle.compose.LocalLifecycleOwner.current.lifecycle
    DisposableEffect(lifecycle) {
        val observer =
            androidx.lifecycle.LifecycleEventObserver { _, event ->
                if (event == androidx.lifecycle.Lifecycle.Event.ON_RESUME)
                    exempt =
                        context
                            .getSystemService(PowerManager::class.java)
                            .isIgnoringBatteryOptimizations(context.packageName)
            }
        lifecycle.addObserver(observer)
        onDispose { lifecycle.removeObserver(observer) }
    }
    fun selected(uris: List<Uri>, folder: Boolean) {
        uris.forEach {
            runCatching {
                context.contentResolver.takePersistableUriPermission(
                    it,
                    Intent.FLAG_GRANT_READ_URI_PERMISSION,
                )
            }
        }
        val recipient = privateRecipient
        if (recipient != null) {
            uris.forEach { app.publish(it, folder, true, groupId, "", recipient) }
            privateRecipient = null
            return
        }
        pendingUris = ArrayList(uris.map(Uri::toString))
        pendingFolder = folder
        page = 7
    }
    val filesPicker =
        androidx.activity.compose.rememberLauncherForActivityResult(
            ActivityResultContracts.OpenMultipleDocuments()
        ) {
            if (it.isNotEmpty()) selected(it, false) else privateRecipient = null
        }
    val treePicker =
        androidx.activity.compose.rememberLauncherForActivityResult(
            ActivityResultContracts.OpenDocumentTree()
        ) {
            if (it != null) selected(listOf(it), true) else privateRecipient = null
        }
    val exportPicker =
        androidx.activity.compose.rememberLauncherForActivityResult(
            ActivityResultContracts.CreateDocument("application/octet-stream")
        ) { uri ->
            val task = exporting
            exporting = null
            if (uri != null && task != null)
                app.action {
                    app.downloads.export(task).inputStream().use { input ->
                        requireNotNull(context.contentResolver.openOutputStream(uri, "wt")).use {
                            input.copyTo(it)
                        }
                    }
                    app.status.value = "文件已保存"
                }
        }
    val diagnosticsPicker =
        androidx.activity.compose.rememberLauncherForActivityResult(
            ActivityResultContracts.CreateDocument("text/plain")
        ) { uri ->
            if (uri != null)
                app.action {
                    val file = java.io.File(app.filesDir, "diagnostics.log")
                    requireNotNull(context.contentResolver.openOutputStream(uri, "wt")).use { output
                        ->
                        if (file.exists()) file.inputStream().use { it.copyTo(output) }
                    }
                    app.status.value = "诊断已导出"
                }
        }
    val snackbar = remember { SnackbarHostState() }
    LaunchedEffect(status) { if (status != "共享已停止") snackbar.showSnackbar(status) }
    val title =
        when (page) {
            0 -> "消息"
            1 -> "设备"
            2 -> "我的发布"
            3 -> "传输"
            4 -> "设置"
            5 -> peer?.hello?.nickname ?: "会话"
            6 -> "${peer?.hello?.nickname ?: "设备"}的资源"
            else -> "发布资源"
        }
    Scaffold(
        modifier = Modifier.imePadding(),
        snackbarHost = { SnackbarHost(snackbar) },
        topBar = {
            TopAppBar(
                title = { Text(title, maxLines = 1, overflow = TextOverflow.Ellipsis) },
                navigationIcon = {
                    if (page > 3) IconButton(onClick = ::back) { Glyph("back", "返回") }
                },
                actions = {
                    if (page == 5) TextButton(onClick = ::resources) { Text("资源") }
                    if (page <= 3) IconButton(onClick = { page = 4 }) { Glyph("settings", "设置") }
                },
            )
        },
        bottomBar = {
            if (page == 5 && peer != null)
                Surface(tonalElevation = 3.dp) {
                    Column(
                        Modifier.navigationBarsPadding().padding(horizontal = 8.dp, vertical = 6.dp)
                    ) {
                        if (!sharing)
                            TextButton(onClick = { app.startSharing() }) { Text("启动共享以收发消息") }
                        Row(verticalAlignment = Alignment.Bottom) {
                            IconButton(onClick = { attachment = true }) { Glyph("add", "发送附件") }
                            OutlinedTextField(
                                chat,
                                {
                                    chat = it.take(2000)
                                    peerId?.let { id -> drafts.edit().putString(id, chat).apply() }
                                },
                                placeholder = { Text("发送消息") },
                                modifier = Modifier.weight(1f),
                                minLines = 1,
                                maxLines = 5,
                                shape = RoundedCornerShape(24.dp),
                            )
                            IconButton(
                                enabled = chat.isNotBlank(),
                                onClick = {
                                    val text = chat
                                    val id = peer.hello.deviceId
                                    chat = ""
                                    drafts.edit().remove(id).apply()
                                    app.action { app.enqueue(id, "Text", text) }
                                },
                            ) {
                                Glyph("send", "发送")
                            }
                        }
                    }
                }
            else if (page <= 3)
                NavigationBar {
                    listOf(
                            "消息" to "chat",
                            "设备" to "devices",
                            "我的发布" to "folder",
                            "传输" to "download",
                        )
                        .forEachIndexed { index, (label, icon) ->
                            NavigationBarItem(
                                selected = selectedTab == index,
                                onClick = {
                                    selectedTab = index
                                    page = index
                                },
                                icon = { Glyph(icon) },
                                label = { Text(label) },
                            )
                        }
                }
        },
        floatingActionButton = {
            if (page == 1 || page == 2)
                FloatingActionButton(
                    onClick = {
                        if (page == 1) addDevice = true
                        else {
                            privateRecipient = null
                            pendingUris = arrayListOf()
                            page = 7
                        }
                    }
                ) {
                    Glyph("add", if (page == 1) "添加设备" else "发布资源")
                }
        },
    ) { padding ->
        if (page == 5) {
            val listState = rememberLazyListState()
            LaunchedEffect(messages.size, peerId) {
                if (messages.isNotEmpty()) listState.animateScrollToItem(messages.size - 1)
            }
            LazyColumn(
                state = listState,
                modifier = Modifier.padding(padding).fillMaxSize().padding(horizontal = 12.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp),
                contentPadding = PaddingValues(vertical = 16.dp),
            ) {
                if (messages.isEmpty()) item { Empty("开始一段对话", "发送消息、文件或文件夹，资源也可以直接分享给对方。") }
                items(messages, key = { "${it.outgoing}:${it.request.messageId}" }) { message ->
                    MessageBubble(
                        app,
                        message,
                        peer,
                        data.publications.find { it.id == message.request.resourceId },
                    )
                }
            }
        } else if (page == 6)
            key(peerId) { ResourcePage(Modifier.padding(padding), app, peer, search, { search = it }) }
        else
            LazyColumn(
                Modifier.padding(padding).fillMaxSize().padding(horizontal = 16.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp),
                contentPadding = PaddingValues(top = 8.dp, bottom = 88.dp),
            ) {
                if (page <= 3)
                    item {
                        Surface(
                            color = MaterialTheme.colorScheme.secondaryContainer,
                            shape = RoundedCornerShape(16.dp),
                        ) {
                            Row(
                                Modifier.fillMaxWidth()
                                    .padding(horizontal = 16.dp, vertical = 8.dp),
                                verticalAlignment = Alignment.CenterVertically,
                            ) {
                                Column(Modifier.weight(1f)) {
                                    Text(service, style = MaterialTheme.typography.labelLarge)
                                    Text(
                                        if (sharing) "可接收消息与文件" else "开启后允许其他设备连接",
                                        style = MaterialTheme.typography.bodySmall,
                                    )
                                }
                                TextButton(
                                    onClick = {
                                        if (wanted) app.stopSharing() else app.startSharing()
                                    }
                                ) {
                                    Text(if (wanted) "停止" else "开启")
                                }
                            }
                        }
                    }
                when (page) {
                    0 -> {
                        val conversations =
                            data.peers
                                .filter { p ->
                                    data.messages.any {
                                        it.request.senderDeviceId == p.hello.deviceId ||
                                            it.request.recipientDeviceId == p.hello.deviceId
                                    }
                                }
                                .sortedByDescending { p ->
                                    data.messages
                                        .lastOrNull {
                                            it.request.senderDeviceId == p.hello.deviceId ||
                                                it.request.recipientDeviceId == p.hello.deviceId
                                        }
                                        ?.request
                                        ?.sentUtc
                                }
                        item {
                            TextButton(
                                onClick = {
                                    selectedTab = 1
                                    page = 1
                                }
                            ) {
                                Text("选择设备，开始聊天")
                            }
                        }
                        if (conversations.isEmpty())
                            item { Empty("还没有会话", "在同一 Wi-Fi 下发现设备，或者手动添加电脑地址。") }
                        items(conversations, key = { it.hello.deviceId }) { p ->
                            val last =
                                data.messages.lastOrNull {
                                    it.request.senderDeviceId == p.hello.deviceId ||
                                        it.request.recipientDeviceId == p.hello.deviceId
                                }
                            Card(
                                onClick = { openChat(p.hello.deviceId) },
                                colors =
                                    CardDefaults.cardColors(
                                        containerColor = MaterialTheme.colorScheme.surface
                                    ),
                            ) {
                                Row(
                                    Modifier.fillMaxWidth().padding(14.dp),
                                    verticalAlignment = Alignment.CenterVertically,
                                    horizontalArrangement = Arrangement.spacedBy(12.dp),
                                ) {
                                    Avatar(p.hello.nickname, p.hello.avatar)
                                    Column(Modifier.weight(1f)) {
                                        Row {
                                            Text(
                                                p.hello.nickname,
                                                modifier = Modifier.weight(1f),
                                                style = MaterialTheme.typography.titleMedium,
                                            )
                                            Text(
                                                last?.request?.sentUtc?.let(::timeLabel) ?: "",
                                                style = MaterialTheme.typography.labelSmall,
                                            )
                                        }
                                        Text(
                                            last?.request?.text ?: "[文件资源]",
                                            maxLines = 1,
                                            overflow = TextOverflow.Ellipsis,
                                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                                        )
                                        Text(
                                            if (
                                                p.error == null &&
                                                    System.currentTimeMillis() - p.lastSeen < 120000
                                            )
                                                "最近在线"
                                            else "离线，可排队发送",
                                            style = MaterialTheme.typography.labelSmall,
                                        )
                                    }
                                }
                            }
                        }
                    }
                    1 -> {
                        item {
                            Row(verticalAlignment = Alignment.CenterVertically) {
                                Text(
                                    "同一网络中的设备",
                                    modifier = Modifier.weight(1f),
                                    style = MaterialTheme.typography.titleSmall,
                                )
                                TextButton(enabled = !busy, onClick = { app.refresh() }) {
                                    Text(if (busy) "发现中…" else "刷新")
                                }
                            }
                            if (busy) LinearProgressIndicator(Modifier.fillMaxWidth())
                        }
                        if (data.peers.isEmpty() && candidates.isEmpty())
                            item { Empty("尚未发现设备", "让电脑与手机连接同一 Wi-Fi，并在两端开启共享。") }
                        items(data.peers, key = { it.hello.deviceId }) { p ->
                            Card(
                                colors =
                                    CardDefaults.cardColors(
                                        containerColor = MaterialTheme.colorScheme.surface
                                    )
                            ) {
                                Column(Modifier.padding(14.dp)) {
                                    Row(
                                        verticalAlignment = Alignment.CenterVertically,
                                        horizontalArrangement = Arrangement.spacedBy(12.dp),
                                    ) {
                                        Avatar(p.hello.nickname, p.hello.avatar)
                                        Column(Modifier.weight(1f)) {
                                            Text(
                                                p.hello.nickname,
                                                style = MaterialTheme.typography.titleMedium,
                                            )
                                            Text(
                                                candidates[p.hello.deviceId]?.state
                                                    ?: if (
                                                        p.error == null &&
                                                            System.currentTimeMillis() -
                                                                p.lastSeen < 120000
                                                    )
                                                        "最近在线"
                                                    else "离线",
                                                style = MaterialTheme.typography.bodySmall,
                                            )
                                            Text(
                                                p.host,
                                                style = MaterialTheme.typography.labelSmall,
                                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                                            )
                                        }
                                    }
                                    Row {
                                        TextButton(onClick = { openChat(p.hello.deviceId) }) {
                                            Text("发消息")
                                        }
                                        TextButton(
                                            onClick = {
                                                peerId = p.hello.deviceId
                                                resources()
                                            }
                                        ) {
                                            Text("查看资源")
                                        }
                                    }
                                }
                            }
                        }
                        items(
                            candidates.values.filter { c ->
                                data.peers.none { it.hello.deviceId == c.id }
                            },
                            key = { "candidate:${it.id}" },
                        ) { c ->
                            ListItem(
                                headlineContent = { Text(c.name) },
                                supportingContent = { Text(c.state) },
                                leadingContent = { Avatar(c.name) },
                            )
                        }
                    }
                    2 -> {
                        item {
                            OutlinedTextField(
                                search,
                                { search = it },
                                placeholder = { Text("搜索发布名称或备注") },
                                singleLine = true,
                                modifier = Modifier.fillMaxWidth(),
                            )
                        }
                        if (public.isEmpty()) item { Empty("把资源分享出去", "点击右下角发布，选择文件或文件夹。") }
                        items(
                            public.filter {
                                it.name.contains(search, true) || it.note.contains(search, true)
                            },
                            key = { it.id },
                        ) { p ->
                            Card {
                                Column(Modifier.fillMaxWidth().padding(16.dp)) {
                                    Text(p.name, style = MaterialTheme.typography.titleMedium)
                                    Text(
                                        "${if(p.folder) "文件夹" else "文件"} · ${if(p.mode=="Copy") "应用内副本" else "引用原文件"}",
                                        style = MaterialTheme.typography.bodySmall,
                                    )
                                    if (p.note.isNotBlank()) Text(p.note)
                                    Row {
                                        TextButton(onClick = { sharePublication(p) }) { Text("分享") }
                                        TextButton(
                                            onClick = {
                                                app.action { app.store.remove("publication", p.id) }
                                            }
                                        ) {
                                            Text("撤销发布")
                                        }
                                    }
                                }
                            }
                        }
                    }
                    3 -> {
                        if (data.transfers.isEmpty())
                            item { Empty("暂无传输", "从聊天或设备资源页下载，进度会显示在这里。") }
                        items(
                            data.transfers.sortedBy { it.state == "Completed" },
                            key = { it.id },
                        ) { t ->
                            Card {
                                Column(
                                    Modifier.fillMaxWidth().padding(16.dp),
                                    verticalArrangement = Arrangement.spacedBy(8.dp),
                                ) {
                                    Text(
                                        t.resource.name,
                                        style = MaterialTheme.typography.titleMedium,
                                    )
                                    Text(
                                        "${transferState(t.state)} · ${size(t.bytes)} / ${size(t.total)}",
                                        style = MaterialTheme.typography.bodySmall,
                                    )
                                    if (t.total > 0)
                                        LinearProgressIndicator(
                                            progress = {
                                                (t.bytes.toFloat() / t.total).coerceIn(0f, 1f)
                                            },
                                            modifier = Modifier.fillMaxWidth(),
                                        )
                                    t.error?.let {
                                        Text(it, color = MaterialTheme.colorScheme.error)
                                    }
                                    Row {
                                        if (t.state == "Running")
                                            TextButton(onClick = { app.downloads.pause(t.id) }) {
                                                Text("暂停")
                                            }
                                        else if (t.state != "Completed")
                                            TextButton(
                                                onClick = { app.action { app.downloads.resume(t) } }
                                            ) {
                                                Text(if (t.state == "Failed") "重试" else "继续")
                                            }
                                        if (t.state == "Completed") {
                                            TextButton(onClick = { share(t) }) { Text("分享") }
                                            TextButton(
                                                onClick = {
                                                    exporting = t
                                                    exportPicker.launch(
                                                        t.resource.name +
                                                            if (t.resource.kind == "Folder") ".zip"
                                                            else ""
                                                    )
                                                }
                                            ) {
                                                Text("保存到…")
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                    4 -> {
                        item {
                            Text("本机资料", style = MaterialTheme.typography.titleMedium)
                            OutlinedTextField(
                                nickname,
                                { nickname = it.take(80) },
                                label = { Text("昵称") },
                                modifier = Modifier.fillMaxWidth(),
                            )
                            TextButton(
                                enabled = nickname.isNotBlank(),
                                onClick = { app.nickname = nickname },
                            ) {
                                Text("保存昵称")
                            }
                        }
                        item {
                            Card {
                                Column(
                                    Modifier.padding(16.dp),
                                    verticalArrangement = Arrangement.spacedBy(8.dp),
                                ) {
                                    Text("后台运行", style = MaterialTheme.typography.titleMedium)
                                    Text("共享状态：$service")
                                    Text(if (exempt) "系统电池优化：已豁免" else "系统电池优化：仍受限制")
                                    Text(
                                        "持续共享会增加耗电。熄屏收发需要系统允许后台联网；其他系统限制无法自动检测。",
                                        style = MaterialTheme.typography.bodySmall,
                                    )
                                    TextButton(
                                        onClick = {
                                            context.startActivity(
                                                Intent(
                                                    android.provider.Settings
                                                        .ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS
                                                )
                                            )
                                        }
                                    ) {
                                        Text("打开电池优化设置")
                                    }
                                    TextButton(
                                        onClick = {
                                            context.startActivity(
                                                Intent(
                                                    android.provider.Settings
                                                        .ACTION_APPLICATION_DETAILS_SETTINGS,
                                                    Uri.parse("package:${context.packageName}"),
                                                )
                                            )
                                        }
                                    ) {
                                        Text("应用系统设置")
                                    }
                                    TextButton(
                                        onClick = {
                                            context.startActivity(
                                                Intent(
                                                        android.provider.Settings
                                                            .ACTION_APP_NOTIFICATION_SETTINGS
                                                    )
                                                    .putExtra(
                                                        android.provider.Settings.EXTRA_APP_PACKAGE,
                                                        context.packageName,
                                                    )
                                            )
                                        }
                                    ) {
                                        Text("通知设置")
                                    }
                                }
                            }
                        }
                        item {
                            Text("存储与诊断", style = MaterialTheme.typography.titleMedium)
                            Text("应用内文件：${size(storageBytes)}")
                            TextButton(
                                onClick = {
                                    app.action {
                                        val released =
                                            app.files.cleanUnusedCopies {
                                                app.store.publications().map { it.id }.toSet()
                                            }
                                        app.status.value = "已清理 ${size(released)}"
                                    }
                                }
                            ) {
                                Text("清理无引用副本")
                            }
                            TextButton(
                                onClick = {
                                    diagnosticsPicker.launch("ResourceManager-diagnostics.txt")
                                }
                            ) {
                                Text("导出诊断记录")
                            }
                            Text(
                                "ResourceManager 0.1.1 · Android 10+",
                                style = MaterialTheme.typography.bodySmall,
                            )
                            Text(
                                "设备 ID：${app.identity.deviceId}",
                                style = MaterialTheme.typography.bodySmall,
                            )
                            Text(
                                "局域网 HTTP 内容未加密，请在可信网络使用。",
                                style = MaterialTheme.typography.bodySmall,
                            )
                        }
                    }
                    6 -> {
                        item {
                            ResourcePage(
                                app = app,
                                peer = peer,
                                search = search,
                                onSearch = { search = it },
                            )
                        }
                    }
                    7 -> {
                        item {
                            Text(
                                if (pendingUris.isEmpty()) "先选择要发布的内容"
                                else "已选择 ${pendingUris.size} 项",
                                style = MaterialTheme.typography.titleMedium,
                            )
                            Row {
                                TextButton(
                                    onClick = {
                                        privateRecipient = null
                                        filesPicker.launch(arrayOf("*/*"))
                                    }
                                ) {
                                    Text("选择文件")
                                }
                                TextButton(
                                    onClick = {
                                        privateRecipient = null
                                        treePicker.launch(null)
                                    }
                                ) {
                                    Text("选择文件夹")
                                }
                            }
                            TextButton(onClick = { sourceBrowser = true }) {
                                Text("浏览已授权目录 / 按类型筛选")
                            }
                        }
                        item {
                            OutlinedTextField(
                                note,
                                { note = it.take(2000) },
                                label = { Text("备注（可选）") },
                                modifier = Modifier.fillMaxWidth(),
                                minLines = 1,
                                maxLines = 5,
                            )
                            TextButton(onClick = { advanced = !advanced }) {
                                Text(if (advanced) "收起高级设置" else "分组、权限与存储方式")
                            }
                        }
                        if (advanced)
                            item {
                                Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                                    Row(verticalAlignment = Alignment.CenterVertically) {
                                        Checkbox(copy, { copy = it })
                                        Text("复制到应用内")
                                    }
                                    Text(
                                        "无法保留原文件授权时自动复制。",
                                        style = MaterialTheme.typography.bodySmall,
                                    )
                                    data.groups.forEach { g ->
                                        FilterChip(
                                            selected = groupId == g.group.id,
                                            onClick = { groupId = g.group.id },
                                            label = { Text(g.group.name) },
                                        )
                                    }
                                    OutlinedTextField(
                                        groupName,
                                        { groupName = it.take(80) },
                                        label = { Text("新建分组") },
                                    )
                                    TextButton(
                                        enabled = groupName.isNotBlank(),
                                        onClick = {
                                            val name = groupName
                                            groupName = ""
                                            app.action {
                                                app.store.save(
                                                    LocalGroup(
                                                        Group(
                                                            UUID.randomUUID().toString(),
                                                            name,
                                                            createdUtc = Instant.now().toString(),
                                                        )
                                                    )
                                                )
                                            }
                                        },
                                    ) {
                                        Text("添加分组")
                                    }
                                    data.groups
                                        .find { it.group.id == groupId }
                                        ?.let { g ->
                                            Text("所选分组权限（影响该分组已有发布）")
                                            listOf(
                                                    "Public" to "公开",
                                                    "Private" to "仅自己",
                                                    "AllowList" to "指定设备",
                                                )
                                                .forEach { (value, label) ->
                                                    FilterChip(
                                                        selected = g.access == value,
                                                        onClick = {
                                                            app.action {
                                                                app.store.save(
                                                                    g.copy(access = value)
                                                                )
                                                            }
                                                        },
                                                        label = { Text(label) },
                                                    )
                                                }
                                            if (g.access == "AllowList")
                                                data.peers.forEach { p ->
                                                    Row(
                                                        verticalAlignment =
                                                            Alignment.CenterVertically
                                                    ) {
                                                        Checkbox(
                                                            p.hello.deviceId in g.devices,
                                                            { checked ->
                                                                app.action {
                                                                    app.store.save(
                                                                        g.copy(
                                                                            devices =
                                                                                if (checked)
                                                                                    (g.devices +
                                                                                            p.hello
                                                                                                .deviceId)
                                                                                        .distinct()
                                                                                else
                                                                                    g.devices -
                                                                                        p.hello
                                                                                            .deviceId
                                                                        )
                                                                    )
                                                                }
                                                            },
                                                        )
                                                        Text(p.hello.nickname)
                                                    }
                                                }
                                        }
                                }
                            }
                        item {
                            Button(
                                enabled = pendingUris.isNotEmpty(),
                                onClick = {
                                    pendingUris.forEach { value ->
                                        val uri = Uri.parse(value)
                                        val granted =
                                            context.contentResolver.persistedUriPermissions.any {
                                                it.uri == uri && it.isReadPermission
                                            }
                                        app.publish(
                                            uri,
                                            pendingFolder,
                                            copy || !granted,
                                            groupId,
                                            note,
                                        )
                                    }
                                    pendingUris = arrayListOf()
                                    selectedTab = 2
                                    page = 2
                                },
                                modifier = Modifier.fillMaxWidth(),
                            ) {
                                Text("确认发布")
                            }
                        }
                    }
                }
            }
    }
    if (addDevice)
        AlertDialog(
            onDismissRequest = { addDevice = false },
            title = { Text("添加设备") },
            text = {
                OutlinedTextField(
                    host,
                    { host = it },
                    label = { Text("IP 地址[:端口]") },
                    singleLine = true,
                )
            },
            confirmButton = {
                TextButton(
                    onClick = {
                        val parts = host.trim().split(':')
                        app.connect(parts[0], parts.getOrNull(1)?.toIntOrNull() ?: PORT)
                        addDevice = false
                    }
                ) {
                    Text("连接")
                }
            },
            dismissButton = { TextButton(onClick = { addDevice = false }) { Text("取消") } },
        )
    if (attachment)
        ModalBottomSheet(onDismissRequest = { attachment = false }) {
            Column(Modifier.padding(20.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text("发送附件", style = MaterialTheme.typography.titleLarge)
                TextButton(
                    onClick = {
                        attachment = false
                        privateRecipient = peerId
                        filesPicker.launch(arrayOf("*/*"))
                    }
                ) {
                    Text("发送文件")
                }
                TextButton(
                    onClick = {
                        attachment = false
                        privateRecipient = peerId
                        treePicker.launch(null)
                    }
                ) {
                    Text("发送文件夹")
                }
                TextButton(
                    onClick = {
                        attachment = false
                        publicationPicker = true
                    }
                ) {
                    Text("发送已发布资源")
                }
            }
        }
    if (publicationPicker)
        AlertDialog(
            onDismissRequest = { publicationPicker = false },
            title = { Text("选择已发布资源") },
            text = {
                LazyColumn {
                    items(public.filter { it.id in data.allowed }) { p ->
                        TextButton(
                            onClick = {
                                val id = peerId!!
                                app.action { app.enqueue(id, "Resource", resourceId = p.id) }
                                publicationPicker = false
                            }
                        ) {
                            Text(p.name)
                        }
                    }
                    if (public.none { it.id in data.allowed }) item { Text("没有允许该设备访问的发布") }
                }
            },
            confirmButton = { TextButton(onClick = { publicationPicker = false }) { Text("关闭") } },
        )
    if (sourceBrowser)
        ModalBottomSheet(onDismissRequest = { sourceBrowser = false }) {
            SourceBrowser(app) { uris, folder ->
                selected(uris, folder)
                sourceBrowser = false
            }
        }
    if (incoming.isNotEmpty())
        AlertDialog(
            onDismissRequest = clearIncoming,
            title = { Text("发送 ${incoming.size} 个文件") },
            text = {
                LazyColumn {
                    item { Text("选择接收会话，文件将私发给该设备。") }
                    items(data.peers) { p ->
                        FilterChip(
                            selected = incomingRecipient == p.hello.deviceId,
                            onClick = { incomingRecipient = p.hello.deviceId },
                            label = { Text(p.hello.nickname) },
                        )
                    }
                    if (data.peers.isEmpty()) item { Text("请先添加设备，再从其他应用分享。") }
                }
            },
            confirmButton = {
                TextButton(
                    enabled = incomingRecipient != null,
                    onClick = {
                        val id = incomingRecipient!!
                        incoming.forEach { app.publish(it, false, true, "default", "", id) }
                        clearIncoming()
                        openChat(id)
                    },
                ) {
                    Text("确认发送")
                }
            },
            dismissButton = { TextButton(onClick = clearIncoming) { Text("取消") } },
        )
}

@Composable
internal fun MessageBubble(app: RmApplication, m: Message, peer: Peer?, publication: Publication?) {
    val resource by
        produceState<Resource?>(null, m.request.messageId, peer?.hello?.deviceId) {
            if (m.request.resourceId != null)
                value =
                    withContext(Dispatchers.IO) {
                        val id = m.request.resourceId
                        val cacheKey = "${peer?.hello?.deviceId}:$id"
                        val cached =
                            app.store.get("resource-preview", cacheKey)?.let {
                                wire.decodeFromString<Resource>(it)
                            }
                        try {
                            val found =
                                if (publication != null) app.files.describe(publication)
                                else if (!m.outgoing && peer != null) {
                                    val private = m.request.kind == "PrivateResource"
                                    val prefix =
                                        if (private) "/api/v1/chat/resources/"
                                        else "/api/v1/resources/"
                                    app.client.networkIO {
                                        app.client.get<Resource>(peer, prefix + id, private)
                                    }
                                } else cached
                            if (found != null)
                                app.store.put(
                                    "resource-preview",
                                    cacheKey,
                                    wire.encodeToString(found),
                                )
                            found
                        } catch (e: Exception) {
                            if (e is kotlinx.coroutines.CancellationException) throw e
                            cached
                        }
                    }
        }
    Row(
        Modifier.fillMaxWidth(),
        horizontalArrangement = if (m.outgoing) Arrangement.End else Arrangement.Start,
    ) {
        Surface(
            shape = RoundedCornerShape(18.dp),
            color =
                if (m.outgoing) MaterialTheme.colorScheme.primaryContainer
                else MaterialTheme.colorScheme.surfaceContainer,
            modifier = Modifier.widthIn(max = 300.dp),
        ) {
            Column(Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                Text(
                    m.request.text
                        ?: resource?.name
                        ?: publication?.name
                        ?: if (m.request.kind == "PrivateResource") "私发文件资源" else "分享的资源",
                    style = MaterialTheme.typography.bodyLarge,
                )
                if (m.request.resourceId != null) {
                    Text(
                        "${if(resource?.kind=="Folder" || publication?.folder==true) "文件夹" else "文件"}${resource?.let { " · ${size(it.size)}" } ?: " · 详情待加载"}",
                        style = MaterialTheme.typography.labelMedium,
                    )
                }
                Text(
                    "${timeLabel(m.request.sentUtc)} · ${when(m.state) {"Delivered"->"已送达"
"Received"->"已收到"
"Queued"->"等待送达"
"Sending"->"发送中"
"Canceled"->"已取消"
else->"发送失败"}}",
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                m.error?.let {
                    Text(
                        it,
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.error,
                    )
                }
                if (m.outgoing && m.state == "Queued")
                    TextButton(
                        onClick = {
                            app.action {
                                if (
                                    app.store.transition(m, "Queued", "Canceled") &&
                                        m.request.kind == "PrivateResource"
                                )
                                    m.request.resourceId?.let {
                                        app.store.remove("publication", it)
                                    }
                            }
                        }
                    ) {
                        Text("取消发送")
                    }
                if (m.outgoing && m.state == "Failed")
                    TextButton(
                        onClick = { app.action { app.store.transition(m, "Failed", "Queued") } }
                    ) {
                        Text("重试")
                    }
                if (!m.outgoing && m.request.resourceId != null && peer != null)
                    TextButton(
                        onClick = {
                            app.action {
                                app.client.connectAsync(peer.host, peer.hello.port)
                                val private = m.request.kind == "PrivateResource"
                                val prefix =
                                    if (private) "/api/v1/chat/resources/" else "/api/v1/resources/"
                                val resource =
                                    app.client.get<Resource>(
                                        peer,
                                        prefix + m.request.resourceId,
                                        private,
                                    )
                                app.downloads.enqueue(peer, resource, private)
                            }
                        }
                    ) {
                        Text("下载资源")
                    }
            }
        }
    }
}

private fun timeLabel(value: String) = runCatching {
    java.time.ZonedDateTime.ofInstant(Instant.parse(value), java.time.ZoneId.systemDefault())
        .format(java.time.format.DateTimeFormatter.ofPattern("HH:mm"))
}
    .getOrDefault("")

private fun transferState(value: String) =
    when (value) {
        "Queued" -> "等待下载"
        "Running" -> "下载中"
        "Paused" -> "已暂停"
        "Completed" -> "已完成"
        "Failed" -> "失败"
        else -> value
    }

internal fun size(bytes: Long) =
    when {
        bytes >= 1024 * 1024 * 1024 -> "%.1f GB".format(bytes.toDouble() / (1024 * 1024 * 1024))
        bytes >= 1024 * 1024 -> "%.1f MB".format(bytes.toDouble() / (1024 * 1024))
        bytes >= 1024 -> "%.1f KB".format(bytes.toDouble() / 1024)
        else -> "$bytes B"
    }

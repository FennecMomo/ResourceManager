package dev.resourcemanager.protocol

import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

val wire = Json { ignoreUnknownKeys = true; encodeDefaults = true }
const val PORT = 37642
const val DISCOVERY_PORT = 37643
val capabilities = listOf("signed-device-v1", "resource-groups-v1", "resource-access-v1", "chat-v1", "chat-private-resource-v1", "reminder-v1")
@Serializable data class Hello(val deviceId: String, val nickname: String, val port: Int = PORT, val avatar: String? = null, val capabilities: List<String>? = dev.resourcemanager.protocol.capabilities)
@Serializable data class SignedHello(val hello: Hello, val nonce: String, val publicKey: String, val signature: String)
@Serializable data class Peer(val hello: Hello, val host: String, val key: String, val lastSeen: Long = 0, val error: String? = null)
@Serializable data class Resource(val id: String, val name: String, val kind: String, val mode: String, val size: Long, val modifiedUtc: String, val available: Boolean, val note: String = "", val groupId: String = "default", val serverStored: Boolean = false)
@Serializable data class RemoteFile(val relativePath: String, val size: Long, val modifiedUtc: String, val isDirectory: Boolean = false)
@Serializable data class Group(val id: String, val name: String, val parentId: String? = null, val sortOrder: Int = 0, val createdUtc: String)
@Serializable data class Catalog(val groups: List<Group>, val resources: List<Resource>)
@Serializable data class ChatRequest(val protocol: String = "chat-v1", val messageId: String, val senderDeviceId: String, val recipientDeviceId: String, val sentUtc: String, val kind: String, val text: String? = null, val resourceId: String? = null)
@Serializable data class ChatReceipt(val accepted: Boolean, val reason: String? = null, val statusCode: Int = 200, val duplicate: Boolean = false, val receivedUtc: String? = null)
@Serializable data class Reminder(val protocol: String = "reminder-v1", val messageId: String, val senderDeviceId: String, val resourceId: String, val resourceName: String, val kind: String, val note: String, val sentUtc: String)
@Serializable data class Discovery(val protocol: String = "ResourceManager.LanDiscovery.v1", val type: String, val deviceId: String, val nickname: String, val port: Int)

package dev.resourcemanager.android

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import dev.resourcemanager.protocol.*
import java.security.KeyStore
import java.security.KeyPairGenerator
import java.security.Signature
import java.security.spec.ECGenParameterSpec
import java.util.Base64
import java.util.UUID

class AndroidIdentity(context: Context): Identity {
    private val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
    private val alias = "resource-manager-device-v1"
    private val prefs = context.getSharedPreferences("identity",Context.MODE_PRIVATE)
    override val deviceId: String
    init {
        if(!store.containsAlias(alias)) {
            KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC,"AndroidKeyStore").apply {
                initialize(KeyGenParameterSpec.Builder(alias,KeyProperties.PURPOSE_SIGN or KeyProperties.PURPOSE_VERIFY).setAlgorithmParameterSpec(ECGenParameterSpec("secp256r1")).setDigests(KeyProperties.DIGEST_SHA256).build())
            }.generateKeyPair()
            check(prefs.edit().putString("id",UUID.randomUUID().toString()).commit())
        }
        deviceId = prefs.getString("id",null) ?: UUID.randomUUID().toString().also { check(prefs.edit().putString("id",it).commit()) }
    }
    override val publicKey: String get() = Base64.getEncoder().encodeToString(store.getCertificate(alias).publicKey.encoded)
    override fun sign(bytes: ByteArray): String = Base64.getEncoder().encodeToString(Proof.derToRaw(Signature.getInstance("SHA256withECDSA").run {
        initSign(store.getKey(alias,null) as java.security.PrivateKey); update(bytes); sign()
    }))
}

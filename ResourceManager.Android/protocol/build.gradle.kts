plugins { kotlin("jvm"); kotlin("plugin.serialization") }
kotlin { jvmToolchain(17) }
dependencies {
    implementation("org.jetbrains.kotlinx:kotlinx-serialization-json:1.9.0")
    implementation("com.squareup.okhttp3:okhttp:4.12.0")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-core:1.10.2")
    testImplementation(kotlin("test"))
}
tasks.test {
    useJUnitPlatform()
    inputs.property("interopPort", providers.environmentVariable("RM_INTEROP_PORT").orElse(""))
}

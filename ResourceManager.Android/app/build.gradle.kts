plugins { id("com.android.application"); kotlin("android"); kotlin("plugin.serialization"); id("org.jetbrains.kotlin.plugin.compose") }
android {
    namespace = "dev.resourcemanager.android"
    compileSdk = 36
    defaultConfig { applicationId = "dev.resourcemanager.android"; minSdk = 29; targetSdk = 36; versionCode = 1; versionName = "0.1.0"; testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner" }
    buildFeatures { compose = true }
    compileOptions { sourceCompatibility = JavaVersion.VERSION_17; targetCompatibility = JavaVersion.VERSION_17 }
    kotlinOptions { jvmTarget = "17" }
    packaging { resources.excludes += setOf("META-INF/INDEX.LIST", "META-INF/io.netty.versions.properties") }
}
dependencies {
    implementation(project(":protocol"))
    implementation(platform("androidx.compose:compose-bom:2025.10.00"))
    implementation("androidx.activity:activity-compose:1.11.0")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.lifecycle:lifecycle-runtime-compose:2.9.4")
    implementation("androidx.core:core-ktx:1.17.0")
    implementation("org.jetbrains.kotlinx:kotlinx-serialization-json:1.9.0")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.10.2")
    implementation("com.squareup.okhttp3:okhttp:4.12.0")
    implementation("io.ktor:ktor-server-cio:3.3.0")
    implementation("io.ktor:ktor-server-core:3.3.0")
    androidTestImplementation("androidx.test:runner:1.7.0")
    androidTestImplementation("androidx.test.ext:junit:1.3.0")
}

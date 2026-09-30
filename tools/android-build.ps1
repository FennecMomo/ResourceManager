param(
    [string]$JavaHome = $env:JAVA_HOME,
    [string]$SdkRoot = $env:ANDROID_HOME,
    [switch]$DeviceTests,
    [string]$DeviceSerial,
    [switch]$Package
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$toolRoot = Join-Path $repoRoot '.tools/android'
if (!$JavaHome) {
    $jdk = Get-ChildItem (Join-Path $toolRoot 'jdk') -Directory -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($jdk) { $JavaHome = $jdk.FullName }
}
if (!$SdkRoot) { $SdkRoot = Join-Path $toolRoot 'sdk' }
if (!$JavaHome -or !(Test-Path (Join-Path $JavaHome 'bin/java.exe'))) { throw 'JDK 17 is required. Pass -JavaHome or set JAVA_HOME.' }
if (!(Test-Path (Join-Path $SdkRoot 'platforms/android-36/android.jar'))) { throw 'Install Android SDK platform 36 and build-tools 35.0.0, then pass -SdkRoot.' }
$env:JAVA_HOME = $JavaHome
$env:ANDROID_HOME = $SdkRoot
$env:ANDROID_USER_HOME = Join-Path $toolRoot 'user'
$env:GRADLE_USER_HOME = Join-Path $toolRoot 'gradle-home'
$projectRoot = Join-Path $repoRoot 'ResourceManager.Android'
$gradle = Join-Path $toolRoot 'gradle/gradle-8.13/bin/gradle.bat'
if (!(Test-Path $gradle)) { $gradle = Join-Path $projectRoot 'gradlew.bat' }
$tasks = @(':protocol:test', ':app:assembleDebug', ':app:lintDebug')
if ($DeviceTests) { $tasks += ':app:assembleDebugAndroidTest' }
& $gradle -p $projectRoot @tasks --console=plain '-Pkotlin.compiler.execution.strategy=in-process'
if ($LASTEXITCODE -ne 0) { throw 'Android build/checks failed.' }
$apk = Join-Path $projectRoot 'app/build/outputs/apk/debug/app-debug.apk'
if ($DeviceTests) {
    if (!$DeviceSerial) { throw 'Pass an explicit -DeviceSerial for an isolated test device.' }
    $adb = Join-Path $SdkRoot 'platform-tools/adb.exe'
    & $adb -s $DeviceSerial install -r $apk
    if ($LASTEXITCODE -ne 0) { throw 'APK install failed.' }
    & $adb -s $DeviceSerial install -r (Join-Path $projectRoot 'app/build/outputs/apk/androidTest/debug/app-debug-androidTest.apk')
    if ($LASTEXITCODE -ne 0) { throw 'Instrumentation install failed.' }
    $sdkLevel = & $adb -s $DeviceSerial shell getprop ro.build.version.sdk
    if ([int]$sdkLevel -ge 33) { & $adb -s $DeviceSerial shell pm grant dev.resourcemanager.android android.permission.POST_NOTIFICATIONS }
    $testOutput = & $adb -s $DeviceSerial shell am instrument -w dev.resourcemanager.android.test/androidx.test.runner.AndroidJUnitRunner
    $testOutput | Write-Output
    if ($LASTEXITCODE -ne 0 -or ($testOutput -join "`n") -notmatch 'OK \(\d+ tests?\)' -or ($testOutput -join "`n") -match 'FAILURES|Process crashed') { throw 'Device tests failed.' }
}
if ($Package) {
    $versionMatch = [regex]::Match((Get-Content (Join-Path $projectRoot 'app/build.gradle.kts') -Raw), 'versionName\s*=\s*"([0-9.]+)"')
    if (!$versionMatch.Success) { throw 'Cannot determine Android version.' }
    $version = $versionMatch.Groups[1].Value
    $outputRoot = Join-Path $repoRoot "dist/android/$version"
    New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
    $outputName = "ResourceManager-Android-$version-test.apk"
    $outputApk = Join-Path $outputRoot $outputName
    Copy-Item -LiteralPath $apk -Destination $outputApk -Force
    $hash = (Get-FileHash -LiteralPath $outputApk -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $outputName" | Set-Content (Join-Path $outputRoot 'SHA256SUMS.txt') -Encoding ascii
    Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination (Join-Path $outputRoot 'README.md') -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot 'validation.md') -Destination (Join-Path $outputRoot 'validation.md') -Force
    Write-Output "Android test package: $outputApk"
}

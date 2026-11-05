plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "io.github.ghermin.averse"
    compileSdk = 35

    defaultConfig {
        applicationId = "io.github.ghermin.averse"
        minSdk = 26
        targetSdk = 35
        versionName = System.getenv("AVERSE_VERSION_NAME") ?: "1.0.6"
        // 1.2.3 → 10203 : toujours croissant d'une version à l'autre (la 1.0.0 publiée avait 1).
        versionCode = System.getenv("AVERSE_VERSION_CODE")?.toInt()
            ?: versionName!!.split('.').map { it.toIntOrNull() ?: 0 }.let { (it + listOf(0, 0, 0)).take(3) }
                .let { (a, b, c) -> a * 10000 + b * 100 + c }
    }

    signingConfigs {
        create("release") {
            val ks = System.getenv("AVERSE_KEYSTORE")
            if (ks != null) {
                storeFile = file(ks)
                storePassword = System.getenv("AVERSE_KEYSTORE_PASSWORD")
                keyAlias = System.getenv("AVERSE_KEY_ALIAS") ?: "averse"
                keyPassword = System.getenv("AVERSE_KEY_PASSWORD") ?: System.getenv("AVERSE_KEYSTORE_PASSWORD")
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            signingConfig = if (System.getenv("AVERSE_KEYSTORE") != null)
                signingConfigs.getByName("release") else signingConfigs.getByName("debug")
        }
    }

    // Une seule archive pour tous les téléphones (arm64, arm 32 bits, x86_64), bibliothèques compressées.
    packaging {
        jniLibs.useLegacyPackaging = true
    }

    // L'interface commune au PC et au téléphone.
    sourceSets["main"].assets.srcDirs("src/main/assets", "../../ui")

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions {
        jvmTarget = "17"
    }
    buildFeatures {
        buildConfig = true
    }
}

dependencies {
    val lt = "2.1.0-39"
    implementation("org.libtorrent4j:libtorrent4j:$lt")
    implementation("org.libtorrent4j:libtorrent4j-android-arm64:$lt")
    implementation("org.libtorrent4j:libtorrent4j-android-arm:$lt")
    implementation("org.libtorrent4j:libtorrent4j-android-x86_64:$lt")
    implementation("androidx.core:core-ktx:1.15.0")
    implementation("androidx.webkit:webkit:1.12.1")
}

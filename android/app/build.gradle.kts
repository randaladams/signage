plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

// Set by the GitHub workflow. Local builds can pass -PserverUrl=https://...
val serverUrl: String = System.getenv("SIGNAGE_SERVER_URL")
    ?: (project.findProperty("serverUrl") as String?)
    ?: ""
val buildNumber: Int = (System.getenv("VERSION_CODE") ?: "1").toInt()
val keystorePath: String? = System.getenv("KEYSTORE_FILE")

android {
    namespace = "com.signage.player"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.signage.player"
        minSdk = 22          // Fire OS 5+; Fire OS 6.x is API 25
        // Kept at 28 on purpose: lets the app start itself after boot on newer Fire OS too.
        // Sideloaded apps aren't subject to store target-SDK rules.
        targetSdk = 28
        versionCode = buildNumber
        versionName = "1.0.$buildNumber"
        buildConfigField("String", "SERVER_URL", "\"${serverUrl.trimEnd('/')}\"")
    }

    signingConfigs {
        if (keystorePath != null) {
            create("release") {
                storeFile = file(keystorePath)
                storePassword = System.getenv("KEYSTORE_PASSWORD")
                keyAlias = System.getenv("KEY_ALIAS") ?: "signage"
                keyPassword = System.getenv("KEYSTORE_PASSWORD")
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            if (keystorePath != null) signingConfig = signingConfigs.getByName("release")
        }
    }

    buildFeatures {
        buildConfig = true
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions {
        jvmTarget = "17"
    }

    lint {
        checkReleaseBuilds = false
        abortOnError = false
    }
}

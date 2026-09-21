plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.android)
}

android {
    namespace = "dev.jarvis.app"
    compileSdk = 35

    defaultConfig {
        applicationId = "dev.jarvis.assistant"
        minSdk = 26
        targetSdk = 35
        versionCode = 1
        versionName = "0.1.0"
        testInstrumentationRunner = "android.app.Instrumentation"
        vectorDrawables.useSupportLibrary = false
    }

    buildTypes {
        debug {
            isMinifyEnabled = false
            applicationIdSuffix = ".debug"
            versionNameSuffix = "-debug"
        }
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro",
            )
            // No signing configuration is committed to this repository. Release builds
            // produce an unsigned APK; supply your own keystore as described in
            // docs/DEVELOPMENT.md.
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    @Suppress("DEPRECATION")
    kotlinOptions {
        jvmTarget = "17"
    }

    // View binding is deliberately DISABLED. Its generated classes implement
    // androidx.viewbinding.ViewBinding, which would drag an AndroidX artifact into an
    // otherwise dependency-free app and - more importantly - would make the UI layer
    // impossible to type-check offline. findViewById is more verbose but keeps the
    // entire app compilable against android.jar alone.
    buildFeatures {
        viewBinding = false
    }

    lint {
        abortOnError = false
        warningsAsErrors = false
        checkReleaseBuilds = false
        xmlReport = true
        htmlReport = true
    }

    testOptions {
        // android.jar methods throw by default; returning default values lets pure-logic
        // unit tests run on the JVM without Robolectric.
        unitTests.isReturnDefaultValues = true
    }
}

dependencies {
    // The ONLY runtime dependency. :core is pure Kotlin with no dependencies of its own.
    implementation(project(":core"))

    testImplementation(libs.junit)
}

# Add project specific ProGuard rules here.

# :core is plain Kotlin with no reflection-based magic, but the tool registry does
# look tools up by name at runtime, so keep tool implementations.
-keep class dev.jarvis.app.tools.** { *; }
-keep class dev.jarvis.app.accessibility.** { *; }

# Keep the AccessibilityService and NotificationListenerService entry points.
-keep class dev.jarvis.app.service.** { *; }

# Kotlin metadata is required for readable stack traces in crash reports.
-keepattributes SourceFile,LineNumberTable
-renamesourcefileattribute SourceFile

# libtorrent4j : classes appelées depuis le code natif (SWIG) et alertes créées dynamiquement.
-keep class org.libtorrent4j.** { *; }
-keepclassmembers class * { @android.webkit.JavascriptInterface <methods>; }

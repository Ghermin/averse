package io.github.ghermin.averse

import android.app.Application

class AverseApp : Application() {
    val engine: TorrentEngine by lazy { TorrentEngine(this).also { it.start() } }
}

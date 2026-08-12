import AVFoundation
import SwiftUI

/// Full-bleed HLS video with no system transport chrome, so the stats overlay owns the
/// screen. AVPlayerLayer rather than VideoPlayer for exactly that reason.
struct PlayerView: UIViewRepresentable {
    let player: AVPlayer

    func makeUIView(context: Context) -> PlayerContainer {
        let view = PlayerContainer()
        view.playerLayer.player = player
        view.playerLayer.videoGravity = .resizeAspect
        view.backgroundColor = .black
        return view
    }

    func updateUIView(_ uiView: PlayerContainer, context: Context) {
        if uiView.playerLayer.player !== player { uiView.playerLayer.player = player }
    }

    final class PlayerContainer: UIView {
        override static var layerClass: AnyClass { AVPlayerLayer.self }
        var playerLayer: AVPlayerLayer { layer as! AVPlayerLayer }
    }
}

/// Owns the AVPlayer and keeps a live stream playing across the hiccups a bot session
/// will actually hit: the broadcast stopping and restarting, the PC sleeping, wifi
/// blipping. A live HLS manifest disappears when the broadcaster stops, so "failed"
/// is a normal state here, not an error to surface loudly.
@MainActor
final class StreamPlayer: ObservableObject {
    @Published private(set) var isPlaying = false

    let player = AVPlayer()
    private var currentURL: URL?
    private var stallObserver: NSObjectProtocol?

    init() {
        player.automaticallyWaitsToMinimizeStalling = true
        player.isMuted = false
        stallObserver = NotificationCenter.default.addObserver(
            forName: .AVPlayerItemFailedToPlayToEndTime,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            Task { @MainActor in self?.restart() }
        }
    }

    deinit {
        if let stallObserver { NotificationCenter.default.removeObserver(stallObserver) }
    }

    func play(url: URL) {
        guard currentURL != url else {
            if player.timeControlStatus != .playing { player.play() }
            return
        }
        currentURL = url
        let item = AVPlayerItem(url: url)
        player.replaceCurrentItem(with: item)
        player.play()
        isPlaying = true
    }

    func stop() {
        player.pause()
        player.replaceCurrentItem(with: nil)
        currentURL = nil
        isPlaying = false
    }

    /// Re-open the same URL — the usual fix when a live edge is lost.
    private func restart() {
        guard let url = currentURL else { return }
        currentURL = nil
        play(url: url)
    }
}

import SwiftUI

/// The living-room screen: the game full-bleed, with the character's live stats laid
/// over it. Everything auto-recovers — nobody wants to hunt for a remote because the
/// bot changed zones or the PC blipped.
///
/// Press play/pause on the Siri Remote to hide the overlay for a clean picture.
struct LiveView: View {
    let token: String
    var onUnpair: () -> Void

    @StateObject private var stream = StreamPlayer()
    @State private var status = LiveStatus()
    @State private var info = StreamInfo()
    @State private var showOverlay = true
    @State private var lastError: String?
    @State private var pollTask: Task<Void, Never>?

    private var client: HubClient { HubClient(token: token) }

    var body: some View {
        ZStack {
            Color.black.ignoresSafeArea()

            if info.playable {
                PlayerView(player: stream.player)
                    .ignoresSafeArea()
            } else {
                idleScreen
            }

            if showOverlay {
                statsOverlay
                    .transition(.opacity)
            }
        }
        .onAppear { startPolling() }
        .onDisappear {
            pollTask?.cancel()
            stream.stop()
        }
        .onPlayPauseCommand { withAnimation(.easeInOut(duration: 0.25)) { showOverlay.toggle() } }
        .onExitCommand { onUnpair() }
    }

    // MARK: - Screens

    /// Shown whenever there is no video to play. It states plainly *why*, because
    /// "live on the phone" and "playable on the TV" are not the same thing: a browser
    /// WebRTC broadcast has no HLS for tvOS.
    private var idleScreen: some View {
        ZStack {
            EqBackground()
            VStack(spacing: 22) {
                Image(systemName: status.online ? "antenna.radiowaves.left.and.right" : "powersleep")
                    .font(.system(size: 78))
                    .foregroundStyle(Eq.dim)

                Text(idleHeadline)
                    .font(.system(size: 44, weight: .semibold))
                    .foregroundStyle(Eq.text)

                Text(idleDetail)
                    .font(.system(size: 26))
                    .foregroundStyle(Eq.dim)
                    .multilineTextAlignment(.center)
                    .frame(maxWidth: 900)

                if let lastError {
                    Text(lastError)
                        .font(.system(size: 21))
                        .foregroundStyle(Eq.bad)
                        .padding(.top, 8)
                }
            }
            .padding(60)
        }
    }

    private var idleHeadline: String {
        if info.live && info.hls == nil { return "Streaming to phone only" }
        return status.online ? "Waiting for the broadcast" : "The gaming PC is offline"
    }

    private var idleDetail: String {
        if info.live && info.hls == nil {
            return "A browser broadcast is running, which Apple TV can't play. "
                 + "Start the stream from EQ Avatar on the PC to watch it here."
        }
        if status.online {
            return "\(status.username ?? "Your character") is \(status.activity.lowercased()). "
                 + "Start broadcasting on the gaming PC and it will appear here automatically."
        }
        return "Once EQ Avatar is running on the gaming PC, this screen wakes up on its own."
    }

    private var statsOverlay: some View {
        VStack {
            HStack(alignment: .top) {
                VStack(alignment: .leading, spacing: 6) {
                    HStack(spacing: 12) {
                        Circle()
                            .fill(status.online ? (status.paused ? Eq.warn : Eq.good) : Eq.dim)
                            .frame(width: 16, height: 16)
                            .shadow(color: (status.online ? Eq.good : .clear).opacity(0.8), radius: 8)

                        Text(status.username ?? "EQ Avatar")
                            .font(.system(size: 40, weight: .bold))
                            .foregroundStyle(Eq.text)

                        if let tier = status.tier {
                            Text(tier)
                                .font(.system(size: 20, weight: .bold))
                                .foregroundStyle(Eq.tier(tier))
                                .padding(.horizontal, 14)
                                .padding(.vertical, 6)
                                .background(
                                    Capsule().fill(Eq.tier(tier).opacity(0.18))
                                        .overlay(Capsule().strokeBorder(Eq.tier(tier).opacity(0.5), lineWidth: 1))
                                )
                        }
                    }

                    Text(overlaySubtitle)
                        .font(.system(size: 24))
                        .foregroundStyle(Eq.dim)
                }

                Spacer()

                Text(status.activity.uppercased())
                    .font(.system(size: 34, weight: .heavy))
                    .foregroundStyle(status.online ? (status.paused ? Eq.warn : Eq.good) : Eq.dim)
            }
            .padding(28)
            .background(scrim)
            .padding(.horizontal, 60)
            .padding(.top, 40)

            Spacer()

            HStack(spacing: 54) {
                stat("KILLS", "\(status.kills)")
                stat("XP TICKS", "\(status.xpTicks)")
                stat("ACTIONS", "\(status.actions)")
                if status.level > 0 { stat("LEVEL", "\(status.level)") }
                Spacer()
                if status.recording {
                    HStack(spacing: 10) {
                        Circle().fill(Eq.accent).frame(width: 12, height: 12)
                        Text("recording")
                            .font(.system(size: 22))
                            .foregroundStyle(Eq.dim)
                    }
                }
            }
            .padding(28)
            .background(scrim)
            .padding(.horizontal, 60)
            .padding(.bottom, 46)
        }
    }

    private var overlaySubtitle: String {
        var parts: [String] = []
        if status.level > 0, let cls = status.characterClass, !cls.isEmpty {
            parts.append("level \(status.level) \(cls)")
        }
        parts.append(status.subtitle)
        return parts.joined(separator: "  ·  ")
    }

    private func stat(_ label: String, _ value: String) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(value)
                .font(.system(size: 44, weight: .bold))
                .foregroundStyle(Eq.text)
            Text(label)
                .font(.system(size: 19, weight: .medium))
                .foregroundStyle(Eq.dim)
        }
    }

    private var scrim: some View {
        RoundedRectangle(cornerRadius: 22)
            .fill(.black.opacity(0.55))
            .overlay(RoundedRectangle(cornerRadius: 22).strokeBorder(.white.opacity(0.08), lineWidth: 1))
    }

    // MARK: - Polling

    private func startPolling() {
        pollTask?.cancel()
        pollTask = Task {
            while !Task.isCancelled {
                await refresh()
                try? await Task.sleep(nanoseconds: 5_000_000_000)
            }
        }
    }

    private func refresh() async {
        let c = client
        do {
            async let s = c.status()
            async let i = c.streamInfo()
            let (newStatus, newInfo) = try await (s, i)
            await MainActor.run {
                status = newStatus
                info = newInfo
                lastError = nil
                if newInfo.playable, let url = newInfo.hls {
                    stream.play(url: url)
                } else {
                    stream.stop()
                }
            }
        } catch HubError.unauthorized {
            await MainActor.run { onUnpair() }
        } catch {
            await MainActor.run { lastError = error.localizedDescription }
        }
    }
}

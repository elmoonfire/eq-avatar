import SwiftUI

/// First run on the TV.
///
/// Nobody should type a 50-character token on a Siri Remote, so the TV shows a short
/// code and the member approves it from the phone app where they are already signed in.
/// The TV polls until that happens, then receives a viewer-only token.
struct PairingView: View {
    var onPaired: (String, String) -> Void

    @State private var code: String?
    @State private var pairID: String?
    @State private var error: String?
    @State private var pollTask: Task<Void, Never>?

    var body: some View {
        ZStack {
            EqBackground()

            VStack(spacing: 34) {
                Circle()
                    .fill(
                        RadialGradient(
                            colors: [.white, Eq.accent, Color(red: 0.10, green: 0.36, blue: 0.47)],
                            center: UnitPoint(x: 0.4, y: 0.35),
                            startRadius: 4,
                            endRadius: 70
                        )
                    )
                    .frame(width: 110, height: 110)
                    .shadow(color: Eq.accent.opacity(0.55), radius: 30)

                VStack(spacing: 10) {
                    Text("EQ Avatar")
                        .font(.system(size: 62, weight: .bold))
                        .foregroundStyle(Eq.text)
                    Text("Watch your character live on the big screen")
                        .font(.system(size: 28))
                        .foregroundStyle(Eq.dim)
                }

                if let code {
                    VStack(spacing: 18) {
                        Text(code)
                            .font(.system(size: 92, weight: .heavy, design: .monospaced))
                            .kerning(8)
                            .foregroundStyle(Eq.accent)
                            .padding(.horizontal, 56)
                            .padding(.vertical, 26)
                            .background(
                                RoundedRectangle(cornerRadius: 26)
                                    .fill(Eq.panel)
                                    .overlay(
                                        RoundedRectangle(cornerRadius: 26)
                                            .strokeBorder(Eq.accent.opacity(0.45), lineWidth: 2)
                                    )
                            )

                        Text("Open EQ Avatar on your phone and enter this code")
                            .font(.system(size: 26))
                            .foregroundStyle(Eq.dim)
                        Text("The code expires in 10 minutes")
                            .font(.system(size: 21))
                            .foregroundStyle(Eq.dim.opacity(0.7))
                    }
                } else if let error {
                    VStack(spacing: 16) {
                        Text(error)
                            .font(.system(size: 26))
                            .foregroundStyle(Eq.bad)
                            .multilineTextAlignment(.center)
                        Button("Try again") { start() }
                            .font(.system(size: 26, weight: .semibold))
                    }
                } else {
                    ProgressView()
                        .scaleEffect(1.6)
                        .tint(Eq.accent)
                }
            }
            .padding(60)
        }
        .onAppear { start() }
        .onDisappear { pollTask?.cancel() }
    }

    private func start() {
        error = nil
        code = nil
        pollTask?.cancel()
        pollTask = Task {
            do {
                let started = try await HubClient.startPairing()
                guard !Task.isCancelled else { return }
                await MainActor.run {
                    code = started.code
                    pairID = started.pairID
                }
                await poll(pairID: started.pairID)
            } catch {
                await MainActor.run {
                    self.error = "Couldn't reach the hub.\n\(error.localizedDescription)"
                }
            }
        }
    }

    private func poll(pairID: String) async {
        while !Task.isCancelled {
            try? await Task.sleep(nanoseconds: 3_000_000_000)
            guard !Task.isCancelled else { return }
            do {
                switch try await HubClient.pollPairing(pairID: pairID) {
                case .approved(let token, let username):
                    await MainActor.run { onPaired(token, username) }
                    return
                case .expired:
                    await MainActor.run { start() }   // quietly get a fresh code
                    return
                case .pending:
                    continue
                }
            } catch {
                continue   // transient network trouble: keep waiting rather than giving up
            }
        }
    }
}

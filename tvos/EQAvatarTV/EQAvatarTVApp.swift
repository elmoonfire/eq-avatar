import SwiftUI

@main
struct EQAvatarTVApp: App {
    @State private var token = Credentials.token

    var body: some Scene {
        WindowGroup {
            Group {
                if let token, !token.isEmpty {
                    LiveView(token: token, onUnpair: unpair)
                } else {
                    PairingView(onPaired: paired)
                }
            }
            .preferredColorScheme(.dark)
        }
    }

    private func paired(token: String, username: String) {
        Credentials.token = token
        Credentials.username = username
        self.token = token
    }

    private func unpair() {
        Credentials.clear()
        token = nil
    }
}

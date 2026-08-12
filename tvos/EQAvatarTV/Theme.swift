import SwiftUI

/// The same palette as the phone app, the desktop client and the members portal, so all
/// four surfaces read as one product. Sizes are scaled for the 10-foot view.
enum Eq {
    static let bg = Color(red: 0.043, green: 0.059, blue: 0.086)      // #0B0F16
    static let panel = Color(red: 0.071, green: 0.094, blue: 0.149)   // #121826
    static let accent = Color(red: 0.310, green: 0.765, blue: 0.969)  // #4FC3F7
    static let good = Color(red: 0.486, green: 0.890, blue: 0.545)    // #7CE38B
    static let warn = Color(red: 1.000, green: 0.718, blue: 0.302)    // #FFB74D
    static let bad = Color(red: 0.878, green: 0.400, blue: 0.369)     // #E0665E
    static let text = Color(red: 0.902, green: 0.929, blue: 0.953)    // #E6EDF3
    static let dim = Color(red: 0.604, green: 0.655, blue: 0.706)     // #9AA7B4

    static func tier(_ t: String?) -> Color {
        switch t {
        case "Hyper":     return good
        case "Ludicrous": return warn
        case "Plaid":     return Color(red: 0.910, green: 0.475, blue: 0.976)
        default:          return accent
        }
    }
}

/// A soft dark backdrop used behind pairing and idle states.
struct EqBackground: View {
    var body: some View {
        ZStack {
            Eq.bg
            RadialGradient(
                colors: [Eq.accent.opacity(0.18), .clear],
                center: .topLeading,
                startRadius: 60,
                endRadius: 1200
            )
        }
        .ignoresSafeArea()
    }
}

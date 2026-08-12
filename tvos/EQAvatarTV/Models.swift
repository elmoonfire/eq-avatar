import Foundation

/// Wire models, parsed defensively: the desktop agent and the hub evolve on their own
/// schedule, and a missing key must never take down the living-room screen.
struct LiveStatus {
    var online = false
    var ageSeconds = 0
    var role = "Idle"
    var paused = false
    var zone: String?
    var kills = 0
    var xpTicks = 0
    var actions = 0
    var recording = false
    var username: String?
    var characterClass: String?
    var tier: String?
    var level = 0

    init() {}

    init(json: [String: Any]) {
        let status = json["status"] as? [String: Any] ?? [:]
        let client = json["client"] as? [String: Any] ?? [:]
        let session = status["session"] as? [String: Any] ?? [:]

        online = json["online"] as? Bool ?? false
        ageSeconds = Self.int(json["age"])
        role = status["role"] as? String ?? "Idle"
        paused = status["paused"] as? Bool ?? false
        zone = (status["zone"] as? String).flatMap { $0.isEmpty ? nil : $0 }
        kills = Self.int(session["kills"])
        xpTicks = Self.int(session["xp_ticks"])
        actions = Self.int(session["actions"])
        recording = session["recording"] as? Bool ?? false
        username = client["username"] as? String
        characterClass = client["class"] as? String
        tier = client["tier"] as? String
        level = Self.int(client["level"])
    }

    private static func int(_ v: Any?) -> Int {
        if let i = v as? Int { return i }
        if let d = v as? Double { return Int(d) }
        if let s = v as? String { return Int(s) ?? 0 }
        return 0
    }

    /// What to show as the headline on the TV.
    var activity: String {
        guard online else { return "PC offline" }
        if role == "Idle" { return "Idle" }
        return paused ? "\(role) — paused" : role
    }

    var subtitle: String {
        guard online else { return "Start EQ Avatar on the gaming PC" }
        if let zone, !zone.isEmpty, zone != "Unknown" { return "in \(zone)" }
        return "zone unknown"
    }
}

/// Playback info. The Apple TV plays **HLS** — tvOS has no web view, so the WebRTC
/// player the phone and website use cannot run here.
struct StreamInfo {
    var live = false
    var source: String?
    var hls: URL?

    init() {}

    init(json: [String: Any]) {
        live = json["live"] as? Bool ?? false
        source = json["source"] as? String
        if let s = json["hls"] as? String, let url = URL(string: s) { hls = url }
    }

    /// Only a Cloudflare Stream broadcast is playable on tvOS. A browser/desktop WebRTC
    /// broadcast shows as "live" for the phone, but there is no HLS for the TV to play,
    /// so the TV must say so plainly rather than spin forever on a black screen.
    var playable: Bool { live && hls != nil }
}

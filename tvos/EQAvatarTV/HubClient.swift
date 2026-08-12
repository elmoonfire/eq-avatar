import Foundation

/// Client for the EQ Avatar hub control-plane API.
///
/// The TV is a **viewer**: the token it is issued during pairing cannot send commands,
/// so a screen in the living room can never start or stop the bot. Only `/hub/api/*` is
/// exempt from the site's network lockdown, which is why every call here targets it.
actor HubClient {
    static let base = URL(string: "https://eqavatar.ldtlan.com/hub/api")!

    private let token: String
    private let session: URLSession

    init(token: String) {
        self.token = token
        let cfg = URLSessionConfiguration.ephemeral
        cfg.timeoutIntervalForRequest = 20
        cfg.waitsForConnectivity = true
        self.session = URLSession(configuration: cfg)
    }

    // MARK: - Requests

    private func request(_ path: String, body: [String: Any]? = nil, authed: Bool = true) async throws -> [String: Any] {
        var req = URLRequest(url: HubClient.base.appendingPathComponent(path))
        req.httpMethod = body == nil ? "GET" : "POST"
        if authed { req.setValue(token, forHTTPHeaderField: "X-EQA-Token") }
        if let body {
            req.setValue("application/json", forHTTPHeaderField: "Content-Type")
            req.httpBody = try JSONSerialization.data(withJSONObject: body)
        }
        let (data, response) = try await session.data(for: req)
        guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw HubError.unreadable
        }
        if let http = response as? HTTPURLResponse, http.statusCode == 401 || http.statusCode == 403 {
            throw HubError.unauthorized
        }
        if json["ok"] as? Bool == false {
            throw HubError.message(json["error"] as? String ?? "Request failed")
        }
        return json
    }

    /// Live status + character profile — one call paints the whole overlay.
    func status() async throws -> LiveStatus {
        LiveStatus(json: try await request("status.php"))
    }

    /// Where the video is, and whether anything is broadcasting right now.
    func streamInfo() async throws -> StreamInfo {
        StreamInfo(json: try await request("stream.php"))
    }

    // MARK: - Pairing (no token needed; these run before we have one)

    struct PairingStart {
        let pairID: String
        let code: String
    }

    static func startPairing() async throws -> PairingStart {
        let client = HubClient(token: "")
        let json = try await client.request("pair.php",
                                            body: ["op": "start", "device": "Apple TV"],
                                            authed: false)
        guard let pairID = json["pair_id"] as? String, let code = json["code"] as? String else {
            throw HubError.unreadable
        }
        return PairingStart(pairID: pairID, code: code)
    }

    enum PairingState {
        case pending
        case expired
        case approved(token: String, username: String)
    }

    static func pollPairing(pairID: String) async throws -> PairingState {
        let client = HubClient(token: "")
        let json = try await client.request("pair.php",
                                            body: ["op": "poll", "pair_id": pairID],
                                            authed: false)
        switch json["status"] as? String {
        case "approved":
            guard let token = json["token"] as? String else { return .expired }
            return .approved(token: token, username: json["username"] as? String ?? "")
        case "expired", "consumed":
            return .expired
        default:
            return .pending
        }
    }
}

enum HubError: LocalizedError {
    case unreadable
    case unauthorized
    case message(String)

    var errorDescription: String? {
        switch self {
        case .unreadable:      return "The hub sent something unreadable."
        case .unauthorized:    return "This Apple TV is no longer paired."
        case .message(let m):  return m
        }
    }
}

/// Where the app keeps its pairing, so the TV only pairs once.
enum Credentials {
    private static let tokenKey = "eqa_tv_token"
    private static let userKey = "eqa_tv_user"

    static var token: String? {
        get { UserDefaults.standard.string(forKey: tokenKey) }
        set { UserDefaults.standard.setValue(newValue, forKey: tokenKey) }
    }

    static var username: String? {
        get { UserDefaults.standard.string(forKey: userKey) }
        set { UserDefaults.standard.setValue(newValue, forKey: userKey) }
    }

    static func clear() {
        UserDefaults.standard.removeObject(forKey: tokenKey)
        UserDefaults.standard.removeObject(forKey: userKey)
    }
}

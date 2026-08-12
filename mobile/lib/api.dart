import 'dart:convert';
import 'package:http/http.dart' as http;
import 'package:shared_preferences/shared_preferences.dart';
import 'models.dart';

/// Client for the EQ Avatar hub control-plane API.
///
/// Auth is a per-member bearer token minted by the members portal
/// (/hub/api/mytoken.php). It is sent as `X-EQA-Token`, which the hub accepts
/// alongside `Authorization: Bearer` — the custom header survives proxies that
/// strip Authorization, which is why the server supports both.
///
/// Only `/hub/api/*` is exempt from the site's Cloudflare network lockdown, so
/// every call here works from cell data; portal pages do not.
class HubApi {
  HubApi({this.baseUrl = defaultBase, required this.token, http.Client? client})
      : _http = client ?? http.Client();

  static const defaultBase = 'https://eqavatar.ldtlan.com/hub';
  static const _tokenKey = 'eqa_token';
  static const _baseKey = 'eqa_base';

  final String baseUrl;
  final String token;
  final http.Client _http;

  Map<String, String> get _headers => {
        'X-EQA-Token': token,
        'Accept': 'application/json',
      };

  Uri _u(String path, [Map<String, String>? q]) =>
      Uri.parse('$baseUrl/api/$path').replace(queryParameters: q);

  Future<Map<String, dynamic>> _get(String path, [Map<String, String>? q]) async {
    final r = await _http.get(_u(path, q), headers: _headers).timeout(const Duration(seconds: 20));
    return _decode(r);
  }

  Future<Map<String, dynamic>> _post(String path, Map<String, dynamic> body) async {
    final r = await _http
        .post(_u(path), headers: {..._headers, 'Content-Type': 'application/json'}, body: jsonEncode(body))
        .timeout(const Duration(seconds: 20));
    return _decode(r);
  }

  Map<String, dynamic> _decode(http.Response r) {
    final Map<String, dynamic> j;
    try {
      j = (jsonDecode(r.body) as Map).cast<String, dynamic>();
    } catch (_) {
      throw HubException(r.statusCode == 200
          ? 'The hub sent something unreadable.'
          : 'Hub error ${r.statusCode}.');
    }
    if (r.statusCode == 401 || r.statusCode == 403) {
      throw HubException(j['error']?.toString() ?? 'Your access token was rejected.', authFailed: true);
    }
    if (j['ok'] == false) throw HubException(j['error']?.toString() ?? 'Request failed.');
    return j;
  }

  /// Live status + character profile in one call — this paints the home screen.
  Future<LiveStatus> status() async => LiveStatus.fromJson(await _get('status.php'));

  /// Recent command history, so the UI can show what the bot did with each tap.
  Future<List<CommandRecord>> commands() async {
    final j = await _get('commands.php');
    final list = (j['commands'] as List?) ?? const [];
    return list.map((e) => CommandRecord.fromJson((e as Map).cast<String, dynamic>())).toList();
  }

  /// Queue a command for the desktop app. It polls every ~4 s, so expect the
  /// result to land within a few seconds; commands expire after 10 minutes.
  Future<int> sendCommand(String kind, [Map<String, dynamic> payload = const {}]) async {
    final j = await _post('commands.php', {'kind': kind, 'payload': payload});
    return (j['id'] is int) ? j['id'] as int : 0;
  }

  Future<int> switchRole(String role) => sendCommand('switch_role', {'role': role});
  Future<int> stopAll() => sendCommand('stop');

  /// Confine the Grind role to a rectangle drawn on the zone map (game coords).
  Future<int> setGrindArea({
    required String zone,
    required double x1,
    required double y1,
    required double x2,
    required double y2,
  }) =>
      sendCommand('set_grind_area',
          {'zone': zone, 'shape': 'rect', 'x1': x1, 'y1': y1, 'x2': x2, 'y2': y2});

  Future<int> farmMob({required String mob, String? zone, int? level}) {
    final payload = <String, dynamic>{'mob': mob};
    if (zone != null) payload['zone'] = zone;
    if (level != null) payload['level'] = level;
    return sendCommand('farm_mob', payload);
  }

  /// Approve an Apple TV that is showing a pairing code.
  ///
  /// The TV is issued a viewer-only token by the hub, so a screen in the living room
  /// can watch the session but can never issue commands.
  Future<String> claimTvCode(String code) async {
    final j = await _post('pair.php', {'op': 'claim', 'code': code.trim().toUpperCase()});
    return (j['device'] ?? 'TV').toString();
  }

  Future<List<SessionSummary>> sessions() async {
    final j = await _get('sessions.php');
    final list = (j['sessions'] as List?) ?? const [];
    return list.map((e) => SessionSummary.fromJson((e as Map).cast<String, dynamic>())).toList();
  }

  Future<SessionDetail> session(String sid) async =>
      SessionDetail.fromJson(await _get('sessions.php', {'sid': sid}));

  /// Is a live broadcast running for this account (browser share or the desktop app)?
  Future<bool> streamLive() async {
    try {
      final j = await _get('stream.php');
      return j['live'] == true;
    } catch (_) {
      return false;
    }
  }

  /// The page the in-app player loads. Same origin as the API, so its own fetches
  /// need no CORS; the token is injected by JS after load rather than put in the
  /// URL, to keep it out of server access logs.
  String get watchPageUrl => '$baseUrl/stream/watch.html';

  void dispose() => _http.close();

  // ---- token persistence -------------------------------------------------

  static Future<String?> savedToken() async =>
      (await SharedPreferences.getInstance()).getString(_tokenKey);

  static Future<String> savedBase() async =>
      (await SharedPreferences.getInstance()).getString(_baseKey) ?? defaultBase;

  static Future<void> save(String token, {String base = defaultBase}) async {
    final p = await SharedPreferences.getInstance();
    await p.setString(_tokenKey, token.trim());
    await p.setString(_baseKey, base.trim());
  }

  static Future<void> forget() async {
    final p = await SharedPreferences.getInstance();
    await p.remove(_tokenKey);
  }
}

class HubException implements Exception {
  HubException(this.message, {this.authFailed = false});
  final String message;
  final bool authFailed;
  @override
  String toString() => message;
}

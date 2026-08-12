/// Wire models for the EQ Avatar hub control-plane API (/hub/api/*).
/// Every field is defensively parsed — the desktop agent evolves independently,
/// and a missing key must never crash the phone.
library;

int _int(dynamic v) => v is int ? v : (v is num ? v.toInt() : int.tryParse('$v') ?? 0);
double? _dbl(dynamic v) => v is num ? v.toDouble() : double.tryParse('$v');

/// GET /hub/api/status.php — what the character is doing right now.
class LiveStatus {
  final bool online;
  final int? ageSeconds;
  final String role;
  final bool paused;
  final String? zone;
  final double? locEw, locNs;
  final int? locAgeSeconds;
  final int kills, actions, xpTicks;
  final bool recording;
  final String? version;
  final bool streamLive;

  // profile (from the clients table)
  final String? username, tier, characterClass, race, guild, server;
  final int level;

  const LiveStatus({
    required this.online,
    this.ageSeconds,
    this.role = 'Idle',
    this.paused = false,
    this.zone,
    this.locEw,
    this.locNs,
    this.locAgeSeconds,
    this.kills = 0,
    this.actions = 0,
    this.xpTicks = 0,
    this.recording = false,
    this.version,
    this.streamLive = false,
    this.username,
    this.tier,
    this.characterClass,
    this.race,
    this.guild,
    this.server,
    this.level = 1,
  });

  static const offline = LiveStatus(online: false);

  factory LiveStatus.fromJson(Map<String, dynamic> j) {
    final st = (j['status'] as Map?)?.cast<String, dynamic>() ?? const {};
    final client = (j['client'] as Map?)?.cast<String, dynamic>() ?? const {};
    final session = (st['session'] as Map?)?.cast<String, dynamic>() ?? const {};
    final loc = (st['loc'] as Map?)?.cast<String, dynamic>();
    final stream = (st['stream'] as Map?)?.cast<String, dynamic>() ?? const {};

    return LiveStatus(
      online: j['online'] == true,
      ageSeconds: j['age'] == null ? null : _int(j['age']),
      role: (st['role'] ?? 'Idle').toString(),
      paused: st['paused'] == true,
      zone: st['zone']?.toString(),
      locEw: loc == null ? null : _dbl(loc['ew']),
      locNs: loc == null ? null : _dbl(loc['ns']),
      locAgeSeconds: loc == null ? null : _int(loc['age_s']),
      kills: _int(session['kills']),
      actions: _int(session['actions']),
      xpTicks: _int(session['xp_ticks']),
      recording: session['recording'] == true,
      version: st['version']?.toString(),
      streamLive: stream['live'] == true,
      username: client['username']?.toString(),
      tier: client['tier']?.toString(),
      characterClass: client['class']?.toString(),
      race: client['race']?.toString(),
      guild: client['guild']?.toString(),
      server: client['server']?.toString(),
      level: _int(client['level']),
    );
  }

  /// Human summary of how stale the agent's last heartbeat is.
  String get freshness {
    if (!online) return 'offline';
    final a = ageSeconds ?? 0;
    if (a < 45) return 'live';
    return '${a}s ago';
  }

  String get activity {
    if (!online) return 'PC offline';
    if (role == 'Idle') return 'Idle';
    return paused ? '$role — paused' : role;
  }
}

/// A command queued from this phone, and how it turned out.
class CommandRecord {
  final int id;
  final String kind, status;
  final String? result, issuedBy;
  final int createdAt;

  const CommandRecord({
    required this.id,
    required this.kind,
    required this.status,
    this.result,
    this.issuedBy,
    this.createdAt = 0,
  });

  factory CommandRecord.fromJson(Map<String, dynamic> j) => CommandRecord(
        id: _int(j['id']),
        kind: (j['kind'] ?? '').toString(),
        status: (j['status'] ?? '').toString(),
        result: j['result']?.toString(),
        issuedBy: j['issued_by']?.toString(),
        createdAt: _int(j['created_at']),
      );

  bool get pending => status == 'queued' || status == 'delivered';
  bool get failed => status == 'failed' || status == 'expired';
}

/// One recorded automation session (summary row from sessions.php).
class SessionSummary {
  final String sid, role, zone;
  final int startedAt, endedAt, kills, xpTicks, aa, deaths;
  final int dmgDealt, dmgTaken;

  const SessionSummary({
    required this.sid,
    this.role = '',
    this.zone = '',
    this.startedAt = 0,
    this.endedAt = 0,
    this.kills = 0,
    this.xpTicks = 0,
    this.aa = 0,
    this.deaths = 0,
    this.dmgDealt = 0,
    this.dmgTaken = 0,
  });

  factory SessionSummary.fromJson(Map<String, dynamic> j) => SessionSummary(
        sid: (j['sid'] ?? '').toString(),
        role: (j['role'] ?? '').toString(),
        zone: (j['zone'] ?? '').toString(),
        startedAt: _int(j['started_at']),
        endedAt: _int(j['ended_at']),
        kills: _int(j['kills']),
        xpTicks: _int(j['xp_ticks']),
        aa: _int(j['aa']),
        deaths: _int(j['deaths']),
        dmgDealt: _int(j['dmg_dealt']),
        dmgTaken: _int(j['dmg_taken']),
      );

  DateTime get started => DateTime.fromMillisecondsSinceEpoch(startedAt * 1000);
  Duration get duration => Duration(seconds: (endedAt - startedAt).clamp(0, 86400 * 7));

  String get durationText {
    final d = duration;
    if (d.inHours >= 1) return '${d.inHours}h ${(d.inMinutes % 60).toString().padLeft(2, '0')}m';
    return '${d.inMinutes}m ${(d.inSeconds % 60).toString().padLeft(2, '0')}s';
  }

  double get hours => duration.inSeconds / 3600.0;

  /// XP is counted in ticks — the EQ log prints no amount — so this is ticks/hour.
  double get xpPerHour => hours >= 0.03 ? xpTicks / hours : 0;
  double get killsPerHour => hours >= 0.03 ? kills / hours : 0;
  double get dps => duration.inSeconds >= 5 ? dmgDealt / duration.inSeconds : 0;
}

/// Full detail for one session, including the per-minute damage timeline.
class SessionDetail {
  final SessionSummary summary;
  final List<double> dealtPerMinute, takenPerMinute;
  final Map<String, String> settings;

  const SessionDetail({
    required this.summary,
    this.dealtPerMinute = const [],
    this.takenPerMinute = const [],
    this.settings = const {},
  });

  factory SessionDetail.fromJson(Map<String, dynamic> j) {
    final s = (j['session'] as Map?)?.cast<String, dynamic>() ?? const {};
    final detail = (s['json'] as Map?)?.cast<String, dynamic>() ?? const {};

    List<double> series(String key) {
      final raw = detail[key];
      if (raw is List) return raw.map((e) => (_dbl(e) ?? 0)).toList();
      return const [];
    }

    final settingsRaw = (detail['Settings'] as Map?)?.cast<String, dynamic>() ?? const {};
    return SessionDetail(
      summary: SessionSummary.fromJson(s),
      dealtPerMinute: series('DealtPerMinute'),
      takenPerMinute: series('TakenPerMinute'),
      settings: settingsRaw.map((k, v) => MapEntry(k, '$v')),
    );
  }

  bool get hasCombat =>
      dealtPerMinute.any((v) => v > 0) || takenPerMinute.any((v) => v > 0);
}

import 'dart:convert';

import 'package:eq_avatar/api.dart';
import 'package:eq_avatar/models.dart';
import 'package:eq_avatar/screens/pair_screen.dart';
import 'package:eq_avatar/theme.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:shared_preferences/shared_preferences.dart';

void main() {
  group('LiveStatus parsing', () {
    test('reads a full agent payload', () {
      final j = jsonDecode('''
      {"ok":true,"online":true,"age":12,
       "status":{"role":"Grind","paused":false,"zone":"Rivervale",
                 "loc":{"ew":-120.4,"ns":310.2,"age_s":3},
                 "session":{"kills":7,"actions":140,"xp_ticks":2,"recording":true},
                 "stream":{"live":true},"version":"0.9.12"},
       "client":{"username":"Bryari","tier":"Plaid","level":50,"class":"Warrior / Druid / Bard"}}
      ''') as Map<String, dynamic>;

      final s = LiveStatus.fromJson(j);
      expect(s.online, isTrue);
      expect(s.role, 'Grind');
      expect(s.zone, 'Rivervale');
      expect(s.locEw, closeTo(-120.4, 0.001));
      expect(s.kills, 7);
      expect(s.recording, isTrue);
      expect(s.streamLive, isTrue);
      expect(s.username, 'Bryari');
      expect(s.level, 50);
      expect(s.freshness, 'live');
      expect(s.activity, 'Grind');
    });

    test('survives an empty/degenerate payload', () {
      final s = LiveStatus.fromJson(<String, dynamic>{'ok': true, 'online': false});
      expect(s.online, isFalse);
      expect(s.role, 'Idle');
      expect(s.kills, 0);
      expect(s.locEw, isNull);
      expect(s.activity, 'PC offline');
    });

    test('paused agent is reported as paused, not stopped', () {
      final s = LiveStatus.fromJson(<String, dynamic>{
        'online': true,
        'age': 5,
        'status': {'role': 'Hunt', 'paused': true},
      });
      expect(s.activity, 'Hunt — paused');
    });
  });

  group('SessionSummary', () {
    test('computes duration and rates', () {
      final s = SessionSummary.fromJson(<String, dynamic>{
        'sid': '20260811_a',
        'role': 'Grind',
        'zone': 'Rivervale',
        'started_at': 1754900000,
        'ended_at': 1754903600, // exactly one hour
        'kills': 47,
        'xp_ticks': 12,
        'dmg_dealt': 180000,
      });
      expect(s.duration.inMinutes, 60);
      expect(s.durationText, '1h 00m');
      expect(s.killsPerHour, closeTo(47, 0.01));
      expect(s.xpPerHour, closeTo(12, 0.01));
      expect(s.dps, closeTo(50, 0.01));
    });

    test('a zero-length session does not divide by zero', () {
      const s = SessionSummary(sid: 'x');
      expect(s.dps, 0);
      expect(s.killsPerHour, 0);
      expect(s.durationText, '0m 00s');
    });
  });

  group('SessionDetail', () {
    test('reads the per-minute series out of the desktop record', () {
      final j = jsonDecode('''
      {"ok":true,"session":{"sid":"s1","role":"Grind","started_at":0,"ended_at":600,
        "json":{"DealtPerMinute":[10,20,30],"TakenPerMinute":[1,2,3],
                "Settings":{"mode":"Rotation only","rest s":"8"}}}}
      ''') as Map<String, dynamic>;

      final d = SessionDetail.fromJson(j);
      expect(d.dealtPerMinute, [10, 20, 30]);
      expect(d.takenPerMinute, [1, 2, 3]);
      expect(d.settings['mode'], 'Rotation only');
      expect(d.hasCombat, isTrue);
    });

    test('a session with no combat reports none', () {
      final d = SessionDetail.fromJson(<String, dynamic>{
        'session': {
          'sid': 's2',
          'json': {'DealtPerMinute': <num>[], 'TakenPerMinute': <num>[]}
        }
      });
      expect(d.hasCombat, isFalse);
    });
  });

  group('HubApi', () {
    test('sends the token header and unwraps the payload', () async {
      late http.Request seen;
      final api = HubApi(
        token: 'eqa_test',
        client: MockClient((req) async {
          seen = req;
          return http.Response('{"ok":true,"online":true,"status":{"role":"Grind"}}', 200);
        }),
      );

      final s = await api.status();
      expect(seen.headers['X-EQA-Token'], 'eqa_test');
      expect(seen.url.path, endsWith('/hub/api/status.php'));
      expect(s.role, 'Grind');
    });

    test('a rejected token raises an auth failure the UI can act on', () async {
      final api = HubApi(
        token: 'bad',
        client: MockClient((_) async => http.Response('{"ok":false,"error":"invalid token"}', 401)),
      );
      expect(
        () => api.status(),
        throwsA(isA<HubException>().having((e) => e.authFailed, 'authFailed', isTrue)),
      );
    });

    test('queues a command with the right wire shape', () async {
      late Map<String, dynamic> body;
      final api = HubApi(
        token: 't',
        client: MockClient((req) async {
          body = (jsonDecode(req.body) as Map).cast<String, dynamic>();
          return http.Response('{"ok":true,"id":42}', 200);
        }),
      );

      final id = await api.switchRole('Grind');
      expect(id, 42);
      expect(body['kind'], 'switch_role');
      expect((body['payload'] as Map)['role'], 'Grind');
    });

    test('grind area is sent in game coordinates', () async {
      late Map<String, dynamic> body;
      final api = HubApi(
        token: 't',
        client: MockClient((req) async {
          body = (jsonDecode(req.body) as Map).cast<String, dynamic>();
          return http.Response('{"ok":true,"id":1}', 200);
        }),
      );

      await api.setGrindArea(zone: 'Rivervale', x1: -350, y1: 120, x2: -90, y2: 420);
      expect(body['kind'], 'set_grind_area');
      final p = (body['payload'] as Map).cast<String, dynamic>();
      expect(p['shape'], 'rect');
      expect(p['zone'], 'Rivervale');
      expect(p['x1'], -350);
      expect(p['y2'], 420);
    });

    test('stream availability degrades to false instead of throwing', () async {
      final api = HubApi(
        token: 't',
        client: MockClient((_) async => http.Response('nope', 500)),
      );
      expect(await api.streamLive(), isFalse);
    });
  });

  group('PairScreen', () {
    setUp(() => SharedPreferences.setMockInitialValues({}));

    testWidgets('refuses an empty token without calling the hub', (tester) async {
      await tester.pumpWidget(MaterialApp(
        theme: Eq.build(),
        home: PairScreen(onPaired: (_) => fail('should not pair on empty input')),
      ));

      await tester.tap(find.widgetWithText(FilledButton, 'Connect'));
      await tester.pump();

      expect(find.textContaining('Paste the access token'), findsOneWidget);
    });

    testWidgets('shows the app name and a token field on first run', (tester) async {
      await tester.pumpWidget(MaterialApp(
        theme: Eq.build(),
        home: PairScreen(onPaired: (_) {}),
      ));

      expect(find.text('EQ Avatar'), findsOneWidget);
      expect(find.byType(TextField), findsOneWidget);
    });
  });
}

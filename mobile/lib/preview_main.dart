/// Design harness — NOT the app entry point (that is main.dart).
///
/// Renders the real screens against a fake hub so the UI can be reviewed in a
/// browser without a running gaming PC:
///   flutter build web -t lib/preview_main.dart
/// Kept in the repo because "render it and look at it" is part of building a
/// screen, and this is the only way to do that without a device.
library;

import 'package:flutter/material.dart';

import 'api.dart';
import 'models.dart';
import 'screens/home_screen.dart';
import 'screens/sessions_screen.dart';
import 'theme.dart';

class FakeApi extends HubApi {
  FakeApi() : super(token: 'preview');

  @override
  Future<LiveStatus> status() async => LiveStatus.fromJson({
        'online': true,
        'age': 8,
        'status': {
          'role': 'Grind',
          'paused': false,
          'zone': 'Rivervale',
          'loc': {'ew': -120.4, 'ns': 310.2, 'age_s': 4},
          'session': {'kills': 47, 'actions': 1382, 'xp_ticks': 12, 'recording': true},
          'stream': {'live': true},
          'version': '0.9.34',
        },
        'client': {
          'username': 'Bryari',
          'tier': 'Plaid',
          'level': 50,
          'class': 'Warrior / Druid / Bard',
          'race': 'Halfling',
          'guild': 'Legends of the Dark Tower',
        },
      });

  @override
  Future<bool> streamLive() async => true;

  @override
  Future<List<CommandRecord>> commands() async => [
        CommandRecord.fromJson({
          'id': 3,
          'kind': 'switch_role',
          'status': 'done',
          'result': 'started Grind remotely — remember the game must stay the focused window',
          'created_at': 0,
        }),
        CommandRecord.fromJson({
          'id': 2,
          'kind': 'farm_mob',
          'status': 'failed',
          'result': 'farm-a-mob arrives in the next app update — command received safely',
          'created_at': 0,
        }),
        CommandRecord.fromJson({'id': 1, 'kind': 'stop', 'status': 'done', 'result': 'all roles stopped'}),
      ];

  @override
  Future<List<SessionSummary>> sessions() async => [
        SessionSummary.fromJson({
          'sid': 'a',
          'role': 'Grind',
          'zone': 'Rivervale',
          'started_at': 1786400000,
          'ended_at': 1786403600,
          'kills': 47,
          'xp_ticks': 12,
          'aa': 3,
          'dmg_dealt': 184200,
          'dmg_taken': 52100,
        }),
        SessionSummary.fromJson({
          'sid': 'b',
          'role': 'Follower',
          'zone': 'Misty Thicket',
          'started_at': 1786300000,
          'ended_at': 1786307200,
          'kills': 96,
          'xp_ticks': 21,
          'aa': 5,
          'dmg_dealt': 402000,
          'dmg_taken': 140500,
        }),
        SessionSummary.fromJson({
          'sid': 'c',
          'role': 'Hunt',
          'zone': 'Kithicor Forest',
          'started_at': 1786200000,
          'ended_at': 1786201500,
          'kills': 8,
          'xp_ticks': 2,
        }),
      ];

  @override
  Future<SessionDetail> session(String sid) async => SessionDetail.fromJson({
        'session': {
          'sid': 'a',
          'role': 'Grind',
          'zone': 'Rivervale',
          'started_at': 1786400000,
          'ended_at': 1786403600,
          'kills': 47,
          'xp_ticks': 12,
          'aa': 3,
          'deaths': 0,
          'dmg_dealt': 184200,
          'dmg_taken': 52100,
          'json': {
            'DealtPerMinute': [1200, 2600, 3100, 2800, 4200, 3900, 5100, 4600, 3200, 4800, 5600, 4100],
            'TakenPerMinute': [400, 900, 1200, 700, 1600, 1100, 2100, 1400, 600, 1800, 2400, 1300],
            'Settings': {
              'mode': 'Rotation only',
              'rotation': '1,1400 | 2,900',
              'rest s': '8',
              'variance %': '15',
            },
          },
        },
      });
}

void main() => runApp(const PreviewApp());

class PreviewApp extends StatelessWidget {
  const PreviewApp({super.key});

  @override
  Widget build(BuildContext context) {
    final api = FakeApi();
    return MaterialApp(
      debugShowCheckedModeBanner: false,
      theme: Eq.build(),
      home: ColoredBox(
        color: const Color(0xFF05080D),
        child: SingleChildScrollView(
          scrollDirection: Axis.horizontal,
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              _Frame(label: 'Live', child: HomeScreen(api: api, onSignOut: () async {})),
              _Frame(label: 'Sessions', child: SessionsScreen(api: api)),
              _Frame(
                label: 'Session detail',
                child: SessionDetailScreen(
                  api: api,
                  summary: SessionSummary.fromJson({
                    'sid': 'a',
                    'role': 'Grind',
                    'zone': 'Rivervale',
                    'started_at': 1786400000,
                    'ended_at': 1786403600,
                    'kills': 47,
                    'xp_ticks': 12,
                    'aa': 3,
                    'dmg_dealt': 184200,
                    'dmg_taken': 52100,
                  }),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _Frame extends StatelessWidget {
  const _Frame({required this.label, required this.child});
  final String label;
  final Widget child;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.all(18),
      child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
        Padding(
          padding: const EdgeInsets.only(bottom: 8, left: 4),
          child: Text(label,
              style: const TextStyle(color: Eq.dim, fontSize: 12, letterSpacing: 1.2)),
        ),
        Container(
          width: 390,
          height: 820,
          decoration: BoxDecoration(
            borderRadius: BorderRadius.circular(22),
            border: Border.all(color: const Color(0xFF223047), width: 2),
          ),
          clipBehavior: Clip.antiAlias,
          child: MediaQuery(
            data: const MediaQueryData(size: Size(390, 820), devicePixelRatio: 2),
            child: child,
          ),
        ),
      ]),
    );
  }
}

import 'dart:async';
import 'package:flutter/material.dart';
import '../api.dart';
import '../models.dart';
import '../theme.dart';
import 'stream_screen.dart';

/// The live screen: what the character is doing right now, plus the controls
/// that steer it. Polls status every 5 s while visible.
class HomeScreen extends StatefulWidget {
  const HomeScreen({super.key, required this.api, required this.onSignOut});

  final HubApi api;
  final Future<void> Function() onSignOut;

  @override
  State<HomeScreen> createState() => _HomeScreenState();
}

class _HomeScreenState extends State<HomeScreen> {
  LiveStatus _status = LiveStatus.offline;
  List<CommandRecord> _commands = const [];
  Timer? _timer;
  bool _first = true;
  bool _sending = false;
  bool _streamLive = false;
  String? _error;

  @override
  void initState() {
    super.initState();
    _refresh();
    _timer = Timer.periodic(const Duration(seconds: 5), (_) => _refresh());
  }

  @override
  void dispose() {
    _timer?.cancel();
    super.dispose();
  }

  Future<void> _refresh() async {
    try {
      final results = await Future.wait([
        widget.api.status(),
        widget.api.commands(),
        widget.api.streamLive(),
      ]);
      if (!mounted) return;
      setState(() {
        _status = results[0] as LiveStatus;
        _commands = results[1] as List<CommandRecord>;
        _streamLive = results[2] as bool;
        _first = false;
        _error = null;
      });
    } on HubException catch (e) {
      if (!mounted) return;
      setState(() {
        _first = false;
        _error = e.message;
      });
    } catch (_) {
      if (!mounted) return;
      setState(() {
        _first = false;
        _error = 'No connection to the hub.';
      });
    }
  }

  Future<void> _send(String label, Future<int> Function() action) async {
    setState(() => _sending = true);
    try {
      await action();
      if (!mounted) return;
      ScaffoldMessenger.of(context).showSnackBar(SnackBar(
        backgroundColor: Eq.panel,
        duration: const Duration(seconds: 3),
        content: Text('$label sent — the PC picks it up within a few seconds.',
            style: const TextStyle(color: Eq.text)),
      ));
      await _refresh();
    } catch (e) {
      if (!mounted) return;
      ScaffoldMessenger.of(context).showSnackBar(SnackBar(
        backgroundColor: Eq.panel,
        content: Text('$e', style: const TextStyle(color: Eq.bad)),
      ));
    } finally {
      if (mounted) setState(() => _sending = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    if (_first) {
      return const Scaffold(body: Center(child: CircularProgressIndicator(color: Eq.accent)));
    }
    final s = _status;
    return Scaffold(
      appBar: AppBar(
        titleSpacing: 16,
        title: Row(children: [
          _Dot(live: s.online, paused: s.paused),
          const SizedBox(width: 9),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              mainAxisSize: MainAxisSize.min,
              children: [
                Text(s.username ?? 'EQ Avatar',
                    style: const TextStyle(fontSize: 16, fontWeight: FontWeight.w600)),
                Text(
                  s.online
                      ? 'level ${s.level} ${s.characterClass ?? ''} · ${s.freshness}'
                      : 'PC offline',
                  style: const TextStyle(fontSize: 11.5, color: Eq.dim),
                ),
              ],
            ),
          ),
        ]),
        actions: [
          if (s.tier != null)
            Container(
              margin: const EdgeInsets.only(right: 8),
              padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 5),
              decoration: BoxDecoration(
                  color: Eq.tier(s.tier).withValues(alpha: 0.18),
                  borderRadius: BorderRadius.circular(20),
                  border: Border.all(color: Eq.tier(s.tier).withValues(alpha: 0.5))),
              child: Text(s.tier!,
                  style: TextStyle(color: Eq.tier(s.tier), fontSize: 11, fontWeight: FontWeight.w700)),
            ),
          PopupMenuButton<String>(
            color: Eq.panel,
            icon: const Icon(Icons.more_vert, color: Eq.dim),
            onSelected: (v) {
              if (v == 'signout') widget.onSignOut();
            },
            itemBuilder: (_) => const [
              PopupMenuItem(value: 'signout', child: Text('Unpair this device')),
            ],
          ),
        ],
      ),
      body: RefreshIndicator(
        color: Eq.accent,
        backgroundColor: Eq.panel,
        onRefresh: _refresh,
        child: ListView(
          padding: const EdgeInsets.fromLTRB(16, 8, 16, 28),
          children: [
            if (_error != null) ...[_ErrorBar(message: _error!), const SizedBox(height: 14)],
            _StatusCard(status: s),
            const SizedBox(height: 14),
            _StreamCard(
              live: _streamLive,
              onWatch: () => Navigator.of(context).push(MaterialPageRoute(
                builder: (_) => StreamScreen(api: widget.api),
              )),
            ),
            const SizedBox(height: 14),
            _ControlsCard(
              busy: _sending || !s.online,
              offline: !s.online,
              onRole: (r) => _send(r, () => widget.api.switchRole(r)),
              onStop: () => _send('Stop', widget.api.stopAll),
            ),
            if (_commands.isNotEmpty) ...[
              const SizedBox(height: 14),
              _CommandFeed(commands: _commands.take(6).toList()),
            ],
          ],
        ),
      ),
    );
  }
}

class _Dot extends StatelessWidget {
  const _Dot({required this.live, required this.paused});
  final bool live, paused;

  @override
  Widget build(BuildContext context) {
    final c = !live ? const Color(0xFF5D6878) : (paused ? Eq.warn : Eq.good);
    return Container(
      width: 11,
      height: 11,
      decoration: BoxDecoration(
        color: c,
        shape: BoxShape.circle,
        boxShadow: live ? [BoxShadow(color: c.withValues(alpha: 0.55), blurRadius: 9, spreadRadius: 1)] : null,
      ),
    );
  }
}

class _StatusCard extends StatelessWidget {
  const _StatusCard({required this.status});
  final LiveStatus status;

  @override
  Widget build(BuildContext context) {
    final s = status;
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(18),
        child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Text(s.activity.toUpperCase(),
              style: TextStyle(
                fontSize: 24,
                fontWeight: FontWeight.w700,
                letterSpacing: 0.5,
                color: !s.online ? Eq.dim : (s.paused ? Eq.warn : Eq.good),
              )),
          const SizedBox(height: 4),
          Text(
            s.online
                ? (s.zone?.isNotEmpty == true ? 'in ${s.zone}' : 'zone unknown')
                : 'Start EQ Avatar on your gaming PC to see it here.',
            style: const TextStyle(color: Eq.dim, fontSize: 13.5),
          ),
          if (s.online && s.locEw != null) ...[
            const SizedBox(height: 3),
            Text('position  ${s.locEw!.toStringAsFixed(0)}, ${s.locNs!.toStringAsFixed(0)}'
                '${(s.locAgeSeconds ?? 0) > 30 ? '  (${s.locAgeSeconds}s ago)' : ''}',
                style: const TextStyle(color: Eq.dim, fontSize: 12)),
          ],
          const SizedBox(height: 16),
          Row(children: [
            _Stat(label: 'kills', value: '${s.kills}'),
            _Stat(label: 'xp ticks', value: '${s.xpTicks}'),
            _Stat(label: 'actions', value: '${s.actions}'),
          ]),
          if (s.recording) ...[
            const SizedBox(height: 12),
            Row(children: [
              Container(
                  width: 7,
                  height: 7,
                  decoration: const BoxDecoration(color: Eq.accent, shape: BoxShape.circle)),
              const SizedBox(width: 7),
              const Text('recording this session',
                  style: TextStyle(color: Eq.dim, fontSize: 12)),
            ]),
          ],
        ]),
      ),
    );
  }
}

class _Stat extends StatelessWidget {
  const _Stat({required this.label, required this.value});
  final String label, value;

  @override
  Widget build(BuildContext context) => Expanded(
        child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Text(value, style: const TextStyle(fontSize: 21, fontWeight: FontWeight.w700)),
          const SizedBox(height: 1),
          Text(label, style: const TextStyle(color: Eq.dim, fontSize: 11.5)),
        ]),
      );
}

class _StreamCard extends StatelessWidget {
  const _StreamCard({required this.live, required this.onWatch});
  final bool live;
  final VoidCallback onWatch;

  @override
  Widget build(BuildContext context) {
    return Card(
      child: InkWell(
        borderRadius: BorderRadius.circular(14),
        onTap: live ? onWatch : null,
        child: Padding(
          padding: const EdgeInsets.all(18),
          child: Row(children: [
            Container(
              width: 44,
              height: 44,
              decoration: BoxDecoration(
                color: (live ? Eq.good : Eq.dim).withValues(alpha: 0.15),
                borderRadius: BorderRadius.circular(11),
              ),
              child: Icon(live ? Icons.play_arrow_rounded : Icons.videocam_off_outlined,
                  color: live ? Eq.good : Eq.dim, size: 24),
            ),
            const SizedBox(width: 14),
            Expanded(
              child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                Text(live ? 'Live stream is on' : 'No live stream',
                    style: const TextStyle(fontSize: 15, fontWeight: FontWeight.w600)),
                const SizedBox(height: 2),
                Text(
                  live
                      ? 'Tap to watch your session'
                      : 'Start broadcasting from the gaming PC to watch here',
                  style: const TextStyle(color: Eq.dim, fontSize: 12.5),
                ),
              ]),
            ),
            if (live) const Icon(Icons.chevron_right, color: Eq.dim),
          ]),
        ),
      ),
    );
  }
}

class _ControlsCard extends StatelessWidget {
  const _ControlsCard({
    required this.busy,
    required this.offline,
    required this.onRole,
    required this.onStop,
  });

  final bool busy, offline;
  final void Function(String role) onRole;
  final VoidCallback onStop;

  @override
  Widget build(BuildContext context) {
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(18),
        child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          const Text('Controls', style: TextStyle(fontSize: 15, fontWeight: FontWeight.w600)),
          const SizedBox(height: 3),
          Text(
            offline
                ? 'Available when the PC is online.'
                : 'The game must stay the focused window on the PC.',
            style: const TextStyle(color: Eq.dim, fontSize: 12.5),
          ),
          const SizedBox(height: 14),
          Wrap(spacing: 9, runSpacing: 9, children: [
            _RoleChip(label: 'Grind', icon: Icons.gps_fixed, enabled: !busy, onTap: () => onRole('Grind')),
            _RoleChip(label: 'Hunt', icon: Icons.travel_explore, enabled: !busy, onTap: () => onRole('Hunt')),
            _RoleChip(label: 'Follower', icon: Icons.group, enabled: !busy, onTap: () => onRole('Follower')),
            _RoleChip(label: 'Stop', icon: Icons.stop_circle_outlined, enabled: !busy, danger: true, onTap: onStop),
          ]),
        ]),
      ),
    );
  }
}

class _RoleChip extends StatelessWidget {
  const _RoleChip({
    required this.label,
    required this.icon,
    required this.enabled,
    required this.onTap,
    this.danger = false,
  });

  final String label;
  final IconData icon;
  final bool enabled, danger;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final c = danger ? Eq.warn : Eq.accent;
    return Opacity(
      opacity: enabled ? 1 : 0.45,
      child: Material(
        color: c.withValues(alpha: 0.14),
        borderRadius: BorderRadius.circular(11),
        child: InkWell(
          borderRadius: BorderRadius.circular(11),
          onTap: enabled ? onTap : null,
          child: Container(
            padding: const EdgeInsets.symmetric(horizontal: 15, vertical: 12),
            decoration: BoxDecoration(
              borderRadius: BorderRadius.circular(11),
              border: Border.all(color: c.withValues(alpha: 0.45)),
            ),
            child: Row(mainAxisSize: MainAxisSize.min, children: [
              Icon(icon, size: 17, color: c),
              const SizedBox(width: 8),
              Text(label, style: TextStyle(color: c, fontWeight: FontWeight.w700, fontSize: 13.5)),
            ]),
          ),
        ),
      ),
    );
  }
}

class _CommandFeed extends StatelessWidget {
  const _CommandFeed({required this.commands});
  final List<CommandRecord> commands;

  @override
  Widget build(BuildContext context) {
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(18),
        child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          const Text('Recent commands', style: TextStyle(fontSize: 15, fontWeight: FontWeight.w600)),
          const SizedBox(height: 10),
          for (final c in commands) ...[
            Padding(
              padding: const EdgeInsets.symmetric(vertical: 6),
              child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
                Padding(
                  padding: const EdgeInsets.only(top: 2),
                  child: Icon(
                    c.pending
                        ? Icons.schedule
                        : (c.failed ? Icons.error_outline : Icons.check_circle_outline),
                    size: 16,
                    color: c.pending ? Eq.dim : (c.failed ? Eq.bad : Eq.good),
                  ),
                ),
                const SizedBox(width: 10),
                Expanded(
                  child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                    Text(c.kind.replaceAll('_', ' '),
                        style: const TextStyle(fontSize: 13, fontWeight: FontWeight.w600)),
                    if (c.result?.isNotEmpty == true)
                      Padding(
                        padding: const EdgeInsets.only(top: 1),
                        child: Text(c.result!,
                            style: const TextStyle(color: Eq.dim, fontSize: 12, height: 1.35)),
                      ),
                  ]),
                ),
                Text(c.status, style: const TextStyle(color: Eq.dim, fontSize: 11)),
              ]),
            ),
          ],
        ]),
      ),
    );
  }
}

class _ErrorBar extends StatelessWidget {
  const _ErrorBar({required this.message});
  final String message;

  @override
  Widget build(BuildContext context) => Container(
        padding: const EdgeInsets.all(13),
        decoration: BoxDecoration(
          color: Eq.bad.withValues(alpha: 0.12),
          borderRadius: BorderRadius.circular(11),
          border: Border.all(color: Eq.bad.withValues(alpha: 0.4)),
        ),
        child: Row(children: [
          const Icon(Icons.cloud_off_rounded, color: Eq.bad, size: 18),
          const SizedBox(width: 10),
          Expanded(child: Text(message, style: const TextStyle(color: Eq.bad, fontSize: 12.5))),
        ]),
      );
}

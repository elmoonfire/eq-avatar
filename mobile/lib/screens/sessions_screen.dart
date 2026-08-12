import 'package:flutter/material.dart';
import '../api.dart';
import '../models.dart';
import '../theme.dart';
import '../widgets/damage_chart.dart';

/// Session history — every recorded run, newest first, with a detail view that
/// charts the per-minute damage timeline.
class SessionsScreen extends StatefulWidget {
  const SessionsScreen({super.key, required this.api});

  final HubApi api;

  @override
  State<SessionsScreen> createState() => _SessionsScreenState();
}

class _SessionsScreenState extends State<SessionsScreen> {
  List<SessionSummary>? _sessions;
  String? _error;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    try {
      final s = await widget.api.sessions();
      if (!mounted) return;
      setState(() {
        _sessions = s;
        _error = null;
      });
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _sessions = const [];
        _error = '$e';
      });
    }
  }

  @override
  Widget build(BuildContext context) {
    final list = _sessions;
    return Scaffold(
      appBar: AppBar(title: const Text('Sessions', style: TextStyle(fontSize: 18))),
      body: list == null
          ? const Center(child: CircularProgressIndicator(color: Eq.accent))
          : RefreshIndicator(
              color: Eq.accent,
              backgroundColor: Eq.panel,
              onRefresh: _load,
              child: list.isEmpty
                  ? ListView(children: [
                      const SizedBox(height: 90),
                      Icon(Icons.history_toggle_off, size: 46, color: Eq.dim.withValues(alpha: 0.6)),
                      const SizedBox(height: 14),
                      Center(
                        child: Text(
                          _error ?? 'No sessions synced yet.',
                          textAlign: TextAlign.center,
                          style: const TextStyle(color: Eq.dim, fontSize: 13.5),
                        ),
                      ),
                      const SizedBox(height: 6),
                      const Center(
                        child: Padding(
                          padding: EdgeInsets.symmetric(horizontal: 40),
                          child: Text(
                            'Run Grind or Follower on the PC — finished sessions upload automatically.',
                            textAlign: TextAlign.center,
                            style: TextStyle(color: Eq.dim, fontSize: 12, height: 1.5),
                          ),
                        ),
                      ),
                    ])
                  : ListView.separated(
                      padding: const EdgeInsets.fromLTRB(16, 8, 16, 28),
                      itemCount: list.length,
                      separatorBuilder: (_, _) => const SizedBox(height: 10),
                      itemBuilder: (_, i) => _SessionTile(
                        session: list[i],
                        onTap: () => Navigator.of(context).push(MaterialPageRoute(
                          builder: (_) => SessionDetailScreen(api: widget.api, summary: list[i]),
                        )),
                      ),
                    ),
            ),
    );
  }
}

class _SessionTile extends StatelessWidget {
  const _SessionTile({required this.session, required this.onTap});
  final SessionSummary session;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final s = session;
    return Card(
      child: InkWell(
        borderRadius: BorderRadius.circular(14),
        onTap: onTap,
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Row(children: [
              Expanded(
                child: Text('${s.role}${s.zone.isNotEmpty ? ' · ${s.zone}' : ''}',
                    style: const TextStyle(fontSize: 15, fontWeight: FontWeight.w600)),
              ),
              Text(s.durationText, style: const TextStyle(color: Eq.dim, fontSize: 12.5)),
            ]),
            const SizedBox(height: 2),
            Text(_when(s.started), style: const TextStyle(color: Eq.dim, fontSize: 12)),
            const SizedBox(height: 12),
            Row(children: [
              _Metric(label: 'kills', value: '${s.kills}', sub: s.killsPerHour > 0 ? '${s.killsPerHour.toStringAsFixed(0)}/h' : null),
              _Metric(label: 'xp ticks', value: '${s.xpTicks}', sub: s.xpPerHour > 0 ? '${s.xpPerHour.toStringAsFixed(1)}/h' : null),
              _Metric(label: 'AA', value: '${s.aa}'),
              _Metric(label: 'dps', value: s.dps > 0 ? s.dps.toStringAsFixed(0) : '—'),
            ]),
          ]),
        ),
      ),
    );
  }

  static String _when(DateTime d) {
    const months = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
    final h = d.hour.toString().padLeft(2, '0');
    final m = d.minute.toString().padLeft(2, '0');
    return '${months[d.month - 1]} ${d.day}  $h:$m';
  }
}

class _Metric extends StatelessWidget {
  const _Metric({required this.label, required this.value, this.sub});
  final String label, value;
  final String? sub;

  @override
  Widget build(BuildContext context) => Expanded(
        child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Text(value, style: const TextStyle(fontSize: 17, fontWeight: FontWeight.w700)),
          Text(label, style: const TextStyle(color: Eq.dim, fontSize: 11)),
          if (sub != null) Text(sub!, style: TextStyle(color: Eq.dim.withValues(alpha: 0.75), fontSize: 10.5)),
        ]),
      );
}

/// One session in full: headline stats, the damage timeline, and the exact
/// settings the run used (so two sessions can be compared to tune them).
class SessionDetailScreen extends StatefulWidget {
  const SessionDetailScreen({super.key, required this.api, required this.summary});

  final HubApi api;
  final SessionSummary summary;

  @override
  State<SessionDetailScreen> createState() => _SessionDetailScreenState();
}

class _SessionDetailScreenState extends State<SessionDetailScreen> {
  SessionDetail? _detail;
  String? _error;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    try {
      final d = await widget.api.session(widget.summary.sid);
      if (!mounted) return;
      setState(() => _detail = d);
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = '$e');
    }
  }

  @override
  Widget build(BuildContext context) {
    final s = widget.summary;
    final d = _detail;
    return Scaffold(
      appBar: AppBar(title: Text(s.role, style: const TextStyle(fontSize: 18))),
      body: ListView(
        padding: const EdgeInsets.fromLTRB(16, 8, 16, 28),
        children: [
          Card(
            child: Padding(
              padding: const EdgeInsets.all(18),
              child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                Text(s.durationText,
                    style: const TextStyle(fontSize: 26, fontWeight: FontWeight.w700)),
                Text('${s.zone.isNotEmpty ? s.zone : 'unknown zone'} · ${_SessionTile._when(s.started)}',
                    style: const TextStyle(color: Eq.dim, fontSize: 13)),
                const SizedBox(height: 16),
                Row(children: [
                  _Metric(label: 'kills', value: '${s.kills}'),
                  _Metric(label: 'xp ticks', value: '${s.xpTicks}'),
                  _Metric(label: 'AA', value: '${s.aa}'),
                  _Metric(label: 'deaths', value: '${s.deaths}'),
                ]),
                const SizedBox(height: 14),
                Row(children: [
                  _Metric(label: 'dealt', value: _compact(s.dmgDealt)),
                  _Metric(label: 'taken', value: _compact(s.dmgTaken)),
                  _Metric(label: 'dps', value: s.dps > 0 ? s.dps.toStringAsFixed(1) : '—'),
                ]),
              ]),
            ),
          ),
          const SizedBox(height: 14),
          Card(
            child: Padding(
              padding: const EdgeInsets.all(18),
              child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                const Text('Damage over time',
                    style: TextStyle(fontSize: 15, fontWeight: FontWeight.w600)),
                const SizedBox(height: 12),
                if (d == null && _error == null)
                  const SizedBox(height: 150, child: Center(child: CircularProgressIndicator(color: Eq.accent)))
                else if (_error != null)
                  SizedBox(
                    height: 120,
                    child: Center(child: Text(_error!, style: const TextStyle(color: Eq.dim, fontSize: 13))),
                  )
                else
                  DamageChart(dealt: d!.dealtPerMinute, taken: d.takenPerMinute),
              ]),
            ),
          ),
          if (d != null && d.settings.isNotEmpty) ...[
            const SizedBox(height: 14),
            Card(
              child: Padding(
                padding: const EdgeInsets.all(18),
                child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                  const Text('Settings this run used',
                      style: TextStyle(fontSize: 15, fontWeight: FontWeight.w600)),
                  const SizedBox(height: 10),
                  for (final e in d.settings.entries)
                    Padding(
                      padding: const EdgeInsets.symmetric(vertical: 3),
                      child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
                        SizedBox(
                          width: 130,
                          child: Text(e.key, style: const TextStyle(color: Eq.dim, fontSize: 12)),
                        ),
                        Expanded(child: Text(e.value, style: const TextStyle(fontSize: 12))),
                      ]),
                    ),
                ]),
              ),
            ),
          ],
        ],
      ),
    );
  }

  static String _compact(int v) {
    if (v >= 1000000) return '${(v / 1000000).toStringAsFixed(1)}M';
    if (v >= 1000) return '${(v / 1000).toStringAsFixed(1)}k';
    return '$v';
  }
}

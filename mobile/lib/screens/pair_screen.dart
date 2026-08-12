import 'package:flutter/material.dart';
import '../api.dart';
import '../theme.dart';

/// First run: pair this phone with the member's EQ Avatar account.
///
/// The token comes from the members portal (Account → access token, served by
/// /hub/api/mytoken.php). We verify it against the live API before saving, so a
/// typo is caught here rather than surfacing as a broken home screen.
class PairScreen extends StatefulWidget {
  const PairScreen({super.key, required this.onPaired});

  final void Function(HubApi api) onPaired;

  @override
  State<PairScreen> createState() => _PairScreenState();
}

class _PairScreenState extends State<PairScreen> {
  final _token = TextEditingController();
  bool _busy = false;
  String? _error;

  @override
  void dispose() {
    _token.dispose();
    super.dispose();
  }

  Future<void> _connect() async {
    final token = _token.text.trim();
    if (token.isEmpty) {
      setState(() => _error = 'Paste the access token from the members portal.');
      return;
    }
    setState(() {
      _busy = true;
      _error = null;
    });
    final api = HubApi(token: token);
    try {
      final status = await api.status();
      await HubApi.save(token);
      if (!mounted) return;
      ScaffoldMessenger.of(context).showSnackBar(SnackBar(
        backgroundColor: Eq.panel,
        content: Text('Paired with ${status.username ?? 'your character'}.',
            style: const TextStyle(color: Eq.text)),
      ));
      widget.onPaired(api);
    } on HubException catch (e) {
      setState(() {
        _busy = false;
        _error = e.authFailed ? 'That token was rejected. Copy it again from the portal.' : e.message;
      });
    } catch (_) {
      setState(() {
        _busy = false;
        _error = "Couldn't reach the hub. Check your connection and try again.";
      });
    }
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      body: SafeArea(
        child: Center(
          child: SingleChildScrollView(
            padding: const EdgeInsets.all(24),
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: 440),
              child: Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  Center(
                    child: Container(
                      width: 62,
                      height: 62,
                      decoration: BoxDecoration(
                        shape: BoxShape.circle,
                        gradient: const RadialGradient(
                          center: Alignment(-0.2, -0.3),
                          colors: [Color(0xFFEAF8FF), Eq.accent, Color(0xFF1A5C78)],
                          stops: [0.0, 0.55, 1.0],
                        ),
                        boxShadow: [BoxShadow(color: Eq.accent.withValues(alpha: 0.45), blurRadius: 22)],
                      ),
                    ),
                  ),
                  const SizedBox(height: 22),
                  const Text('EQ Avatar',
                      textAlign: TextAlign.center,
                      style: TextStyle(fontSize: 26, fontWeight: FontWeight.w700, letterSpacing: 0.3)),
                  const SizedBox(height: 8),
                  const Text(
                    'Watch your character and steer the bot from anywhere.',
                    textAlign: TextAlign.center,
                    style: TextStyle(color: Eq.dim, fontSize: 14, height: 1.45),
                  ),
                  const SizedBox(height: 28),
                  TextField(
                    controller: _token,
                    autocorrect: false,
                    enableSuggestions: false,
                    style: const TextStyle(fontSize: 13),
                    decoration: const InputDecoration(
                      hintText: 'Paste your access token',
                      prefixIcon: Icon(Icons.key_rounded, color: Eq.dim, size: 20),
                    ),
                    onSubmitted: (_) => _connect(),
                  ),
                  if (_error != null) ...[
                    const SizedBox(height: 12),
                    Row(children: [
                      const Icon(Icons.error_outline_rounded, color: Eq.bad, size: 18),
                      const SizedBox(width: 8),
                      Expanded(
                        child: Text(_error!, style: const TextStyle(color: Eq.bad, fontSize: 13)),
                      ),
                    ]),
                  ],
                  const SizedBox(height: 18),
                  FilledButton(
                    onPressed: _busy ? null : _connect,
                    child: _busy
                        ? const SizedBox(
                            height: 18,
                            width: 18,
                            child: CircularProgressIndicator(strokeWidth: 2, color: Color(0xFF04121B)))
                        : const Text('Connect'),
                  ),
                  const SizedBox(height: 20),
                  const Text(
                    'Find the token on the members portal under your account. '
                    'It stays on this device.',
                    textAlign: TextAlign.center,
                    style: TextStyle(color: Eq.dim, fontSize: 12, height: 1.5),
                  ),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}

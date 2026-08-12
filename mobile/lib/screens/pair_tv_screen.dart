import 'package:flutter/material.dart';
import '../api.dart';
import '../theme.dart';

/// Approve an Apple TV that is showing a pairing code.
///
/// Typing a 50-character token on a Siri Remote is miserable, so the TV shows a short
/// code instead and the member confirms it here, where they are already signed in.
class PairTvScreen extends StatefulWidget {
  const PairTvScreen({super.key, required this.api});

  final HubApi api;

  @override
  State<PairTvScreen> createState() => _PairTvScreenState();
}

class _PairTvScreenState extends State<PairTvScreen> {
  final _code = TextEditingController();
  bool _busy = false;
  String? _error;
  String? _done;

  @override
  void dispose() {
    _code.dispose();
    super.dispose();
  }

  Future<void> _submit() async {
    final code = _code.text.trim();
    if (code.replaceAll('-', '').length < 6) {
      setState(() => _error = 'Enter the six-character code shown on the TV.');
      return;
    }
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      final device = await widget.api.claimTvCode(code);
      if (!mounted) return;
      setState(() {
        _busy = false;
        _done = device;
      });
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _busy = false;
        _error = '$e';
      });
    }
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(title: const Text('Pair a TV', style: TextStyle(fontSize: 18))),
      body: ListView(
        padding: const EdgeInsets.fromLTRB(20, 10, 20, 28),
        children: [
          if (_done != null)
            Card(
              child: Padding(
                padding: const EdgeInsets.all(20),
                child: Column(children: [
                  const Icon(Icons.tv_rounded, color: Eq.good, size: 42),
                  const SizedBox(height: 12),
                  Text('$_done is paired',
                      style: const TextStyle(fontSize: 17, fontWeight: FontWeight.w600)),
                  const SizedBox(height: 6),
                  const Text(
                    'It will start watching within a few seconds. The TV can watch only — '
                    'it cannot start or stop the bot.',
                    textAlign: TextAlign.center,
                    style: TextStyle(color: Eq.dim, fontSize: 13, height: 1.45),
                  ),
                ]),
              ),
            )
          else ...[
            const Text(
              'Open EQ Avatar on your Apple TV. It shows a six-character code — type it here.',
              style: TextStyle(color: Eq.dim, fontSize: 14, height: 1.5),
            ),
            const SizedBox(height: 18),
            TextField(
              controller: _code,
              autofocus: true,
              textCapitalization: TextCapitalization.characters,
              autocorrect: false,
              style: const TextStyle(fontSize: 26, letterSpacing: 6, fontWeight: FontWeight.w700),
              textAlign: TextAlign.center,
              decoration: const InputDecoration(hintText: 'ABC-123'),
              onSubmitted: (_) => _submit(),
            ),
            if (_error != null) ...[
              const SizedBox(height: 12),
              Row(children: [
                const Icon(Icons.error_outline_rounded, color: Eq.bad, size: 18),
                const SizedBox(width: 8),
                Expanded(child: Text(_error!, style: const TextStyle(color: Eq.bad, fontSize: 13))),
              ]),
            ],
            const SizedBox(height: 18),
            FilledButton(
              onPressed: _busy ? null : _submit,
              child: _busy
                  ? const SizedBox(
                      height: 18,
                      width: 18,
                      child: CircularProgressIndicator(strokeWidth: 2, color: Color(0xFF04121B)))
                  : const Text('Pair'),
            ),
          ],
        ],
      ),
    );
  }
}

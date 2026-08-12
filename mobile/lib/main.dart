import 'package:flutter/material.dart';
import 'api.dart';
import 'screens/pair_screen.dart';
import 'screens/shell.dart';
import 'theme.dart';

void main() => runApp(const EqAvatarApp());

class EqAvatarApp extends StatefulWidget {
  const EqAvatarApp({super.key});

  @override
  State<EqAvatarApp> createState() => _EqAvatarAppState();
}

class _EqAvatarAppState extends State<EqAvatarApp> {
  HubApi? _api;
  bool _loading = true;

  @override
  void initState() {
    super.initState();
    _restore();
  }

  Future<void> _restore() async {
    final token = await HubApi.savedToken();
    final base = await HubApi.savedBase();
    if (!mounted) return;
    setState(() {
      _api = (token == null || token.isEmpty) ? null : HubApi(token: token, baseUrl: base);
      _loading = false;
    });
  }

  void _onPaired(HubApi api) => setState(() => _api = api);

  Future<void> _signOut() async {
    await HubApi.forget();
    if (!mounted) return;
    setState(() => _api = null);
  }

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'EQ Avatar',
      debugShowCheckedModeBanner: false,
      theme: Eq.build(),
      home: _loading
          ? const Scaffold(body: Center(child: CircularProgressIndicator(color: Eq.accent)))
          : (_api == null
              ? PairScreen(onPaired: _onPaired)
              : AppShell(api: _api!, onSignOut: _signOut)),
    );
  }
}

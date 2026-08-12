import 'package:flutter/material.dart';
import '../api.dart';
import '../theme.dart';
import 'home_screen.dart';
import 'sessions_screen.dart';

/// Two-tab shell: what's happening now, and what happened before.
class AppShell extends StatefulWidget {
  const AppShell({super.key, required this.api, required this.onSignOut});

  final HubApi api;
  final Future<void> Function() onSignOut;

  @override
  State<AppShell> createState() => _AppShellState();
}

class _AppShellState extends State<AppShell> {
  int _tab = 0;

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      body: IndexedStack(
        index: _tab,
        children: [
          HomeScreen(api: widget.api, onSignOut: widget.onSignOut),
          SessionsScreen(api: widget.api),
        ],
      ),
      bottomNavigationBar: NavigationBar(
        backgroundColor: Eq.panel,
        indicatorColor: Eq.accent.withValues(alpha: 0.18),
        selectedIndex: _tab,
        onDestinationSelected: (i) => setState(() => _tab = i),
        destinations: const [
          NavigationDestination(
            icon: Icon(Icons.sports_esports_outlined, color: Eq.dim),
            selectedIcon: Icon(Icons.sports_esports, color: Eq.accent),
            label: 'Live',
          ),
          NavigationDestination(
            icon: Icon(Icons.insights_outlined, color: Eq.dim),
            selectedIcon: Icon(Icons.insights, color: Eq.accent),
            label: 'Sessions',
          ),
        ],
      ),
    );
  }
}

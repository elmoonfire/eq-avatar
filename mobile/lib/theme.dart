import 'package:flutter/material.dart';

/// EQ Avatar palette — matches the desktop app and the members portal so the
/// three surfaces read as one product.
class Eq {
  static const bg = Color(0xFF0B0F16);
  static const panel = Color(0xFF121826);
  static const panelHi = Color(0xFF18202F);
  static const line = Color(0xFF1E2635);
  static const accent = Color(0xFF4FC3F7);
  static const good = Color(0xFF7CE38B);
  static const warn = Color(0xFFFFB74D);
  static const bad = Color(0xFFE0665E);
  static const text = Color(0xFFE6EDF3);
  static const dim = Color(0xFF9AA7B4);

  /// Chart series colours. Validated for the dark chart surface (#121826) with the
  /// dataviz validator: lightness band, chroma floor, CVD separation (ΔE 17.4
  /// protan / 30.0 tritan), normal-vision separation (ΔE 26.4) and 3:1 contrast
  /// all pass. Do not swap these for the lighter UI accents without re-validating.
  static const seriesDealt = Color(0xFF2E9BD0);
  static const seriesTaken = Color(0xFFE0665E);

  /// Subscription tier accents (mirrors hub config.php).
  static Color tier(String? t) => switch (t) {
        'Hyper' => good,
        'Ludicrous' => warn,
        'Plaid' => const Color(0xFFE879F9),
        _ => accent,
      };

  static ThemeData build() {
    final base = ThemeData.dark(useMaterial3: true);
    return base.copyWith(
      scaffoldBackgroundColor: bg,
      colorScheme: base.colorScheme.copyWith(
        primary: accent,
        secondary: good,
        surface: panel,
        error: bad,
      ),
      appBarTheme: const AppBarTheme(
        backgroundColor: bg,
        foregroundColor: text,
        elevation: 0,
        centerTitle: false,
      ),
      cardTheme: CardThemeData(
        color: panel,
        elevation: 0,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(14),
          side: const BorderSide(color: line),
        ),
        margin: EdgeInsets.zero,
      ),
      inputDecorationTheme: InputDecorationTheme(
        filled: true,
        fillColor: const Color(0xFF0D1420),
        border: OutlineInputBorder(
          borderRadius: BorderRadius.circular(10),
          borderSide: const BorderSide(color: Color(0xFF263145)),
        ),
        enabledBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(10),
          borderSide: const BorderSide(color: Color(0xFF263145)),
        ),
        hintStyle: const TextStyle(color: dim),
      ),
      filledButtonTheme: FilledButtonThemeData(
        style: FilledButton.styleFrom(
          backgroundColor: accent,
          foregroundColor: const Color(0xFF04121B),
          textStyle: const TextStyle(fontWeight: FontWeight.w700),
          shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(10)),
          padding: const EdgeInsets.symmetric(horizontal: 18, vertical: 14),
        ),
      ),
      textTheme: base.textTheme.apply(bodyColor: text, displayColor: text),
      dividerColor: line,
    );
  }
}

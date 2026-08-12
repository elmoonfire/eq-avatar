import 'package:flutter/material.dart';
import '../theme.dart';

/// Per-minute damage timeline for one recorded session: damage dealt vs taken.
///
/// Change-over-time with two series → a line chart with a soft area under the
/// primary series. Both series are direct-labelled at their last point and a
/// legend is present, so identity never rests on colour alone. Series colours
/// come from Eq.seriesDealt/seriesTaken, which are validated against this dark
/// surface (see theme.dart).
class DamageChart extends StatelessWidget {
  const DamageChart({
    super.key,
    required this.dealt,
    required this.taken,
    this.height = 190,
  });

  final List<double> dealt;
  final List<double> taken;
  final double height;

  @override
  Widget build(BuildContext context) {
    final empty = dealt.every((v) => v == 0) && taken.every((v) => v == 0);
    if (empty) {
      return SizedBox(
        height: height,
        child: const Center(
          child: Text('No combat recorded this session',
              style: TextStyle(color: Eq.dim, fontSize: 13)),
        ),
      );
    }
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(children: [
          _key(Eq.seriesDealt, 'dealt/min'),
          const SizedBox(width: 14),
          _key(Eq.seriesTaken, 'taken/min'),
        ]),
        const SizedBox(height: 10),
        SizedBox(
          height: height,
          width: double.infinity,
          child: CustomPaint(painter: _DamagePainter(dealt: dealt, taken: taken)),
        ),
        const SizedBox(height: 6),
        const Row(mainAxisAlignment: MainAxisAlignment.spaceBetween, children: [
          Text('start', style: TextStyle(color: Eq.dim, fontSize: 11)),
          Text('end of session', style: TextStyle(color: Eq.dim, fontSize: 11)),
        ]),
      ],
    );
  }

  Widget _key(Color c, String label) => Row(mainAxisSize: MainAxisSize.min, children: [
        Container(width: 10, height: 10, decoration: BoxDecoration(color: c, borderRadius: BorderRadius.circular(3))),
        const SizedBox(width: 6),
        // Legend text wears an ink token, never the series colour.
        Text(label, style: const TextStyle(color: Eq.dim, fontSize: 12)),
      ]);
}

class _DamagePainter extends CustomPainter {
  _DamagePainter({required this.dealt, required this.taken});

  final List<double> dealt;
  final List<double> taken;

  @override
  void paint(Canvas canvas, Size size) {
    final n = [dealt.length, taken.length].reduce((a, b) => a > b ? a : b);
    if (n == 0) return;

    double peak = 0;
    for (final v in [...dealt, ...taken]) {
      if (v > peak) peak = v;
    }
    if (peak <= 0) peak = 1;
    // Round the ceiling up so the top gridline is a readable number.
    final ceiling = _niceCeiling(peak);

    const leftPad = 44.0;
    const topPad = 6.0;
    const bottomPad = 18.0;
    final plotW = size.width - leftPad - 8;
    final plotH = size.height - topPad - bottomPad;

    // --- recessive grid + value labels ---
    final grid = Paint()
      ..color = Eq.line
      ..strokeWidth = 1;
    for (var i = 0; i <= 2; i++) {
      final y = topPad + plotH * (i / 2);
      canvas.drawLine(Offset(leftPad, y), Offset(size.width - 8, y), grid);
      final value = ceiling * (1 - i / 2);
      _label(canvas, _compact(value), Offset(leftPad - 8, y), align: TextAlign.right);
    }

    Offset pt(int i, double v) => Offset(
          leftPad + (n == 1 ? plotW / 2 : plotW * (i / (n - 1))),
          topPad + plotH * (1 - (v / ceiling).clamp(0, 1)),
        );

    Path linePath(List<double> data) {
      final p = Path();
      for (var i = 0; i < data.length; i++) {
        final o = pt(i, data[i]);
        i == 0 ? p.moveTo(o.dx, o.dy) : p.lineTo(o.dx, o.dy);
      }
      return p;
    }

    // --- area under the primary series (soft, so the line stays the signal) ---
    if (dealt.isNotEmpty) {
      final area = Path.from(linePath(dealt))
        ..lineTo(pt(dealt.length - 1, 0).dx, topPad + plotH)
        ..lineTo(pt(0, 0).dx, topPad + plotH)
        ..close();
      canvas.drawPath(
        area,
        Paint()
          ..shader = LinearGradient(
            begin: Alignment.topCenter,
            end: Alignment.bottomCenter,
            colors: [Eq.seriesDealt.withValues(alpha: 0.28), Eq.seriesDealt.withValues(alpha: 0.02)],
          ).createShader(Rect.fromLTWH(0, topPad, size.width, plotH)),
      );
    }

    // --- 2px lines ---
    void stroke(List<double> data, Color c) {
      if (data.isEmpty) return;
      canvas.drawPath(
        linePath(data),
        Paint()
          ..color = c
          ..strokeWidth = 2
          ..style = PaintingStyle.stroke
          ..strokeJoin = StrokeJoin.round
          ..strokeCap = StrokeCap.round,
      );
    }

    stroke(taken, Eq.seriesTaken);
    stroke(dealt, Eq.seriesDealt);

    // --- direct labels at the last point (with a surface ring so overlaps read) ---
    void endMarker(List<double> data, Color c) {
      if (data.isEmpty) return;
      final o = pt(data.length - 1, data.last);
      canvas.drawCircle(o, 5.5, Paint()..color = Eq.panel);
      canvas.drawCircle(o, 4, Paint()..color = c);
    }

    endMarker(taken, Eq.seriesTaken);
    endMarker(dealt, Eq.seriesDealt);
  }

  void _label(Canvas canvas, String text, Offset at, {TextAlign align = TextAlign.left}) {
    final tp = TextPainter(
      text: TextSpan(text: text, style: const TextStyle(color: Eq.dim, fontSize: 10)),
      textDirection: TextDirection.ltr,
      textAlign: align,
    )..layout();
    tp.paint(canvas, Offset(align == TextAlign.right ? at.dx - tp.width : at.dx, at.dy - tp.height / 2));
  }

  static double _niceCeiling(double v) {
    if (v <= 10) return 10;
    final mag = 1.0 * _pow10((v).floor().toString().length - 1);
    final step = mag / 2;
    return (v / step).ceil() * step;
  }

  static int _pow10(int e) {
    var r = 1;
    for (var i = 0; i < e; i++) {
      r *= 10;
    }
    return r;
  }

  static String _compact(double v) {
    if (v >= 1000000) return '${(v / 1000000).toStringAsFixed(1)}M';
    if (v >= 1000) return '${(v / 1000).toStringAsFixed(v >= 10000 ? 0 : 1)}k';
    return v.toStringAsFixed(0);
  }

  @override
  bool shouldRepaint(covariant _DamagePainter old) =>
      old.dealt != dealt || old.taken != taken;
}

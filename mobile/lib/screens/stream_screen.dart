import 'package:flutter/material.dart';
import 'package:webview_flutter/webview_flutter.dart';
import 'package:webview_flutter_android/webview_flutter_android.dart';
import 'package:webview_flutter_wkwebview/webview_flutter_wkwebview.dart';
import '../api.dart';
import '../theme.dart';

/// Live game view.
///
/// The player is the hub's own watch page driven by eqstream.js — the same
/// WebRTC code the website uses, so there is one implementation of the
/// Cloudflare SFU handshake instead of three. The token is injected by calling
/// the page's start() after load rather than passed in the URL, which keeps the
/// secret out of server access logs.
class StreamScreen extends StatefulWidget {
  const StreamScreen({super.key, required this.api});

  final HubApi api;

  @override
  State<StreamScreen> createState() => _StreamScreenState();
}

class _StreamScreenState extends State<StreamScreen> {
  late final WebViewController _controller;
  bool _loading = true;

  @override
  void initState() {
    super.initState();

    // Autoplay must not require a tap, and video must render inline (not
    // fullscreen-only) on iOS, or the stream appears as a black box.
    late final PlatformWebViewControllerCreationParams params;
    if (WebViewPlatform.instance is WebKitWebViewPlatform) {
      params = WebKitWebViewControllerCreationParams(
        allowsInlineMediaPlayback: true,
        mediaTypesRequiringUserAction: const <PlaybackMediaTypes>{},
      );
    } else {
      params = const PlatformWebViewControllerCreationParams();
    }

    _controller = WebViewController.fromPlatformCreationParams(params)
      ..setJavaScriptMode(JavaScriptMode.unrestricted)
      ..setBackgroundColor(Colors.black)
      ..setNavigationDelegate(NavigationDelegate(
        onPageFinished: (_) async {
          // watch.html exposes start(token); calling it hides the pairing row
          // and connects straight to the account's live session.
          final safe = widget.api.token.replaceAll(r'\', r'\\').replaceAll("'", r"\'");
          await _controller.runJavaScript("try{start('$safe')}catch(e){}");
          if (mounted) setState(() => _loading = false);
        },
      ));

    if (_controller.platform is AndroidWebViewController) {
      AndroidWebViewController.enableDebugging(false);
      (_controller.platform as AndroidWebViewController)
          .setMediaPlaybackRequiresUserGesture(false);
    }

    _controller.loadRequest(Uri.parse(widget.api.watchPageUrl));
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      backgroundColor: Colors.black,
      appBar: AppBar(
        backgroundColor: Colors.black,
        title: const Text('Live session', style: TextStyle(fontSize: 16)),
        actions: [
          IconButton(
            tooltip: 'Reconnect',
            icon: const Icon(Icons.refresh, color: Eq.dim),
            onPressed: () {
              setState(() => _loading = true);
              _controller.reload();
            },
          ),
        ],
      ),
      body: Stack(children: [
        WebViewWidget(controller: _controller),
        if (_loading)
          const ColoredBox(
            color: Colors.black,
            child: Center(child: CircularProgressIndicator(color: Eq.accent)),
          ),
      ]),
    );
  }
}

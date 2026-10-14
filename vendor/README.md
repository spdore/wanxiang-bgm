# Vendored Build Dependencies

- `vb-cable/VBCABLE_Driver_Pack45.zip` is the official signed VB-CABLE driver archive used by the installer build.
- `webview2/sdk/net462/` contains the WebView2 SDK assemblies required to compile the application.
- `webview2/loader/` contains the architecture-specific WebView2 loader libraries.
- `webview2/bootstrapper/` is ignored by Git. The share installer build requires Microsoft's official Evergreen Bootstrapper there; see [`docs/INSTALLATION.md`](../docs/INSTALLATION.md).
- `music-geshizhuanhuan/` contains the pinned, restricted offline NCM/QMC/KGM-VPR decoder subset used by the 1.9.7 local import helper; see its [`UPSTREAM.md`](music-geshizhuanhuan/UPSTREAM.md).

Third-party terms and notices are collected in [`licenses/`](../licenses/).

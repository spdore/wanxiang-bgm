# Installation Notes

## End users

The repository's current source is version 1.9.7. Until a 1.9.7 GitHub installer release is published, the latest downloadable installer is version 1.9.6.

1. Download the [Wanxiang BGM Player 1.9.6 installer](https://github.com/spdore/wanxiang-bgm/releases/download/v1.9.6/WanxiangBgmSetup-1.9.6.exe).
2. Run it, choose an installation folder with **Browse**, then continue through the setup wizard.
3. The installer checks for WebView2 Runtime and VB-CABLE and reuses existing installations. If WebView2 Runtime is missing, setup uses the included official Microsoft Bootstrapper to download it; an internet connection is required. The VB-CABLE driver package is included.
4. If VB-CABLE is missing, approve its official signed driver setup when Windows requests administrator permission. Follow the driver's prompts; a restart may be required.
5. Launch Wanxiang BGM Player and import audio files. A clean installation starts with an empty library; upgrades keep the existing library and settings.
6. In the player, choose the physical microphone and **CABLE Input**. In QQ or the game, choose **CABLE Output** as the microphone/recording device.

The installer targets 64-bit Intel/AMD computers running Windows 10 version 1903 or later, or Windows 11. It requires .NET Framework 4.8 or later. The application installer is not digitally signed.

## Building the share installer

The source tree includes the VB-CABLE signed driver archive and WebView2 SDK assemblies. Download Microsoft's official Evergreen Bootstrapper from [the Microsoft download link](https://go.microsoft.com/fwlink/p/?LinkId=2124703) and save it as:

```text
vendor/webview2/bootstrapper/MicrosoftEdgeWebview2Setup.exe
```

Then run `packaging/build-share.ps1`. It checks the Microsoft Bootstrapper and VB-Audio signatures before embedding the dependencies. Existing runtime and driver installations are detected and reused. If WebView2 Runtime is missing, the Bootstrapper downloads it from Microsoft, so this case requires an internet connection; the resulting installer is not fully offline.

The driver package may request administrator approval and may require a restart. The application does not bypass Windows elevation, driver signature enforcement, or third-party license terms.


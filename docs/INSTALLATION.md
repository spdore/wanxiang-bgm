# Installation Notes

## End users

1. Download the [Wanxiang BGM Player 1.9.5 installer](https://github.com/spdore/wanxiang-bgm/releases/download/v1.9.5/WanxiangBgmSetup-1.9.5.exe).
2. Run it, choose an installation folder with **Browse**, then continue through the setup wizard.
3. The installer checks for WebView2 Runtime and VB-CABLE and reuses existing installations. Both official installation packages are included for offline setup.
4. If VB-CABLE is missing, approve its official signed driver setup when Windows requests administrator permission. Follow the driver's prompts; a restart may be required.
5. Launch Wanxiang BGM Player and import audio files. A clean installation starts with an empty library; upgrades keep the existing library and settings.
6. In the player, choose the physical microphone and **CABLE Input**. In QQ or the game, choose **CABLE Output** as the microphone/recording device.

The installer targets 64-bit Intel/AMD computers running Windows 10 version 1903 or later, or Windows 11. It requires .NET Framework 4.8 or later. The application installer is not digitally signed.

## Building the share installer

The source tree includes the VB-CABLE signed driver archive and WebView2 SDK assemblies. The large WebView2 Evergreen Standalone Runtime installer is excluded from Git and must be obtained from Microsoft's [WebView2 download page](https://developer.microsoft.com/microsoft-edge/webview2/) before building the installer. Save the x64 package as:

```text
vendor/webview2/runtime-installer/MicrosoftEdgeWebView2RuntimeInstallerX64.exe
```

Then run `packaging/build-share.ps1`. It checks the Microsoft and VB-Audio signatures before embedding the dependencies. The resulting installer can install the Evergreen runtime offline. Existing runtime and driver installations are detected and reused.

The driver package may request administrator approval and may require a restart. The application does not bypass Windows elevation, driver signature enforcement, or third-party license terms.


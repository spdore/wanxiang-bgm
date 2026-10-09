<div align="center">
  <img src="brand.svg" width="72" alt="Wanxiang BGM">
  <h1>Wanxiang BGM Player</h1>
  <p>Play your music. Share it with your team.</p>
  <p><a href="https://github.com/spdore/wanxiang-bgm/raw/refs/heads/main/WanxiangBgmSetup.exe">Download Windows Installer</a> · <a href="#quick-start">Quick Start</a> · <a href="#voice-routing">Voice Setup</a></p>
</div>

A Windows MP3 soundboard with global shortcuts, random playback, and separate music volume controls for your headphones and voice chat. **Version 1.5.2. The shared installer starts with no songs or shortcut bindings.** The interface is currently in Chinese.

## Features

- Import your own MP3s, search your library, assign shortcuts, and remove entries without deleting audio files.
- Play one song at a time. Press any bound song key while playing to stop.
- Choose random candidates from all MP3s in the `bgm` folder, including songs without individual shortcuts.
- Mix your selected physical microphone with this app's music through VB-CABLE.
- Adjust headphone music and voice-channel music independently.
- Light theme with an optional dark theme, integrated window controls, and startup focus handling.

## Installation

1. Download and run [WanxiangBgmSetup.exe](https://github.com/spdore/wanxiang-bgm/raw/refs/heads/main/WanxiangBgmSetup.exe).
2. If VB-CABLE is missing, choose the bundled official driver installer. Follow its instructions and restart Windows if prompted. Driver installation requires administrator permission.
3. Launch the player from the desktop or Start menu.

**Requirements:** Windows 10/11, .NET Framework 4.8, and [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/). The installer includes WebView2 SDK files but does not bundle the full runtime. VB-CABLE is required for sharing music through voice chat. The installer is unsigned.

## Quick Start

1. Click **添加歌曲** (Add Songs) and select an MP3.
2. Click its key field to bind a letter, number, or F1–F24. Press Esc to cancel binding.
3. Click **播放** (Play) or press its shortcut. While playing, another bound song key stops playback.
4. Open **随机播放** (Random Playback), refresh the folder list, select candidates, and assign a separate shortcut. The random key stops current playback before a subsequent press picks a song.
5. Close the window to exit the application.

Settings save automatically. Headphone output follows the Windows default playback device. Both sliders display 0–100%, mapped to an actual music gain of 0–30%. Voice-channel music volume does not change microphone volume. These values are digital gain settings, not a calibrated sound-pressure measurement.

## Voice Routing

```text
Selected physical microphone ─┐
                             ├─ CABLE Input → CABLE Output → QQ / game voice chat
App BGM ─────────────────────┘
        └─ Windows default headphones
```

In **语音设置** (Voice Settings), select your physical microphone and **CABLE Input** as the sending device. In QQ or your game, select **CABLE Output** as its microphone. Only the selected microphone and this app's BGM are mixed; other computer audio is not captured.

If teammates cannot hear music, refresh devices, check both endpoint selections, raise the voice-channel music slider, and run **检查声音通道** (Check Audio Channel) while a song plays. Check the chat app's input meter and noise suppression settings. A missing saved microphone must be selected again.

## Data and Privacy

The distribution stores settings and imported songs locally in `%LOCALAPPDATA%\BgmHotkeyShare`, with MP3s under `bgm`. No developer songs, device configurations, recordings, logs, personal screenshots, credentials, or personal workstation paths are included. Uninstalling preserves user songs/settings and does not uninstall the shared VB-CABLE driver.

## Build from Source

Run PowerShell in the repository root on Windows:

```powershell
.\build.ps1
.\build-share.ps1
```

The first command builds the empty-library player and copies its WebView2 dependencies. The second builds `分享安装包\万象BGM_安装.exe`. Keep the EXE, its `.config`, WebView2 assemblies, and loader together when running a source build.

The app uses C# 5 / .NET Framework 4.8, a Windows Forms window, and embedded offline HTML/CSS/SVG in WebView2. Native MP3 playback supplies headphone audio; Media Foundation and WinMM handle the music/microphone mix for the virtual channel. No Node.js server is required at runtime.

VB-CABLE is donationware from [VB-Audio](https://vb-audio.com/Cable/). The bundled standard driver ZIP is unmodified; its original terms apply. WebView2 and Tabler notices are included with their components.

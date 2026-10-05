# Wanxiang BGM Player

A Windows 10/11 desktop soundboard that plays MP3 files with global keyboard shortcuts and mixes your selected microphone with the app's music through VB-CABLE. The song library starts empty. Add or remove songs, customize shortcuts, and adjust headphone and virtual-channel music volume independently.

## Download and Install

[Download the Windows installer](https://github.com/spdore/wanxiang-bgm/raw/refs/heads/main/WanxiangBgmSetup.exe)

The installer includes the complete, unmodified official standard VB-CABLE driver package. It skips driver installation when VB-CABLE is already detected. Otherwise, you can choose to launch the official driver installer. Driver installation requires administrator approval; follow the vendor's instructions and restart Windows when prompted. The player itself installs for the current user.

## Quick Start

1. Launch the player. No songs or shortcuts are configured by default.
2. Click the Add Songs button (labeled “添加歌曲” in the Chinese interface) to import your own MP3 files.
3. Click a song's key field to assign a global shortcut.
4. Press its shortcut to start playback. While music is playing, pressing any song shortcut stops it. Only one song can play at a time.
5. Select your physical microphone in the player, then select **CABLE Output** as the microphone in your game or chat app.

The player sends the selected microphone and its own BGM to **CABLE Input**. Other computer audio is not included. Chat apps receive the mixed signal from **CABLE Output**.

Headphone playback follows the Windows default output device. Headphone music volume and music sent to the virtual channel have separate sliders. Each slider's 0–100% range maps to an actual gain of 0–30%; virtual-channel music volume does not change microphone volume.

Removing a song from the library keeps its audio file.

## Requirements

- Windows 10 or Windows 11.
- .NET Framework 4.8.
- VB-CABLE for sending microphone and music together to chat apps.

The installer is currently unsigned. Uninstalling the player preserves personal songs and settings and does not remove the shared VB-CABLE driver.

## Build from Source

The application uses C# and Windows Forms with the .NET Framework compiler. Run these commands in PowerShell from the repository root:

```powershell
.\build.ps1
.\build-share.ps1
```

The first command builds `BgmHotkey.exe`. The second creates the distribution installer in the `分享安装包` directory.

## Data and Privacy

The distribution build starts with an empty song library and no shortcut bindings. User data is stored in `%LOCALAPPDATA%\BgmHotkeyShare`, with imported songs in its `bgm` subdirectory.

The repository excludes the developer's songs, device settings, recordings, logs, screenshots, and machine-specific absolute paths. Published commits use a GitHub noreply email address.

## Third-Party Components

VB-CABLE is provided by VB-Audio as donationware. Please consider supporting the vendor through a donation or license purchase.

- [Official VB-CABLE website](https://vb-audio.com/Cable/)
- [Distribution and licensing terms](https://vb-audio.com/Services/licensing.htm)

`VBCABLE_Driver_Pack45.zip` is the complete, unmodified official driver package, including its original README and license. Distribution for professional use must comply with the vendor's additional licensing requirements. VB-CABLE A+B and C+D are not included. Third-party components retain their respective copyrights and licenses.

# Changelog

## 1.9.6

- Replaces the 214 MB WebView2 Evergreen offline installer embedded in the setup package with Microsoft's small official Evergreen Bootstrapper.
- Reuses an existing WebView2 Runtime. If missing, setup runs the documented `/silent /install` Bootstrapper command and verifies the installed Runtime; internet access is required for this case.
- Keeps the signed VB-CABLE driver package and the existing installation flow. Setup is no longer fully offline when WebView2 Runtime is absent.

## 1.9.5

- Centers the playback thumb on the progress rail by resetting the native range-control appearance and aligning the thumb to the transparent slider track.
- Disables WebView2 zoom controls so Ctrl+mouse wheel cannot change the interface scale.

## 1.9.4

- Restores the GRAPHITE studio-console palette in light mode with warm amber accents, keeping the dark graphite-and-amber theme.
- Aligns each volume fader's handle to its vertical rail and reduces the width of the independent output meter segments.
- Separates the player's progress rail and fill from the live spectrum display.
- Computes 32 logarithmic spectrum bands from the application's BGM PCM stream only, ordered from bass on the left to treble on the right; microphone audio is not included.

## 1.9.3

- Reworks the existing interface styling into the GRAPHITE console direction while keeping the 1.9.2 top navigation and page layouts.
- Uses graphite and amber for dark mode, and a warm graphite-and-amber palette for light mode.
- Refines buttons, track cards, dialogs, toast surfaces, and modal backdrops while keeping the 1.9.2 interaction layout.
- Replaces the two home-page volume controls with independent vertical headphone and voice-channel faders and route-specific live level meters.
- Draws the player progress track with vertical level bars whose height follows the measured BGM output peak; seeking and saved anchor behavior are unchanged.

## 1.9.2

- Fixes focus styling so playback hotkeys no longer reveal an outline around the last clicked navigation button; keyboard navigation retains visible focus feedback.
- Closes the shortcut playback range editor immediately after settings are persisted, without a success toast. Validation and persistence failures remain visible inside the editor and can be retried.
- Replaces the browser-native scene deletion confirmation with an application-themed dialog that keeps the selected scene ID fixed until the user's choice.

## 1.9.1

- Moves scene selection and management to a dedicated top-level **Scenes** page, alongside My Library, Random Playback, and Voice Settings. Scene create, copy, rename, switch, and delete actions are available from the page.
- Simplifies track cards by removing format, shortcut-range, loudness, and gain detail lines. The full track name and filename remain available through the name tooltip.
- Removes loudness analysis controls and progress from Voice Settings. The initial library normalization now runs silently in the background, and later imports are queued without rescanning the library.
- Stores the first measurable track as a loudness baseline. Newly added tracks are analyzed or reused from cache and retargeted against that baseline; sound-pack imports queue random-only audio assets as well as shortcut tracks.

## 1.9.0

This local update builds on 1.8.4 and adds playlist scenes, non-repeating random playback, per-track loudness analysis, and portable sound packs. The application remains a C# 5 / .NET Framework 4.8 WinForms application with its existing WebView2 interface and audio engine.

- Adds per-scene song membership, shortcut bindings, playback anchors, random selection, and random hotkeys. Audio devices and master volumes remain global. Existing 1.8.4 settings migrate once into a default scene, with the settings store retaining its backup.
- Adds per-scene shuffle bags. A round consumes each selected, available song once; the next round avoids starting with the preceding round's last song when at least two songs are available. Failed songs are skipped until the library refreshes.
- Adds background BS.1770 / EBU R128 integrated-loudness analysis with an -18 LUFS target, a +11 dB maximum positive gain, and a per-track -1 dBFS sample-peak ceiling. The gain cap keeps native MP3 playback within MCI's 0–1000 volume-factor range, allowing the same per-track gain on headphones and the virtual mix. The cache is keyed by file hash, target, and algorithm version. Silent or unreadable files use unity gain.
- Adds a -1 dBFS sample-peak limiter after microphone and BGM are mixed for the virtual channel. This limiter does not measure inter-sample true peaks.
- Adds `.wbgm` export/import. A pack includes one scene, its shortcut songs, its random-playlist audio, safe metadata, file hashes, and validated loudness analysis. It excludes absolute paths, global device and volume settings, and unrelated user data. Import previews first, reuses matching content, avoids filename overwrites, supports cancellation, and rolls back newly copied audio when the import cannot complete.
- Keeps the compact two-column track cards, existing light and dark themes, playback ranges, full-song seeking, independent music outputs, and microphone pass-through.
- Produces a new empty-library installer with the official WebView2 Evergreen offline runtime and signed VB-CABLE driver package. Existing dependencies are reused. VB-CABLE installation still requires administrator approval and may require a restart.

## 1.8.4

- Adds multi-format audio import, per-track shortcut playback ranges, and full-song manual seeking that does not alter saved anchors.
- Adds a searchable random-playback list that includes files without individual shortcuts and supports bulk selection of visible results.
- Refines the compact two-column track cards and keeps the interface available in light and dark themes.
- Packages an empty-library Windows installer with the official Microsoft WebView2 Evergreen offline runtime and signed VB-CABLE driver package. Existing dependencies are detected and reused.
- Separates application source, UI, packaging code, third-party notices, local release outputs, and local-only user content.

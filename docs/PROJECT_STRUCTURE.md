# Project Structure

```text
app/
  assets/                   Application icon and brand mark
  src/                      C# application source
  ui/                       Embedded WebView2 interface
  app.config                .NET runtime configuration
  app.manifest              Windows application manifest
docs/                       Project and installation documentation
licenses/                   Third-party license and notice files
packaging/                  Installer source, build script, and guide
vendor/
  vb-cable/                 Official signed driver package archive
  webview2/
    sdk/net462/             WebView2 SDK assemblies used at build time
    loader/                  x86 and x64 WebView2 loader DLLs
    bootstrapper/            Local-only Microsoft Evergreen Bootstrapper
release/                    Local build output; not uploaded by the local build
local-only/                 Ignored local build cache, diagnostics, and private media
bgm/                        Local development library; never published
build.ps1                   Local player build entry point
README.md                   English project overview
CHANGELOG.md                Release history
```

`release/`, `local-only/`, and the root `bgm/` folder are excluded from Git. The Bootstrapper under `vendor/webview2/bootstrapper/` is also excluded; the build validates its Microsoft signature and embeds it in the release installer. The full Evergreen Runtime is downloaded by the Bootstrapper only when the installing computer does not already have it.

The scene and audio implementation is split across `Settings.cs` (scene migration and persistence), `RandomShuffleBag.cs` (scene-scoped no-repeat queues), `LoudnessAnalyzer.cs` (background integrated-loudness analysis), and `SoundPackService.cs` (validated portable scene packages). `AudioEngine.cs` retains the shared microphone/BGM mixer and virtual-channel limiter.

The share build writes intermediate files to `local-only/build-staging/`. This keeps transient builds, logs, test fixtures, and personal media outside the public project tree. The local development executable is built in the project root beside the local `bgm/` folder; user-installed builds instead store songs and settings in the current user's local application data folder.

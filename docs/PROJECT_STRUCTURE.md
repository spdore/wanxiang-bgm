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
    runtime-installer/       Local-only Microsoft Evergreen offline runtime
release/                    Local build output; installer is published as a Release asset
local-only/                 Ignored local build cache, diagnostics, and private media
bgm/                        Local development library; never published
build.ps1                   Local player build entry point
README.md                   English project overview
CHANGELOG.md                Release history
```

`release/`, `local-only/`, and the root `bgm/` folder are excluded from Git. The runtime installer under `vendor/webview2/runtime-installer/` is also excluded because it is a large, version-updated Microsoft binary. The published installer already contains it.

The share build writes intermediate files to `local-only/build-staging/`. This keeps transient builds, logs, test fixtures, and personal media outside the public project tree. The local development executable is built in the project root beside the local `bgm/` folder; user-installed builds instead store songs and settings in the current user's local application data folder.

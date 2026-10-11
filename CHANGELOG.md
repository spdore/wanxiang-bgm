# Changelog

## 1.8.4

Compared with the previous public project snapshot, this release adds a reorganized source tree and updated documentation while keeping the established Windows audio and shortcut workflow.

- Adds multi-format audio import, per-track shortcut playback ranges, and full-song manual seeking that does not alter saved anchors.
- Adds a searchable random-playback list that includes files without individual shortcuts and supports bulk selection of visible results.
- Refines the compact two-column track cards and keeps the interface available in light and dark themes.
- Packages an empty-library Windows installer with the official Microsoft WebView2 Evergreen offline runtime and signed VB-CABLE driver package. Existing dependencies are detected and reused.
- Separates application source, UI, packaging code, third-party notices, local release outputs, and local-only user content.
- Keeps music files, promotional media, diagnostics, workstation-specific settings, and the large WebView2 offline runtime out of the public source tree.

The release asset contains only the current 1.8.4 installer. No previous installer is included in this release.

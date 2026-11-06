# Client release handoff

The CI client jobs produce short-lived validation artifacts for each workflow
run:

- `vaultflow-android-unsigned-<sha>` contains the Android APK candidate.
- `vaultflow-windows-installer-<sha>` contains a Windows x64 setup EXE for the
  desktop register.

The Windows installer is built from a self-contained .NET and Windows App SDK
publish. It installs per user, creates a Start Menu shortcut, optionally creates
a desktop shortcut, and provides an uninstall entry. If WebView2 is absent, it
runs Microsoft's Evergreen bootstrapper, which needs internet access on that
device. The installer contains only the desktop register; it still needs a
reachable VaultFlow API for first enrolment and online operations. It does not
remove local register data on uninstall.

To build it locally on Windows, install the .NET 10 SDK, MAUI Windows workload,
and Inno Setup 6, then run:

```powershell
.\scripts\build-desktop-installer.ps1
```

The result is `artifacts/installer/VaultFlow-POS-Setup-<version>-win-x64.exe`.
For an offline deployment, download Microsoft's WebView2 standalone installer
separately and install it on the register before running VaultFlow setup. The
Windows setup EXE is currently unsigned; sign it with a trusted code-signing
certificate before distributing it outside a controlled environment. Android
still needs a release keystore and signed AAB configuration. Inno Setup 6.5+
also requires a commercial license for commercial use, including CI builds;
arrange that license before using this installer for a production business.

Artifacts are retained for 14 days and should be downloaded only for validation
or deployment rehearsal. The production server images are published separately
by the `publish-images` job on pushes to `main`.

# Client release handoff

The CI client jobs produce short-lived validation artifacts for each workflow
run:

- `vaultflow-android-unsigned-<sha>` contains the Android APK candidate.
- `vaultflow-windows-unpackaged-<sha>` contains the Windows unpackaged publish
  output.

These artifacts prove that the MAUI client can be published from the pinned
workloads. They are not store releases. Android still needs a release keystore
and signed AAB configuration; Windows still needs packaged MSIX settings and a
trusted signing certificate. Those credentials must be supplied through the CI
secret store before adding a release-signing job.

Artifacts are retained for 14 days and should be downloaded only for validation
or deployment rehearsal. The production server images are published separately
by the `publish-images` job on pushes to `main`.

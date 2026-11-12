# Releasing

Push a version tag and [`.github/workflows/release.yml`](../.github/workflows/release.yml) builds every platform
and publishes one GitHub release:

```bash
git tag v1.2.0 && git push origin v1.2.0      # a release
git tag v1.3.0-beta1 && git push origin v1.3.0-beta1  # a pre-release (anything after a '-')
```

Or run the **Release** workflow by hand from the Actions tab and type a version (`1.2.0`). It creates the `v1.2.0`
tag on the commit you ran it from.

## What it publishes

| Asset | Built on | Notes |
|---|---|---|
| `ShinyCommander-<version>-macos-arm64.zip` | `xcode-27` | The `.app`, Developer ID signed, hardened runtime, notarized and stapled. Apple silicon only |
| `ShinyCommander-<version>-windows-x64-setup.exe`, `…-arm64-setup.exe` | `windows-latest` | Inno Setup, per-user into `%LOCALAPPDATA%\Programs\ShinyCommander`, no admin. Self-contained (.NET + Windows App SDK). **Unsigned**: SmartScreen warns until there's a code-signing certificate |
| `ShinyCommander-<version>-linux-x64.deb/.rpm`, `…-arm64.deb/.rpm` | `ubuntu-24.04` | nfpm. Self-contained, installed to `/opt/shinycommander` with `/usr/bin/shinycommander` and a menu entry |
| `SHA256SUMS.txt` | | Checksums of everything above |

The tag's version goes into `Version`; the macOS bundle and Windows file version get it without the suffix
(`1.3.0-beta1` → `1.3.0`, since `CFBundleShortVersionString` takes at most three integers), and the build number is
the workflow run number.

## Required secrets

Set on the `ShinySoftware/Commander` repository (or the org). The Windows and Linux jobs need none.

| Secret | What |
|---|---|
| `APPLE_MACOS_DIRECT_CERTIFICATE_P12` | The **Developer ID Application** certificate and its private key, exported as .p12 and base64-encoded (`base64 -i cert.p12 \| pbcopy`) |
| `APPLE_MACOS_DIRECT_CERTIFICATE_PASSWORD` | The .p12's export password |
| `APPLE_MY_IDENTITY_P8_KEY` | An App Store Connect API key (.p8) for `notarytool`, as its PEM text or base64 |
| `APPLE_MY_IDENTITY_KEY_ID` | That key's ID |
| `APPLE_MY_IDENTITY_ISSUER_ID` | The issuer ID from App Store Connect → Users and Access → Integrations |

## Moving parts

- **macOS signing.** `src/ShinyCommander.MacOS` turns on the hardened runtime and `Entitlements.plist` (just
  `allow-jit`, for CoreCLR) in Release **only when `CodesignKey` is set**, which CI does. A local `-c Release` build
  stays ad-hoc signed without the hardened runtime: library validation refuses ad-hoc signed dylibs, so an ad-hoc
  hardened app dies in dyld before it starts.
- **Pinned toolchain.** `WORKLOAD_VERSION` and the Xcode the macOS job selects (27.1) have to agree: each macOS
  workload accepts exactly one Xcode. Bump them together.
- **Linux dependencies.** The packages depend on GTK 4, GdkPixbuf and ICU only. Not WebKitGTK: the GTK4 backend loads
  it only for a WebView, and ShinyCommander has none. If one is ever added (a BlazorWebView, say), add
  `libwebkitgtk-6.0-4` / `webkitgtk6.0` to `installers/linux/nfpm.yaml`.
- **Windows upgrades.** The `AppId` in `installers/windows/ShinyCommander.iss` is how an upgrade finds the copy it
  replaces. Never change it.
- **Debug-only bits.** The DevFlow agent and the UI test hooks are referenced and compiled only in Debug, so none of
  them ship.

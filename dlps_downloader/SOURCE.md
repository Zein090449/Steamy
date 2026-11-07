# dlps_downloader (vendored)

This folder vendors the `dlps` Node.js CLI that powers Steamy's Library → Emulated
download flow. Steamy spawns `node src/index.js download "<title>" -o "<dir>"` and
streams the log/progress in-app.

- Upstream: https://github.com/jayjun911/dlps_downloader — forked and maintained by
  the Steamy maintainer. No upstream license file was provided; rights remain with
  the original authors.
- Only the source (`src/`), the package manifests, and the readme are committed.
  Local state (`.env`, `data/`, `downloads/`), `node_modules/`, and diagnostics are
  intentionally excluded (see `.gitignore`).
- Release packaging runs `npm ci` here and copies the source plus `node_modules/`
  next to `Steamy.exe`, so the app auto-detects it. Running it requires Node.js on
  the user's machine; the npm dependencies are bundled in the release ZIP.
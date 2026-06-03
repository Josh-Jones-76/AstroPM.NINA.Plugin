# Astro PM — N.I.N.A. Plugin

Syncs imaging targets from [Astro PM (Astro Project Manager)](https://astro-pm.com) to N.I.N.A.'s Framing Assistant.

Pull a target from your Astro PM project list and the plugin loads it into the Framing Assistant with the right coordinates, camera parameters, rotation, mosaic panel layout, and panel overlap — no manual entry, no copy-paste mistakes.

## Requirements

- N.I.N.A. **3.0.0.9001** or newer
- An [Astro PM](https://astro-pm.com) license — the plugin syncs against your account's project data

## Features

- Target selector ComboBox injected directly into the Framing Assistant UI
- Pulls coordinates, camera parameters, rotation, mosaic panel count, and panel overlap from your Astro PM project
- Filterable target list in the plugin Options page (by Status, Location, Scope, Camera)
- One-click "Load to Framing" from the Options page

## Install

### From the N.I.N.A. plugin manager (once published)
Open N.I.N.A. → Plugins tab → search for "Astro PM" → Install.

### Manual install
1. Build the plugin (see **Building from source** below).
2. Copy `AstroPM.NINA.Plugin.dll` from `bin\Release\` to your NINA plugins folder.
3. Restart N.I.N.A.

## Configuration

Open N.I.N.A. → Options → Plugins → Astro PM and enter your Astro PM credentials.

Settings persist in a `settings.json` file managed by the plugin (outside the version folder, so updates don't wipe them).

## Building from source

Requires .NET 8 SDK and Windows.

```powershell
# Stop NINA first — it locks the DLL
Stop-Process -Name 'NINA' -Force -ErrorAction SilentlyContinue

dotnet build -c Release
```

Both Debug and Release output stay inside the project folder (`bin\Debug\` and `bin\Release\`).

## License

[MIT](LICENSE) © 2026 Astro PM

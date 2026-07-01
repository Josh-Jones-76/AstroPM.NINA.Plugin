import re

with open(r'C:\AICodeProjects\ASG.AstroPM\ASG.AstroPM.NINA\Assets\nina_SVGDictionary.xaml', 'r') as f:
    content = f.read()

pattern = r'<GeometryGroup\s+x:Key="([^"]+)">(.*?)</GeometryGroup>'
matches = re.findall(pattern, content, re.DOTALL)

nina_icons = []
for key, body in matches:
    figures = re.findall(r'Figures="([^"]+)"', body)
    if figures:
        all_nums = []
        for fig in figures:
            nums = re.findall(r'[-+]?[0-9]*\.?[0-9]+(?:[eE][-+]?[0-9]+)?', fig)
            all_nums.extend([float(n) for n in nums])
        max_val = max(abs(n) for n in all_nums) if all_nums else 24
        vb = max(max_val * 1.05, 24)
        nina_icons.append((key, figures, vb))

custom_icons = [
    # --- APM text block (centered, full width) ---
    ("APM Centered", 24, [
        # Box border centered
        "M0.5,4 L23.5,4 L23.5,20 L0.5,20 Z M2,5.5 L22,5.5 L22,18.5 L2,18.5 Z",
        # A centered
        "M3,17 L5.5,6.5 L7,6.5 L9.5,17 L8,17 L7.2,14 L5.3,14 L4.5,17 Z M5.7,12.5 L6.25,9 L6.8,12.5 Z",
        # P centered
        "M10.5,6.5 L14.5,6.5 Q16.5,6.5 16.5,9.5 Q16.5,12 14.5,12 L12,12 L12,17 L10.5,17 Z M12,8 L14,8 Q15,8 15,9.5 Q15,10.5 14,10.5 L12,10.5 Z",
        # M centered
        "M17.5,6.5 L19,6.5 L20.5,11.5 L22,6.5 L23.5,6.5 L23.5,17 L22,17 L22,10.5 L20.5,14.5 L20.5,14.5 L19,10.5 L19,17 L17.5,17 Z",
    ]),

    # --- APM with star behind ---
    ("APM + Star Behind", 24, [
        # 4-point star behind everything (large, centered)
        "M12,0 L14,9 L23,12 L14,15 L12,24 L10,15 L1,12 L10,9 Z",
        # Dark box cutout (filled box acts as background for text)
        "M2,7 L22,7 L22,18 L2,18 Z",
        # A
        "M3.5,16.5 L5.5,8 L7,8 L9,16.5 L7.8,16.5 L7.2,14 L5.3,14 L4.7,16.5 Z M5.7,12.5 L6.25,9.5 L6.8,12.5 Z",
        # P
        "M10,8 L13.5,8 Q15.5,8 15.5,10.5 Q15.5,12.5 13.5,12.5 L11.5,12.5 L11.5,16.5 L10,16.5 Z M11.5,9.3 L13,9.3 Q14,9.3 14,10.5 Q14,11.3 13,11.3 L11.5,11.3 Z",
        # M
        "M16.5,8 L18,8 L19.5,12 L21,8 L22.5,8 L22.5,16.5 L21.2,16.5 L21.2,10.5 L19.5,14 L19.5,14 L17.8,10.5 L17.8,16.5 L16.5,16.5 Z",
    ]),

    # --- APM with small graph curve above ---
    ("APM + Graph Above", 24, [
        # Mini L-axis above
        "M1,0.5 L2.5,0.5 L2.5,9 L23,9 L23,10.5 L1,10.5 Z",
        # Mini altitude curve (filled)
        "M3,10 C6,7 9,2 12,1.5 C15,2 18,7 22,10 Z",
        # Box for text
        "M0.5,12 L23.5,12 L23.5,23.5 L0.5,23.5 Z M2,13.5 L22,13.5 L22,22 L2,22 Z",
        # A
        "M3,20.5 L5.2,14 L6.7,14 L8.9,20.5 L7.6,20.5 L7,18.5 L5.4,18.5 L4.8,20.5 Z M5.7,17.2 L6.2,15.2 L6.7,17.2 Z",
        # P
        "M9.8,14 L13.2,14 Q15,14 15,16.2 Q15,18 13.2,18 L11.3,18 L11.3,20.5 L9.8,20.5 Z M11.3,15.3 L12.8,15.3 Q13.5,15.3 13.5,16.2 Q13.5,16.8 12.8,16.8 L11.3,16.8 Z",
        # M
        "M16,14 L17.3,14 L18.8,17.5 L20.3,14 L21.6,14 L21.6,20.5 L20.3,20.5 L20.3,16.5 L18.8,19.5 L18.8,19.5 L17.3,16.5 L17.3,20.5 L16,20.5 Z",
    ]),

    # --- APM with line items / sequence list above ---
    ("APM + List Above", 24, [
        # Three small line items (like a sequence list)
        "M1,1 L2.5,1 L2.5,2.5 L1,2.5 Z",
        "M4,1 L15,1 L15,2.5 L4,2.5 Z",
        "M1,4 L2.5,4 L2.5,5.5 L1,5.5 Z",
        "M4,4 L12,4 L12,5.5 L4,5.5 Z",
        "M1,7 L2.5,7 L2.5,8.5 L1,8.5 Z",
        "M4,7 L17,7 L17,8.5 L4,8.5 Z",
        # Box for text
        "M0.5,11 L23.5,11 L23.5,23.5 L0.5,23.5 Z M2,12.5 L22,12.5 L22,22 L2,22 Z",
        # A
        "M3,20.5 L5.2,13 L6.7,13 L8.9,20.5 L7.6,20.5 L7,18.5 L5.4,18.5 L4.8,20.5 Z M5.7,17.2 L6.2,14.5 L6.7,17.2 Z",
        # P
        "M9.8,13 L13.2,13 Q15,13 15,15.2 Q15,17 13.2,17 L11.3,17 L11.3,20.5 L9.8,20.5 Z M11.3,14.3 L12.8,14.3 Q13.5,14.3 13.5,15.2 Q13.5,15.8 12.8,15.8 L11.3,15.8 Z",
        # M
        "M16,13 L17.3,13 L18.8,16.5 L20.3,13 L21.6,13 L21.6,20.5 L20.3,20.5 L20.3,16 L18.8,19 L18.8,19 L17.3,16 L17.3,20.5 L16,20.5 Z",
    ]),

    # --- APM with checkmarks above (completed tasks) ---
    ("APM + Checks Above", 24, [
        # Three checkmarks with lines
        "M1,1.5 L2.5,3.5 L5.5,0.5 L6.5,1.5 L2.5,5.5 L0,3 Z",
        "M7.5,1 L18,1 L18,2.8 L7.5,2.8 Z",
        "M1,6.5 L2.5,8.5 L5.5,5.5 L6.5,6.5 L2.5,10.5 L0,8 Z",
        "M7.5,6 L14,6 L14,7.8 L7.5,7.8 Z",
        # Box for text
        "M0.5,12 L23.5,12 L23.5,23.5 L0.5,23.5 Z M2,13.5 L22,13.5 L22,22 L2,22 Z",
        # A
        "M3,20.5 L5.2,14 L6.7,14 L8.9,20.5 L7.6,20.5 L7,18.5 L5.4,18.5 L4.8,20.5 Z M5.7,17.2 L6.2,15.2 L6.7,17.2 Z",
        # P
        "M9.8,14 L13.2,14 Q15,14 15,16.2 Q15,18 13.2,18 L11.3,18 L11.3,20.5 L9.8,20.5 Z M11.3,15.3 L12.8,15.3 Q13.5,15.3 13.5,16.2 Q13.5,16.8 12.8,16.8 L11.3,16.8 Z",
        # M
        "M16,14 L17.3,14 L18.8,17.5 L20.3,14 L21.6,14 L21.6,20.5 L20.3,20.5 L20.3,16.5 L18.8,19.5 L18.8,19.5 L17.3,16.5 L17.3,20.5 L16,20.5 Z",
    ]),

    # --- APM with star above text ---
    ("APM + Star Above", 24, [
        # 4-point star centered above
        "M12,0 L13,4 L17,5 L13,6 L12,10 L11,6 L7,5 L11,4 Z",
        # Box for text
        "M0.5,12 L23.5,12 L23.5,23.5 L0.5,23.5 Z M2,13.5 L22,13.5 L22,22 L2,22 Z",
        # A
        "M3,20.5 L5.2,14 L6.7,14 L8.9,20.5 L7.6,20.5 L7,18.5 L5.4,18.5 L4.8,20.5 Z M5.7,17.2 L6.2,15.2 L6.7,17.2 Z",
        # P
        "M9.8,14 L13.2,14 Q15,14 15,16.2 Q15,18 13.2,18 L11.3,18 L11.3,20.5 L9.8,20.5 Z M11.3,15.3 L12.8,15.3 Q13.5,15.3 13.5,16.2 Q13.5,16.8 12.8,16.8 L11.3,16.8 Z",
        # M
        "M16,14 L17.3,14 L18.8,17.5 L20.3,14 L21.6,14 L21.6,20.5 L20.3,20.5 L20.3,16.5 L18.8,19.5 L18.8,19.5 L17.3,16.5 L17.3,20.5 L16,20.5 Z",
    ]),

    # --- Just APM no box (clean) ---
    ("APM No Box", 24, [
        # A (larger, centered vertically)
        "M1,22 L5,2 L7,2 L11,22 L9,22 L8,18 L4,18 L3,22 Z M4.5,16 L6,6 L7.5,16 Z",
        # P
        "M12,2 L17,2 Q20,2 20,6.5 Q20,11 17,11 L14,11 L14,22 L12,22 Z M14,4 L16.5,4 Q18,4 18,6.5 Q18,9 16.5,9 L14,9 Z",
        # M
        "M21,22 L21,2 L23,2 L25,10 L27,2 L29,2 L29,22 L27,22 L27,8 L25,15 L23,8 L23,22 Z",
    ]),

    ("Compass Star", 24, [
        "M12,0 L14,10 L24,12 L14,14 L12,24 L10,14 L0,12 L10,10 Z"
    ]),
    ("Star + Ring", 24, [
        "M12,0.5 A11.5,11.5 0 1,1 12,23.5 A11.5,11.5 0 1,1 12,0.5 Z M12,2 A10,10 0 1,0 12,22 A10,10 0 1,0 12,2 Z",
        "M12,3 L13,9.5 L19,12 L13,14.5 L12,21 L11,14.5 L5,12 L11,9.5 Z"
    ]),
    ("8-Point Star", 24, [
        "M12,0 L13.5,9 L18,3 L15,9.5 L24,12 L15,14.5 L18,21 L13.5,15 L12,24 L10.5,15 L6,21 L9,14.5 L0,12 L9,9.5 L6,3 L10.5,9 Z"
    ]),
    ("Moon + Star", 24, [
        "M9,2 A9,9 0 1,0 9,22 A7,7 0 1,1 9,2 Z",
        "M19,3 L19.8,5.5 L22,6 L19.8,6.5 L19,9 L18.2,6.5 L16,6 L18.2,5.5 Z"
    ]),
    ("Telescope", 24, [
        "M2,7 L14,3 L16.5,9.5 L4.5,13.5 Z",
        "M14,3 L22,1 L23.5,5.5 L16.5,9.5 Z",
        "M8.5,11 L10,11 L10,22 L8.5,22 Z",
        "M5.5,22 L8.5,15 L9.5,15.5 L6.5,22 Z",
        "M13,22 L10,15.5 L9,15 L12,22 Z",
        "M5,21.5 L13.5,21.5 L13.5,23 L5,23 Z",
    ]),
    ("Checklist", 24, [
        "M4,0 L20,0 L20,24 L4,24 Z M5.5,1.5 L18.5,1.5 L18.5,22.5 L5.5,22.5 Z",
        "M7,7.5 L9.5,10 L10.5,9 L16,3.5 L17,4.5 L10.5,11 L9.5,12 L6,8.5 Z",
        "M7,14 L17,14 L17,15.5 L7,15.5 Z",
        "M7,18 L14,18 L14,19.5 L7,19.5 Z",
    ]),
    ("Gauge", 24, [
        "M2,19 A11,11 0 1,1 22,19 L21,17.5 A9.5,9.5 0 1,0 3,17.5 Z",
        "M11.3,13.5 L16,5.5 L12.7,13.5 Z",
        "M12,12 A2,2 0 1,1 12,16 A2,2 0 1,1 12,12 Z",
        "M5,15.5 L6,15.5 L6,17.5 L5,17.5 Z",
        "M11.5,4 L12.5,4 L12.5,6 L11.5,6 Z",
        "M18,15.5 L19,15.5 L19,17.5 L18,17.5 Z",
    ]),
    ("Night Sky", 24, [
        "M7,4 A1.3,1.3 0 1,1 7,6.6 A1.3,1.3 0 1,1 7,4 Z",
        "M14,2.5 A1,1 0 1,1 14,4.5 A1,1 0 1,1 14,2.5 Z",
        "M18,6 A1.1,1.1 0 1,1 18,8.2 A1.1,1.1 0 1,1 18,6 Z",
        "M4,8.5 A0.8,0.8 0 1,1 4,10.1 A0.8,0.8 0 1,1 4,8.5 Z",
        "M11,7.5 A0.9,0.9 0 1,1 11,9.3 A0.9,0.9 0 1,1 11,7.5 Z",
        "M20.5,3.5 A0.7,0.7 0 1,1 20.5,4.9 A0.7,0.7 0 1,1 20.5,3.5 Z",
        "M0,16 Q6,12 12,14 Q18,16 24,13 L24,15 Q18,18 12,16 Q6,14 0,18 Z",
        "M0,19 L24,19 L24,21 L0,21 Z",
    ]),
    ("Clock", 24, [
        "M12,0.5 A11.5,11.5 0 1,1 12,23.5 A11.5,11.5 0 1,1 12,0.5 Z M12,2 A10,10 0 1,0 12,22 A10,10 0 1,0 12,2 Z",
        "M11.3,12 L11.3,5.5 L12.7,5.5 L12.7,12 Z",
        "M12,11.5 L17,8.5 L17.5,9.5 L12.5,12.5 Z",
        "M12,11 A1.2,1.2 0 1,1 12,13.4 A1.2,1.2 0 1,1 12,11 Z",
        "M12,3 A0.9,0.9 0 1,1 12,4.8 A0.9,0.9 0 1,1 12,3 Z",
        "M19.2,11.1 A0.9,0.9 0 1,1 19.2,12.9 A0.9,0.9 0 1,1 19.2,11.1 Z",
        "M12,19.2 A0.9,0.9 0 1,1 12,21 A0.9,0.9 0 1,1 12,19.2 Z",
        "M3,11.1 A0.9,0.9 0 1,1 3,12.9 A0.9,0.9 0 1,1 3,11.1 Z",
    ]),
    ("Altitude Chart", 24, [
        "M2,2 L4,2 L4,20 L22,20 L22,22 L2,22 Z",
        "M5,20 C8,16 10,6 13,5 C16,6 18,16 21,20 Z",
    ]),
    ("Multi-Target Chart", 24, [
        "M2,2 L3.5,2 L3.5,20.5 L22,20.5 L22,22 L2,22 Z",
        "M4,20 C6,17 8,6 12,5 C16,6 18,15 21,20 Z",
        "M12,3.5 L12.5,5 L14,5 L12.8,5.8 L13.3,7 L12,6.2 L10.7,7 L11.2,5.8 L10,5 L11.5,5 Z",
    ]),
    ("Star + Chart", 24, [
        "M5,0 L6,3.5 L9.5,5 L6,6.5 L5,10 L4,6.5 L0.5,5 L4,3.5 Z",
        "M2,13 L3.5,13 L3.5,21 L22,21 L22,22.5 L2,22.5 Z",
        "M4,21 C8,18 11,14 15,13.5 C18,14 20,18 22,21 Z",
        "M19,1 L19.5,2.8 L21.5,3.5 L19.5,4.2 L19,6 L18.5,4.2 L16.5,3.5 L18.5,2.8 Z",
    ]),
]

html = """<!DOCTYPE html>
<html>
<head>
<style>
body { background: #1e1e1e; color: white; font-family: Consolas, monospace; padding: 20px; max-width: 1600px; margin: 0 auto; }
h1 { color: #fff; border-bottom: 2px solid #555; padding-bottom: 10px; }
h2 { color: #ccc; margin-top: 40px; border-bottom: 1px solid #444; padding-bottom: 8px; }
.note { color: #888; font-size: 12px; margin-bottom: 16px; }
.grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(220px, 1fr)); gap: 12px; }
.card { background: #252525; border: 1px solid #444; border-radius: 6px; padding: 12px; text-align: center; cursor: pointer; transition: border-color 0.2s; }
.card:hover { border-color: #888; background: #2a2a2a; }
.card.custom { border-color: #4a6; }
.card.custom:hover { border-color: #6c8; }
.card .sizes { display: flex; justify-content: center; align-items: flex-end; gap: 12px; margin-bottom: 8px; }
.card .label { font-size: 10px; color: #aaa; word-break: break-all; }
.card.custom .label { color: #6c8; }
.size-tag { font-size: 8px; color: #666; }
</style>
</head>
<body>
<h1>Astro PM + NINA Icon Reference</h1>
<p class="note">All icons rendered as <strong>filled geometries</strong> (no stroke) &mdash; exactly how NINA renders ImageGeometry.<br>
Shown at 16px (actual toolbar size), 32px, and 64px.</p>

<h2>Astro PM Custom Icons (Fixed for Fill Rendering)</h2>
<p class="note">Green border = custom candidates. All redesigned as proper filled shapes with real thickness.</p>
<div class="grid">
"""

for name, vb, paths in custom_icons:
    paths_svg = ""
    for p in paths:
        paths_svg += f'<path d="{p}" fill="white" fill-rule="evenodd"/>'
    html += f"""<div class="card custom">
  <div class="sizes">
    <div><svg width="16" height="16" viewBox="0 0 {vb} {vb}">{paths_svg}</svg><div class="size-tag">16</div></div>
    <div><svg width="32" height="32" viewBox="0 0 {vb} {vb}">{paths_svg}</svg><div class="size-tag">32</div></div>
    <div><svg width="64" height="64" viewBox="0 0 {vb} {vb}">{paths_svg}</svg><div class="size-tag">64</div></div>
  </div>
  <div class="label">{name}</div>
</div>
"""

html += """</div>
<h2>NINA Built-in Icons (SVGDictionary.xaml)</h2>
<p class="note">168 icons from NINA source. Any can be loaded via Application.Current.Resources.</p>
<div class="grid">
"""

for key, figures, vb in nina_icons:
    paths_svg = ""
    for fig in figures:
        paths_svg += f'<path d="{fig}" fill="white" fill-rule="evenodd"/>'
    html += f"""<div class="card">
  <div class="sizes">
    <div><svg width="16" height="16" viewBox="0 0 {vb:.0f} {vb:.0f}">{paths_svg}</svg><div class="size-tag">16</div></div>
    <div><svg width="32" height="32" viewBox="0 0 {vb:.0f} {vb:.0f}">{paths_svg}</svg><div class="size-tag">32</div></div>
    <div><svg width="64" height="64" viewBox="0 0 {vb:.0f} {vb:.0f}">{paths_svg}</svg><div class="size-tag">64</div></div>
  </div>
  <div class="label">{key}</div>
</div>
"""

html += "</div>\n</body>\n</html>"

with open(r'C:\AICodeProjects\ASG.AstroPM\ASG.AstroPM.NINA\Assets\icon_reference.html', 'w') as f:
    f.write(html)

print(f"Done: {len(custom_icons)} custom + {len(nina_icons)} NINA icons")

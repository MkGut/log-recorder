"""Builds the aero icon set: SVG sources -> PNG exports -> .ico files for log.Shell.

Run:  python tools/icons/build_icons.py
Needs Microsoft Edge (used headless as the SVG renderer) and Pillow.
"""
import subprocess
import sys
import tempfile
import time
from pathlib import Path
from PIL import Image

HERE = Path(__file__).parent
ROOT = HERE.parents[1]
SVG = HERE / "svg"                                   # tracked sources
PNG = ROOT / "artifacts" / "icons" / "aero" / "png"  # per-size exports (git-ignored)
ASSETS = ROOT / "src" / "log.Shell" / "Assets"
EDGE = r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
ICO_SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
SIMPLE_UP_TO = 32  # sizes at or below this use the simplified drawing

DEFS = """
<defs>
  <linearGradient id="body" x1="0" y1="0" x2="0.25" y2="1">
    <stop offset="0" stop-color="#8FE3FF"/><stop offset=".3" stop-color="#3FA4F5"/>
    <stop offset=".68" stop-color="#2A46DC"/><stop offset="1" stop-color="#2420B4"/>
  </linearGradient>
  <linearGradient id="belly" x1="0" y1="0" x2="0" y2="1">
    <stop offset="0" stop-color="#F4FDFF"/><stop offset="1" stop-color="#9FDcff"/>
  </linearGradient>
  <linearGradient id="rim" x1="0" y1="0" x2="1" y2="1">
    <stop offset="0" stop-color="#E9FBFF"/><stop offset=".5" stop-color="#5E8BF5"/>
    <stop offset="1" stop-color="#151A8C"/>
  </linearGradient>
  <linearGradient id="gloss" x1="0" y1="0" x2="0" y2="1">
    <stop offset="0" stop-color="#fff" stop-opacity=".9"/><stop offset="1" stop-color="#fff" stop-opacity=".05"/>
  </linearGradient>
  <radialGradient id="glow" cx=".5" cy=".85" r=".6">
    <stop offset="0" stop-color="#7DF0FF" stop-opacity=".75"/><stop offset="1" stop-color="#7DF0FF" stop-opacity="0"/>
  </radialGradient>
  <radialGradient id="bubble" cx=".35" cy=".3" r=".8">
    <stop offset="0" stop-color="#fff" stop-opacity=".95"/><stop offset=".35" stop-color="#BDEFFF" stop-opacity=".55"/>
    <stop offset="1" stop-color="#4BA8F5" stop-opacity=".75"/>
  </radialGradient>
  <radialGradient id="eye" cx=".4" cy=".35" r=".7">
    <stop offset="0" stop-color="#27409E"/><stop offset="1" stop-color="#050B3C"/>
  </radialGradient>
  <filter id="soft" x="-30%" y="-30%" width="160%" height="160%"><feGaussianBlur stdDeviation="7"/></filter>
  <clipPath id="whaleClip"><path d="{whale}"/></clipPath>
</defs>
"""

WHALE = ("M 22 150 C 22 96 66 70 120 70 C 160 70 186 90 200 114 C 207 104 210 94 209 84 "
         "C 198 78 184 70 176 52 C 193 50 206 56 213 67 C 221 56 235 49 250 51 "
         "C 248 73 238 90 225 99 C 232 150 204 220 122 222 C 62 222 22 198 22 150 Z")


def bubble(x, y, r):
    return (f'<circle cx="{x}" cy="{y}" r="{r}" fill="url(#bubble)" stroke="#E9FBFF" stroke-width="{max(2, r * .14):.1f}"/>'
            f'<ellipse cx="{x - r * .3:.1f}" cy="{y - r * .38:.1f}" rx="{r * .3:.1f}" ry="{r * .18:.1f}" fill="#fff" opacity=".9" '
            f'transform="rotate(-30 {x - r * .3:.1f} {y - r * .38:.1f})"/>')


def whale(simple):
    """The mascot. simple=True drops fine detail so it survives at 16-32 px."""
    eye_r, rim_w = (17, 9) if simple else (13, 5)
    parts = [
        '<ellipse cx="128" cy="232" rx="86" ry="12" fill="#06103F" opacity=".35" filter="url(#soft)"/>' if not simple else "",
        f'<path d="{WHALE}" fill="url(#body)"/>',
        '<g clip-path="url(#whaleClip)">',
        '<ellipse cx="100" cy="226" rx="104" ry="48" fill="url(#belly)"/>',
        '<ellipse cx="120" cy="200" rx="120" ry="60" fill="url(#glow)"/>',
        '<ellipse cx="112" cy="98" rx="74" ry="26" fill="url(#gloss)"/>' if not simple
        else '<ellipse cx="112" cy="100" rx="70" ry="24" fill="#fff" opacity=".55"/>',
        '</g>',
        f'<path d="{WHALE}" fill="none" stroke="url(#rim)" stroke-width="{rim_w}" stroke-linejoin="round"/>',
        f'<circle cx="74" cy="150" r="{eye_r}" fill="url(#eye)"/>',
        f'<circle cx="{74 - eye_r * .3:.1f}" cy="{150 - eye_r * .35:.1f}" r="{eye_r * .36:.1f}" fill="#fff"/>',
    ]
    if not simple:
        parts += [
            '<circle cx="79" cy="156" r="2.6" fill="#fff" opacity=".8"/>',
            '<path d="M 46 175 Q 58 187 74 181" fill="none" stroke="#0A1560" stroke-width="5" stroke-linecap="round"/>',
            '<ellipse cx="96" cy="174" rx="11" ry="6.5" fill="#FF9BD0" opacity=".7"/>',
            '<path d="M 138 176 C 160 172 174 186 168 204 C 152 204 140 194 138 176 Z" fill="#2A46DC" stroke="#9FDCFF" stroke-width="3" stroke-linejoin="round"/>',
            bubble(104, 40, 15), bubble(132, 22, 9), bubble(78, 22, 7),
        ]
    return "\n".join(parts)


BADGES = {
    # name: (fill top, fill bottom, glyph svg drawn in a 100x100 box centred on 50,50)
    "recording": ("#D9FF8A", "#4FB81E", '<circle cx="50" cy="50" r="17" fill="#fff"/>'),
    "paused": ("#FFE79A", "#F09A12", '<rect x="31" y="28" width="14" height="44" rx="5" fill="#fff"/><rect x="55" y="28" width="14" height="44" rx="5" fill="#fff"/>'),
    "finishing": ("#B9F4FF", "#1E9BE8", '<path d="M 28 52 L 44 67 L 73 34" fill="none" stroke="#fff" stroke-width="13" stroke-linecap="round" stroke-linejoin="round"/>'),
    "error": ("#FFB3A1", "#E2341D", '<rect x="43" y="22" width="14" height="36" rx="7" fill="#fff"/><circle cx="50" cy="73" r="8.5" fill="#fff"/>'),
}


def badge(name, simple):
    top, bottom, glyph = BADGES[name]
    size, ring = (132, 12) if simple else (104, 8)
    x = y = 256 - size
    s = size / 100
    return f"""
<linearGradient id="bg" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="{top}"/><stop offset="1" stop-color="{bottom}"/></linearGradient>
<g transform="translate({x} {y}) scale({s})">
  <circle cx="50" cy="50" r="{50 - ring / s / 2:.1f}" fill="url(#bg)" stroke="#fff" stroke-width="{ring / s:.1f}"/>
  {glyph}
  {'' if simple else '<ellipse cx="50" cy="27" rx="27" ry="13" fill="#fff" opacity=".45"/>'}
</g>"""


def svg(inner):
    return (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" width="100%" height="100%" style="position:fixed;inset:0">'
            f'{DEFS.format(whale=WHALE)}{inner}</svg>')


def icon_svg(state, simple):
    body = whale(simple)
    if state == "idle" or state == "app":
        return svg(body)
    # Shrink the mascot toward the top-left so the badge sits over the belly, not the face.
    return svg(f'<g transform="scale(.9)">{body}</g>{badge(state, simple)}')


def render(svg_path, png_path, px):
    """Headless Edge draws the SVG on a transparent page (Chromium gets gradients and blur right)."""
    with tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as profile:
        subprocess.run([EDGE, "--headless=new", "--disable-gpu", "--hide-scrollbars", "--no-first-run",
                        f"--user-data-dir={profile}", "--force-device-scale-factor=1",
                        "--default-background-color=00000000", f"--window-size={px},{px}",
                        f"--screenshot={png_path}", svg_path.as_uri()], check=True, capture_output=True)
        # Edge hands off to a child process, so the file can land after the launcher exits.
        for _ in range(100):
            if png_path.exists() and png_path.stat().st_size:
                return
            time.sleep(.1)
        raise RuntimeError(f"Edge did not write {png_path}")


def build(name, state):
    """Writes both SVG variants, every PNG size, and returns the per-size images."""
    masters = {}
    for simple in (False, True):
        path = SVG / f"{name}{'-small' if simple else ''}.svg"
        path.write_text(icon_svg(state, simple), encoding="utf-8")
        big = PNG / f"_{path.stem}_1024.png"
        render(path, big, 1024)
        masters[simple] = Image.open(big).convert("RGBA")
    images = []
    for size in ICO_SIZES:
        img = masters[size <= SIMPLE_UP_TO].resize((size, size), Image.LANCZOS)
        img.save(PNG / f"{name}_{size}.png")
        images.append(img)
    for big in PNG.glob("_*_1024.png"):
        big.unlink()
    return images


def main():
    SVG.mkdir(exist_ok=True)
    PNG.mkdir(parents=True, exist_ok=True)
    icons = {"app-logo": "app", "tray-recording": "recording", "tray-paused": "paused",
             "tray-finishing": "finishing", "tray-error": "error"}
    only = sys.argv[1:]
    for name, state in icons.items():
        if only and name not in only:
            continue
        images = build(name, state)
        largest = images[-1]
        largest.save(ASSETS / f"{name}.ico", format="ICO", sizes=[(s, s) for s in ICO_SIZES], append_images=images[:-1])
        print("built", name)


if __name__ == "__main__":
    main()

"""Generate the original AutoExtract icon. Development-only dependency: Pillow."""
from pathlib import Path
from io import BytesIO
import struct
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / 'src' / 'AutoExtract.App' / 'Assets'
ASSETS.mkdir(parents=True, exist_ok=True)

def render(size):
    scale = 4
    image = Image.new('RGBA', (256 * scale, 256 * scale))
    draw = ImageDraw.Draw(image)
    def rect(bounds, radius, color):
        draw.rounded_rectangle(tuple(round(v * scale) for v in bounds), radius=radius * scale, fill=color)
    def polygon(points, color):
        draw.polygon([(x * scale, y * scale) for x, y in points], fill=color)
    rect((8, 8, 248, 248), 52, '#193D60')
    rect((18, 18, 238, 238), 43, '#204D76')
    rect((55, 122, 201, 211), 13, '#F2AB45')
    rect((55, 143, 127, 211), 10, '#FFC66C')
    rect((48, 111, 208, 142), 8, '#FFD68C')
    # A bold downward arrow remains legible at 16 px on either taskbar theme.
    polygon([(111, 44), (145, 44), (145, 96), (174, 96),
             (128, 145), (82, 96), (111, 96)], '#F5FCFF')
    if size >= 24:
        for y in (157, 175, 193):
            rect((121, y, 135, y + 8), 2, '#7E552A')
    return image.resize((size, size), Image.Resampling.LANCZOS)

sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
frames = []
for size in sizes:
    buffer = BytesIO()
    render(size).save(buffer, format='PNG')
    frames.append(buffer.getvalue())
offset = 6 + 16 * len(sizes)
header = bytearray(struct.pack('<HHH', 0, 1, len(sizes)))
for size, frame in zip(sizes, frames):
    header.extend(struct.pack('<BBBBHHII', size % 256, size % 256, 0, 0, 1, 32, len(frame), offset))
    offset += len(frame)
(ASSETS / 'AutoExtract.ico').write_bytes(header + b''.join(frames))
render(512).save(ASSETS / 'AutoExtract.png')
print('Generated ICO (9 sizes, 16-256 px) and transparent PNG preview.')

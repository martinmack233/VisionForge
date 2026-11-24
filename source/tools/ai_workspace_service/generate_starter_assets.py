"""Developer-only reproducible synthetic assets. No model, dataset service or GPU is used."""
from pathlib import Path
import hashlib
import json
from PIL import Image, ImageDraw

root = Path(__file__).resolve().parents[2] / 'AIExamples/starter-v1'
root.mkdir(parents=True, exist_ok=True)
classes = [dict(id=i, name=name, color=color) for i, (name, color) in enumerate([
    ('矩形斑块', '#D94A53'), ('圆形斑块', '#2C7BE5'), ('短划痕', '#D99A21'), ('边缘缺口', '#238B68')])]
images = []
for index in range(12):
    image = Image.new('RGB', (320, 240), (205 + index, 218, 230 - index))
    draw = ImageDraw.Draw(image)
    draw.rectangle((8, 8, 311, 231), outline=(140, 158, 178), width=2)
    # A unique harmless stripe pattern distinguishes every independent source image.
    for col in range(index + 1): draw.line((12 + 4 * col, 220, 12 + 4 * col, 226), fill=(75, 95, 115))
    boxes = []
    if index not in (5, 11):
        specs = [(0, 30 + index * 2, 32 + index, 38, 28), (1, 155 - index, 40 + index * 2, 32, 32),
                 (2, 70 + index, 142 - index, 65, 6), (3, 282, 150 + index, 29, 21)]
        for cls, x, y, w, h in specs:
            color = [(125, 45, 53), (35, 85, 135), (95, 70, 30), (50, 110, 80)][cls]
            if cls == 1: draw.ellipse((x, y, x + w - 1, y + h - 1), fill=color)
            else: draw.rectangle((x, y, x + w - 1, y + h - 1), fill=color)
            boxes.append(dict(class_id=cls, x=x, y=y, w=w, h=h))
    file = f'source-{index + 1:02}.png'
    image.save(root / file)
    images.append(dict(file=file, batch=f'independent-batch-{index // 2 + 1:02}', width=320, height=240,
                       sha256=hashlib.sha256((root / file).read_bytes()).hexdigest(), boxes=boxes))
manifest = dict(version=1, id='visionstudio-ai-starter-v1', name='AI入门：合成斑块操作练习',
                provenance='VisionPlatform generated geometric images; original work; no user data or weights.',
                license='CC0-1.0 for these synthetic image resources', classes=classes, images=images,
                recommended=dict(tile_width=128, tile_height=128, overlap=.25, grouping='batch', ratios=[2/3, 1/6, 1/6], seed=42))
(root / 'manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding='utf-8')
print(f'Generated {len(images)} independent images, 6 batches, {sum(len(i["boxes"]) for i in images)} boxes.')

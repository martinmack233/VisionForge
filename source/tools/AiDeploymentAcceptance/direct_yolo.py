"""Read-only parity probe of the existing YOLOStudio business inference path.

The input PNG and request are owned by the C# acceptance tool. No database is opened.
"""
import contextlib
import json
from pathlib import Path
import sys
import threading


def main():
    request = json.loads(Path(sys.argv[1]).read_text(encoding='utf-8-sig'))
    sys.path.insert(0, request['yolo_root'])
    with contextlib.redirect_stdout(sys.stderr):
        from core.inference.backend import DetectorBackend
        from core.inference.config import DetectConfig
        from core.inference.service import detect_raster
        deployment = json.loads(Path(request['model_path']).with_name('deployment.json').read_text(encoding='utf-8'))
        options = request['options']
        config = DetectConfig.from_dict(dict(model=request['model_path'], backend='onnx', device='cpu',
            imgsz=options['ImageSize'], batch=options['BatchSize'], conf=options['Confidence'], iou=options['Iou'],
            tiled=options['Tiled'], tile_width=options['TileWidth'], tile_height=options['TileHeight'],
            overlap=options['Overlap'], merge_iou=options['MergeIou'], max_det=options['MaxDetections'],
            preprocess=deployment['preprocessing']['config'], preprocess_source='original deployment parity probe'))
        with DetectorBackend(config, owner='visionstudio-real-deployment-parity') as backend:
            result, _ = detect_raster(request['image_path'], backend, config, threading.Event())
    print(json.dumps(result, ensure_ascii=False))


if __name__ == '__main__':
    main()

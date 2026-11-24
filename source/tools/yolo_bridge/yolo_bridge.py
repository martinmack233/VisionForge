"""Versioned stdio bridge to an explicitly configured, external YOLOStudio.

No training/inference implementation, model, or third-party package is copied here.
The supplied Python environment must already provide the original dependencies.
"""
from __future__ import annotations

import contextlib
import hashlib
import json
import math
from pathlib import Path
import sys
import threading
import time
from contextlib import nullcontext

MAX_REQUEST_BYTES = 65536
MAX_OUTPUT_BYTES = 8 * 1024 * 1024


def run(request, cache=None):
    started = time.perf_counter()
    timings = {}
    if request.get('schema_version') != 1:
        raise ValueError('Unsupported request schema.')
    root = Path(request['yolo_root']).resolve(strict=True)
    if (root / 'component.json').is_file():
        # Versioned, local detection component; no training repository is required.
        sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'ai_runtime_component'))
        from component import validate, environment
        environment(validate(root))
    model = Path(request['model_path']).resolve(strict=True)
    image = Path(request['image_path']).resolve(strict=True)
    if not root.is_dir() or model.suffix.lower() != '.onnx' or image.suffix.lower() not in ('.png', '.tif'):
        raise ValueError('An existing YOLOStudio, ONNX model, and PNG snapshot are required.')
    for name in ('core/inference/backend.py', 'core/inference/config.py', 'core/inference/service.py'):
        if not (root / name).is_file():
            raise ValueError('Configured YOLOStudio inference module is missing: ' + name)
    sidecar = model.with_name('deployment.json')
    if not sidecar.is_file() or sidecar.stat().st_size > 262144:
        raise ValueError('Missing or oversized deployment.json.')
    deployment = json.loads(sidecar.read_text(encoding='utf-8'))
    with model.open('rb') as stream:
        digest = hashlib.file_digest(stream, 'sha256').hexdigest()
    if digest != request['expected_sha256'] or digest != deployment.get('onnx_sha256'):
        raise ValueError('ONNX SHA256 does not match the request/deployment.')
    if deployment.get('task') != 'detect' or deployment.get('schema_version') != 1:
        raise ValueError('Only schema 1 detection deployments are supported.')
    if str(root) not in sys.path: sys.path.insert(0, str(root))
    # Imports are deferred until paths/digests are checked. stdout is protocol-only.
    from core.inference.backend import DetectorBackend
    from core.inference.config import DetectConfig
    from core.inference.service import detect_raster
    from core.annotations import tile_grid
    from PIL import Image
    from core.inference import backend as backend_module
    names = {int(k): str(v) for k, v in deployment['names'].items()}
    selected = request.get('class_ids', [])
    if not isinstance(selected, list) or len(selected) > 1000 or any(type(x) is not int or x not in names for x in selected):
        raise ValueError('Requested class IDs are absent from deployment.json.')
    config = DetectConfig.from_dict(dict(
        model=str(model), backend='onnx', device='cpu', imgsz=request['image_size'], batch=request['batch'],
        conf=request['confidence'], iou=request['iou'], merge_iou=request['merge_iou'],
        tiled=request['tiled'], tile_width=request['tile_width'], tile_height=request['tile_height'],
        overlap=request['overlap'], max_det=request['max_det'],
        preprocess=deployment['preprocessing']['config'], preprocess_source='external deployment.json'))
    with Image.open(image) as source:
        width, height = source.size
    if width * height > 100_000_000:
        raise ValueError('Input snapshot exceeds the 100 MP budget; use an ROI.')
    count = len(tile_grid(width, height, config.tile_width, config.tile_height, config.overlap)) if config.tiled else 1
    if count > 4096 or count * config.max_det > 1_000_000:
        raise ValueError('Tile count/candidate count exceeds the 4096-tile / 1000000-candidate per-run budget.')
    timings['validate_import_ms'] = (time.perf_counter()-started)*1000
    # Observe actual existing backend stages; wrappers never alter tensors/results.
    for function, key in [('letterbox_batch','preprocess_ms'),('decode_output','postprocess_ms')]:
        original = getattr(backend_module, function)
        def measured(*args, _f=original, _key=key, **kwargs):
            t = time.perf_counter()
            try: return _f(*args, **kwargs)
            finally: timings[_key] = timings.get(_key, 0.) + (time.perf_counter()-t)*1000
        setattr(backend_module, function, measured)
    from core.inference import service as service_module
    original_raster = service_module.RasterSource
    class TimedRaster(original_raster):
        def __init__(self, *args, **kwargs):
            t=time.perf_counter()
            try: super().__init__(*args, **kwargs)
            finally: timings['image_decode_ms']=timings.get('image_decode_ms',0.)+(time.perf_counter()-t)*1000
        def __enter__(self):
            t=time.perf_counter()
            try: return super().__enter__()
            finally: timings['image_decode_ms']=timings.get('image_decode_ms',0.)+(time.perf_counter()-t)*1000
    service_module.RasterSource=TimedRaster
    backend = None
    try:
        loading = time.perf_counter()
        backend = cache.get(request, config, DetectorBackend) if cache is not None else DetectorBackend(config, owner='visionplatform-external-bridge')
        timings['model_lookup_load_ms'] = (time.perf_counter()-loading)*1000
        decode_started = time.perf_counter()
        result, _preview = detect_raster(image, backend, config, threading.Event())
        timings['image_pipeline_ms'] = (time.perf_counter()-decode_started)*1000
    finally:
        if cache is None and backend is not None: backend.close()
        service_module.RasterSource=original_raster
        # Do not accumulate wrapper layers across resident requests.
        for function in ('letterbox_batch','decode_output'):
            wrapper = getattr(backend_module, function)
            setattr(backend_module, function, wrapper.__kwdefaults__['_f'])
    timings['forward_ms'] = result['inference_ms']
    timings['python_total_ms'] = (time.perf_counter()-started)*1000
    boxes = [b for b in result['boxes'] if not selected or b['class_id'] in selected]
    if len(boxes) > 10000:
        raise ValueError('Detection count exceeds the 10000-result budget; refine ROI/threshold.')
    for b in boxes:
        if not all(math.isfinite(float(b[k])) for k in ('confidence', 'x', 'y', 'w', 'h')):
            raise ValueError('Non-finite external detection.')
    return dict(schema_version=1, success=True, request_id=request.get('request_id'),
                image_frame_id=request.get('image_frame_id'), run_id=request.get('run_id'), timings=timings, model_sha256=digest,
                width=result['width'], height=result['height'], provider=result['provider'],
                tile_count=result['tile_count'], inference_ms=result['inference_ms'], boxes=boxes)


def main():
    try:
        if len(sys.argv) != 2:
            raise ValueError('Usage: python -B yolo_bridge.py request.json')
        path = Path(sys.argv[1]).resolve(strict=True)
        if path.stat().st_size > MAX_REQUEST_BYTES:
            raise ValueError('Request exceeds 64 KiB.')
        request = json.loads(path.read_text(encoding='utf-8-sig'))
        with contextlib.redirect_stdout(sys.stderr):
            result = run(request)
        text = json.dumps(result, ensure_ascii=False, allow_nan=False, separators=(',', ':'))
        if len(text.encode('utf-8')) > MAX_OUTPUT_BYTES:
            raise ValueError('Response exceeds 8 MiB.')
        print(text, flush=True)
        return 0
    except Exception as exc:
        # No traceback/log dump or source image data crosses the protocol boundary.
        print(json.dumps(dict(schema_version=1, success=False,
                              error=(type(exc).__name__ + ': ' + str(exc))[:2048]), ensure_ascii=False), flush=True)
        return 1


if __name__ == '__main__':
    sys.exit(main())

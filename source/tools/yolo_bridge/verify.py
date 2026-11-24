"""Explicit, optional integration verification against the owner's existing external model.

Run with YOLOStudio's existing Python; missing dependencies/model fail with nonzero exit.
Nothing is installed, downloaded, or written into YOLOStudio.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import statistics
import subprocess
import sys
import tempfile
import time


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--root', type=Path, required=True)
    parser.add_argument('--model', type=Path, required=True)
    parser.add_argument('--image', type=Path, required=True)
    parser.add_argument('--image-size', type=int, default=128)
    parser.add_argument('--report', type=Path, required=True)
    parser.add_argument('--expect-count', type=int, default=2)
    args = parser.parse_args()
    from PIL import Image
    root, model, image = (p.resolve(strict=True) for p in (args.root, args.model, args.image))
    digest = hashlib.sha256(model.read_bytes()).hexdigest()
    report = dict(python=sys.executable, model=str(model), model_sha256=digest, image=str(image),
                  image_sha256=hashlib.sha256(image.read_bytes()).hexdigest(), runs=[], tests={})
    with tempfile.TemporaryDirectory(prefix='VisionPlatform-YoloVerify-') as directory:
        work = Path(directory)
        with Image.open(image) as source:
            source.convert('RGB').save(work / 'input.png')
        request = dict(schema_version=1, yolo_root=str(root), model_path=str(model), image_path=str(work / 'input.png'),
                       expected_sha256=digest, image_size=args.image_size, confidence=.25, iou=.45, merge_iou=.5,
                       class_ids=[], tiled=False, tile_width=320, tile_height=320, overlap=.15, batch=1, max_det=300)
        environment = dict(os.environ, PYTHONDONTWRITEBYTECODE='1', PYTHONIOENCODING='utf-8', YOLO_CONFIG_DIR=directory)

        def call(values, expect_success=True, timeout=60):
            path = work / 'request.json'; path.write_text(json.dumps(values), encoding='utf-8')
            started = time.perf_counter()
            completed = subprocess.run([sys.executable, '-B', '-u', str(Path(__file__).with_name('yolo_bridge.py')), str(path)],
                                       cwd=work, env=environment, capture_output=True, text=True, encoding='utf-8', timeout=timeout)
            result = json.loads(completed.stdout)
            assert result['success'] is expect_success, result
            assert (completed.returncode == 0) is expect_success, completed.stderr[-2048:]
            result['bridge_total_ms'] = (time.perf_counter() - started) * 1000
            return result

        warm = call(request)
        assert len(warm['boxes']) == args.expect_count, warm
        report['warmup'] = warm
        for _ in range(3):
            result = call(request)
            assert result['boxes'] == warm['boxes'], 'Same model/input returned different detections.'
            report['runs'].append(result)
        report['median_bridge_total_ms'] = statistics.median(r['bridge_total_ms'] for r in report['runs'])
        report['median_forward_ms'] = statistics.median(r['inference_ms'] for r in report['runs'])
        report['tests']['same_input_three_repeats'] = True
        tiled = call(dict(request, tiled=True)); report['tiled'] = tiled
        assert tiled['tile_count'] > 1
        assert all(0 <= b['x'] < b['x'] + b['w'] <= tiled['width'] + 1e-4 and
                   0 <= b['y'] < b['y'] + b['h'] <= tiled['height'] + 1e-4 for b in tiled['boxes'])
        report['tests']['original_tile_pipeline_bounds'] = True
        filtered = call(dict(request, class_ids=[0])); report['filtered'] = filtered
        assert filtered['boxes'] and all(b['class_id'] == 0 for b in filtered['boxes'])
        report['tests']['explicit_class_filter'] = True
        missing = call(dict(request, model_path=str(work / 'missing.onnx')), False)
        report['tests']['missing_model_fails'] = bool(missing.get('error'))
        changed = call(dict(request, expected_sha256='0' * 64), False)
        report['tests']['sha_mismatch_fails'] = 'SHA256' in changed['error']
        invalid = call(dict(request, image_size=33), False)
        report['tests']['invalid_size_fails'] = bool(invalid.get('error'))
    report['all_passed'] = all(report['tests'].values())
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps(dict(all_passed=report['all_passed'], report=str(args.report.resolve()),
                          count=len(warm['boxes']), tile_count=tiled['tile_count'],
                          median_bridge_total_ms=report['median_bridge_total_ms'], median_forward_ms=report['median_forward_ms'])))
    return 0 if report['all_passed'] else 1


if __name__ == '__main__':
    sys.exit(main())

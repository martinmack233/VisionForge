"""Compare real existing ONNX inference through the service and original core.

No model is generated/downloaded; all writable state is a dedicated new test home.
"""
import argparse
import importlib.util
import json
from pathlib import Path
import shutil
import sys
import tempfile
import threading
import time


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--yolo-root', type=Path, required=True)
    parser.add_argument('--model', type=Path, required=True)
    parser.add_argument('--image', type=Path, required=True)
    parser.add_argument('--report', type=Path, required=True)
    args = parser.parse_args()
    spec = importlib.util.spec_from_file_location('visionstudio_real_inference', Path(__file__).with_name('service.py'))
    module = importlib.util.module_from_spec(spec); sys.modules[spec.name] = module; spec.loader.exec_module(module)
    work = Path(tempfile.mkdtemp(prefix='v5ai-onnx-'))
    model_folder = work / 'model'; model_folder.mkdir()
    model = model_folder / args.model.name
    shutil.copyfile(args.model, model)
    shutil.copyfile(args.model.with_name('deployment.json'), model_folder / 'deployment.json')
    source = work / args.image.name; shutil.copyfile(args.image, source)
    report = dict(verified=False, source='existing real ONNX and existing image; no replacement model',
                  data_home=str(work / 'data'), model=str(model), image=str(source),
                  original_model=str(args.model.resolve()), original_image=str(args.image.resolve()),
                  downloaded=False, source_data_modified=False, cases=[])
    service = module.AiWorkspaceService(args.yolo_root, work / 'data', work / 'transfer')
    try:
        project = service.dispatch('projects.create', {'name': '独立ONNX服务对照'})['project']
        service.dispatch('context.select', {'project_id': project['id']})
        inspection = service.dispatch('deployment.inspect', {'model_path': str(model)})
        report['inspection'] = inspection
        report['model_sha256'] = module.digest(model)
        report['image_sha256'] = module.digest(source)
        from core.inference.backend import DetectorBackend
        from core.inference.config import DetectConfig
        from core.inference.service import detect_raster
        def wait_for(task, expect='completed'):
            deadline = time.monotonic() + 60
            while time.monotonic() < deadline:
                row = service.tasks[task['task_id']]
                if row['status'] in module.TERMINAL:
                    if row['status'] != expect:
                        raise AssertionError(row)
                    return row
                time.sleep(.03)
            raise TimeoutError('Inference deadline exceeded')
        for tiled in (False, True):
            config = dict(model=str(model), backend='onnx', device='cpu', imgsz=inspection['input_size'],
                          batch=1, conf=.25, iou=.45, tiled=tiled, tile_width=512,
                          tile_height=512, overlap=.2, merge_iou=.5, max_det=300,
                          preprocess=inspection['preprocessing']['config'], preprocess_source='same deployment')
            with DetectorBackend(DetectConfig.from_dict(config), 'visionstudio-original-core-comparison') as backend:
                original, _preview = detect_raster(source, backend, DetectConfig.from_dict(config), threading.Event())
            task = wait_for(service.dispatch('inference.start', {'model_path': str(model), 'inputs': [str(source)],
                'config': config, 'model_sha256': report['model_sha256'], 'image_sha256': report['image_sha256']}))
            integrated = service.dispatch('inference.results', {'task_id': task['task_id']})['items'][0]
            def boxes(result):
                return sorted([{k: b[k] for k in ('class_id', 'name', 'confidence', 'x', 'y', 'w', 'h')}
                               for b in result['boxes']], key=lambda b: (b['class_id'], b['x'], b['y']))
            if boxes(original) != boxes(integrated):
                raise AssertionError('Original core and integrated service detections differ')
            if integrated['source_kind'] != 'file' or integrated['source_sha256'] != report['image_sha256'] or integrated['model_sha256'] != report['model_sha256']:
                raise AssertionError('Inference identity was lost')
            if not all(b['x'] >= 0 and b['y'] >= 0 and b['x'] + b['w'] <= integrated['width'] + 1e-5 and
                       b['y'] + b['h'] <= integrated['height'] + 1e-5 for b in integrated['boxes']):
                raise AssertionError('A restored detection lies outside the original image')
            report['cases'].append(dict(tiled=tiled, exact_boxes_equal=True, count=len(integrated['boxes']),
                tile_count=integrated['tile_count'], frame_id=integrated['frame_id'], task_id=integrated['task_id'],
                elapsed_ms=integrated['elapsed_ms'], inference_ms=integrated['inference_ms'], boxes=boxes(integrated)))
        bad = wait_for(service.dispatch('inference.start', {'model_path': str(model), 'inputs': [str(source)],
            'config': config, 'image_sha256': '0' * 64}), 'failed')
        report['wrong_image_identity_rejected'] = 'SHA256' in bad['error']
        report['verified'] = all(c['exact_boxes_equal'] for c in report['cases']) and report['wrong_image_identity_rejected']
    except Exception as exc:
        report['error'] = repr(exc)
    finally:
        report['shutdown'] = service.shutdown(False)
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2, default=module.safe_json), encoding='utf-8')
    print(json.dumps(dict(verified=report['verified'], cases=[dict(tiled=c['tiled'], count=c['count']) for c in report['cases']],
                         error=report.get('error'), report=str(args.report.resolve())), ensure_ascii=False))
    return 0 if report['verified'] else 1


if __name__ == '__main__':
    raise SystemExit(main())

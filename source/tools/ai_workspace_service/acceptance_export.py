"""Explicit original ONNX export from this task's own small real training model."""
import argparse
import importlib.util
import json
from pathlib import Path
import sys
import time


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--training-report', type=Path, required=True)
    parser.add_argument('--report', type=Path, required=True)
    args = parser.parse_args()
    previous = json.loads(args.training_report.read_text(encoding='utf-8'))
    home = Path(previous['data_home']).resolve(strict=True)
    if not home.parent.name.startswith('v5ai-'):
        raise ValueError('Only the newly created acceptance training workspace is permitted.')
    spec = importlib.util.spec_from_file_location('visionstudio_export_acceptance', Path(__file__).with_name('service.py'))
    module = importlib.util.module_from_spec(spec); sys.modules[spec.name] = module; spec.loader.exec_module(module)
    service = module.AiWorkspaceService(previous['code_root'], home, home.parent / 'transfer')
    report = dict(verified=False, data_home=str(home), downloaded=False, original_model_modified=False)
    try:
        record = previous['resume_record']
        service.dispatch('context.select', {'project_id': record['project_id']})
        model = record['best_pt']
        before_hash = module.digest(model)
        task = service.dispatch('models.export', {'model_path': model,
            'options': dict(imgsz=64, batch=1, precision='FP32', opset=17, dynamic=False)})
        deadline = time.monotonic() + 90
        while time.monotonic() < deadline:
            row = service.tasks[task['task_id']]
            if row['status'] in module.TERMINAL:
                break
            time.sleep(.05)
        else:
            service.dispatch('tasks.cancel', {'task_id': task['task_id']})
            raise TimeoutError('Original ONNX export exceeded its bounded deadline; cancellation requested.')
        report['task'] = row
        if row['status'] != 'completed':
            raise RuntimeError(row.get('error'))
        onnx = row['result']['summary']['onnx']
        inspected = service.dispatch('deployment.inspect', {'model_path': onnx, 'image_size': 64})
        models = service.dispatch('models.list', {'project_id': record['project_id'], 'include_hashes': True})
        matching = next(run for run in models['export_runs'] if run['id'] == row['result']['id'])
        if matching['model_sha256'] != inspected['model_sha256'] or module.digest(model) != before_hash:
            raise AssertionError('Original PT changed or exported model hash mismatch')
        report.update(verified=True, inspection=inspected, export_model_sha256=matching['model_sha256'],
                      source_pt=model, source_pt_sha256=before_hash)
    except Exception as exc:
        report['error'] = repr(exc)
    finally:
        report['shutdown'] = service.shutdown(False)
        args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2, default=module.safe_json), encoding='utf-8')
    print(json.dumps(dict(verified=report['verified'], error=report.get('error'), report=str(args.report.resolve())), ensure_ascii=False))
    return 0 if report['verified'] else 1


if __name__ == '__main__':
    raise SystemExit(main())

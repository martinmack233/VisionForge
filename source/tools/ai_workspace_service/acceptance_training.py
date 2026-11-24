"""Explicit bounded real-GPU workflow validation using existing local weights.

All new data, labels, checkpoints and logs belong to a new temporary data home.
No dependency installation, downloads or changes to existing tasks are performed.
"""
from __future__ import annotations

import argparse
import importlib.util
import json
import os
from pathlib import Path
import sys
import tempfile
import time
import uuid


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--yolo-root', type=Path, required=True)
    parser.add_argument('--report', type=Path, required=True)
    args = parser.parse_args()
    root = args.yolo_root.resolve(strict=True)
    sys.path.insert(0, str(root))
    module_path = Path(__file__).with_name('service.py')
    spec = importlib.util.spec_from_file_location('visionstudio_acceptance_service', module_path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    work = Path(tempfile.mkdtemp(prefix='v5ai-'))
    report = dict(verified=False, dataset='four synthetic 64x64 images, four original class names',
                  data_home=str(work / 'data'), code_root=str(root), python=sys.executable,
                  downloaded=False, installed=False, modified_original_data=False, events=[])
    service = None
    try:
        import numpy as np
        from PIL import Image
        import torch
        from core.inference.backend import cuda_lease
        report['cuda'] = dict(available=torch.cuda.is_available(), version=torch.version.cuda,
                              name=torch.cuda.get_device_name(0) if torch.cuda.is_available() else None)
        if not torch.cuda.is_available():
            raise RuntimeError('CUDA is unavailable; no CPU training substitution is allowed.')
        # Existing OS GPU lease is used even for this preflight; never inspect/kill other owners.
        with cuda_lease('0', 'visionstudio-phase5-training-preflight'):
            pass
        service = module.AiWorkspaceService(root, work / 'data', work / 'transfer')
        project = service.dispatch('projects.create', {'name': '第五阶段独立GPU冒烟'})['project']
        service.dispatch('context.select', {'project_id': project['id']})
        incoming = work / 'sources'; incoming.mkdir()
        for index in range(4):
            pixels = np.full((64, 64, 3), 20 + index * 13, dtype=np.uint8)
            pixels[16:40, 20:44, :] = [220, 150 + index, 80]
            Image.fromarray(pixels).save(incoming / f'synthetic-{index}.png')

        def completed(task):
            deadline = time.monotonic() + 30
            while time.monotonic() < deadline:
                row = service.tasks[task['task_id']]
                if row['status'] in module.TERMINAL:
                    if row['status'] != 'completed':
                        raise RuntimeError(str(row))
                    return row['result']
                time.sleep(.02)
            raise TimeoutError('Data preparation task did not complete in 30 seconds.')

        completed(service.dispatch('images.import', {'paths': [str(incoming)]}))
        for image in service.dispatch('images.list')['items']:
            service.dispatch('annotations.save', {'image_id': image['id'], 'reviewed': True,
                'boxes': [dict(id=uuid.uuid4().hex, class_id=0, x=20, y=16, w=24, h=24)]})
        dataset = completed(service.dispatch('dataset.build', {'options': dict(tiled=False, grouping='image',
            ratios=[.5, .25, .25], seed=29)}))
        dataset_path = str(Path(dataset['path']) / 'data.yaml')
        weights = root / 'models/pretrained/yolo26n.pt'
        if not weights.is_file():
            raise FileNotFoundError('Existing local yolo26n.pt is absent; nothing will be downloaded.')
        config = dict(dataset=dataset_path, model=str(weights), epochs=2, batch=1, imgsz=64,
                      device='0', optimizer='SGD', workers=0, amp=False, allow_download=False)
        report['config'] = config
        started = service.dispatch('training.start', {'config': config})
        report['first_task_id'] = started['task_id']
        first = started['record']
        checkpoint = Path(first['output_dir']) / 'artifacts/weights/last_valid.pt'
        deadline = time.monotonic() + 100
        stop_sent = False
        while time.monotonic() < deadline:
            polled = service.dispatch('training.poll', {'task_id': started['task_id'], 'log_offset': 0})
            record = polled['selected']
            if checkpoint.is_file() and not stop_sent:
                service.dispatch('training.stop', {'task_id': started['task_id']})
                report['events'].append(dict(action='cooperative_stop', time=time.time(), checkpoint=str(checkpoint)))
                stop_sent = True
            if record['status'] in ('completed', 'stopped', 'failed'):
                break
            time.sleep(.025)
        else:
            service.dispatch('training.stop', {'task_id': started['task_id']})
            raise TimeoutError('Bounded two-epoch training exceeded 100 seconds; stop was requested.')
        report['first_record'] = record
        report['first_metrics'] = polled.get('metrics', [])
        report['first_log_excerpt'] = polled.get('log_text', '')[-16000:]
        if record['status'] == 'failed':
            raise RuntimeError(record.get('error'))
        if record['status'] == 'completed':
            report['resume_verified'] = False
            report['resume_boundary'] = 'Two epochs finished before the cooperative stop took effect; completed checkpoints cannot be claimed resumable.'
        else:
            # Preserve the original total of two epochs; resume never starts an extra long task.
            from core.training.processes import verified_process
            release_deadline = time.monotonic() + 12
            while verified_process(service.training.repository.get(started['task_id'])):
                if time.monotonic() >= release_deadline:
                    raise TimeoutError('The stopped worker has not finished GPU cleanup; no competing resume is launched.')
                time.sleep(.1)
            resumed = service.dispatch('training.resume', {'task_id': started['task_id']})
            report['resume_task_id'] = resumed['task_id']
            deadline = time.monotonic() + 100
            while time.monotonic() < deadline:
                resumed_poll = service.dispatch('training.poll', {'task_id': resumed['task_id']})
                resumed_record = resumed_poll['selected']
                if resumed_record['status'] in ('completed', 'stopped', 'failed'):
                    break
                time.sleep(.1)
            else:
                service.dispatch('training.stop', {'task_id': resumed['task_id']})
                raise TimeoutError('Resume exceeded its bounded deadline; cooperative stop was requested.')
            report['resume_record'] = resumed_record
            report['resume_metrics'] = resumed_poll.get('metrics', [])
            report['resume_log_excerpt'] = resumed_poll.get('log_text', '')[-16000:]
            report['resume_verified'] = resumed_record['status'] == 'completed' and bool(report['resume_metrics'])
        report['models'] = service.dispatch('models.list')['items']
        report['verified'] = bool(report['first_metrics']) and record['status'] in ('completed', 'stopped')
    except Exception as exc:
        report['error'] = repr(exc)
    finally:
        if service is not None:
            try:
                report['shutdown'] = service.shutdown(True)
            except Exception as exc:
                report['shutdown_error'] = repr(exc)
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2, default=module.safe_json), encoding='utf-8')
    print(json.dumps(dict(verified=report['verified'], resume_verified=report.get('resume_verified'),
                         error=report.get('error'), report=str(args.report.resolve())), ensure_ascii=False))
    return 0 if report['verified'] else 1


if __name__ == '__main__':
    raise SystemExit(main())

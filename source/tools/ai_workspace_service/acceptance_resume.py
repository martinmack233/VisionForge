"""Resume only the bounded, already-stopped acceptance task from a saved report."""
import argparse
import importlib.util
import json
from pathlib import Path
import sys
import time


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--previous-report', type=Path, required=True)
    parser.add_argument('--report', type=Path, required=True)
    args = parser.parse_args()
    prior = json.loads(args.previous_report.read_text(encoding='utf-8'))
    report = dict(prior, verified=False, previous_report=str(args.previous_report.resolve()),
                  prior_orchestration_error=prior.get('error'))
    report.pop('error', None)
    model_file = Path(__file__).with_name('service.py')
    spec = importlib.util.spec_from_file_location('visionstudio_resume_service', model_file)
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    data_home = Path(prior['data_home']).resolve(strict=True)
    if not data_home.parent.name.startswith('v5ai-') or data_home.name != 'data':
        raise ValueError('Only this task\'s dedicated acceptance data home can be resumed.')
    service = module.AiWorkspaceService(prior['code_root'], data_home, data_home.parent / 'transfer')
    try:
        project_id = prior['first_record']['project_id']
        service.dispatch('context.select', {'project_id': project_id})
        from core.training.processes import verified_process
        deadline = time.monotonic() + 12
        while verified_process(service.training.repository.get(prior['first_task_id'])):
            if time.monotonic() >= deadline:
                raise TimeoutError('The original stopped worker is still cleaning up; no competing task started.')
            time.sleep(.1)
        resumed = service.dispatch('training.resume', {'task_id': prior['first_task_id']})
        report['resume_task_id'] = resumed['task_id']
        deadline = time.monotonic() + 100
        while time.monotonic() < deadline:
            result = service.dispatch('training.poll', {'task_id': resumed['task_id']})
            record = result['selected']
            if record['status'] in ('completed', 'stopped', 'failed'):
                break
            time.sleep(.1)
        else:
            service.dispatch('training.stop', {'task_id': resumed['task_id']})
            raise TimeoutError('Resume exceeded the bounded deadline; cooperative stop requested.')
        report.update(resume_record=record, resume_metrics=result.get('metrics', []),
                      resume_log_excerpt=result.get('log_text', '')[-16000:])
        report['resume_verified'] = record['status'] == 'completed' and bool(result.get('metrics'))
        report['verified'] = bool(prior.get('first_metrics')) and report['resume_verified']
        report['models'] = service.dispatch('models.list')['items']
    except Exception as exc:
        report['error'] = repr(exc)
    finally:
        try:
            report['shutdown'] = service.shutdown(True)
        except Exception as exc:
            report['shutdown_error'] = repr(exc)
        args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2, default=module.safe_json), encoding='utf-8')
    print(json.dumps(dict(verified=report['verified'], resume_verified=report.get('resume_verified'),
                         error=report.get('error'), report=str(args.report.resolve())), ensure_ascii=False))
    return 0 if report['verified'] else 1


if __name__ == '__main__':
    raise SystemExit(main())

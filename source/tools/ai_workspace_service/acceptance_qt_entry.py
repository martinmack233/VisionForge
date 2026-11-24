"""Verify the unchanged independent Qt entry point against this task's fixture."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--training-report', type=Path, required=True)
    parser.add_argument('--report', type=Path, required=True)
    args = parser.parse_args()
    previous = json.loads(args.training_report.read_text(encoding='utf-8'))
    root = Path(previous['code_root']).resolve(strict=True)
    home = Path(previous['data_home']).resolve(strict=True)
    if not home.parent.name.startswith('v5ai-'):
        raise ValueError('Only the dedicated new acceptance fixture is permitted.')
    sys.path.insert(0, str(root))
    from config.paths import AppPaths
    from database.store import Store
    from database.training_store import TrainingStore
    from core.workspace_lock import WorkspaceLease

    def snapshot():
        with WorkspaceLease(home, 'compatibility-fixture-snapshot'):
            store = Store(AppPaths(home))
            projects = store.list_projects()
            return dict(projects=[store.snapshot(p['id']) for p in projects],
                        training=TrainingStore(store).list(), models=TrainingStore(store).models())
    before = snapshot()
    shot = args.report.with_suffix('.png').resolve()
    environment = dict(os.environ, YOLO_STUDIO_HOME=str(home), YOLO_AUTOINSTALL='false',
                       YOLO_CONFIG_DIR=str(home / 'config/ultralytics'),
                       MPLCONFIGDIR=str(home / 'config/matplotlib'), PYTHONDONTWRITEBYTECODE='1',
                       PYTHONIOENCODING='utf-8', QT_QPA_PLATFORM='offscreen')
    process = subprocess.run([sys.executable, '-B', str(root / 'main.py'), '--quit-after', '3',
                              '--screenshot', str(shot)], env=environment, cwd=root,
                             capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=35)
    after = snapshot()
    # UI reconcile may update timestamps/metrics; identities, annotation content and model records must remain.
    def identity(value):
        return dict(projects=value['projects'],
                    training=[{k: r[k] for k in ('id', 'project_id', 'status', 'best_pt', 'last_pt')} for r in value['training']],
                    models=[{k: r[k] for k in ('id', 'project_id', 'run_id', 'path')} for r in value['models']])
    report = dict(verified=process.returncode == 0 and shot.is_file() and identity(before) == identity(after),
                  independent_qt_entry=True, render_mode='Qt offscreen real widget rendering, not native desktop interaction',
                  data_home=str(home), existing_user_data_modified=False, process_exit=process.returncode,
                  screenshot=str(shot), project_count=len(after['projects']),
                  image_count=sum(len(p['images']) for p in after['projects']),
                  annotation_count=sum(len(i['annotations']) for p in after['projects'] for i in p['images']),
                  training_count=len(after['training']), model_count=len(after['models']),
                  identities_and_annotations_preserved=identity(before) == identity(after),
                  stdout=process.stdout[-8000:], stderr=process.stderr[-12000:])
    args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps({k: report[k] for k in ('verified', 'process_exit', 'project_count', 'image_count', 'training_count', 'model_count')}))
    return 0 if report['verified'] else 1


if __name__ == '__main__':
    raise SystemExit(main())

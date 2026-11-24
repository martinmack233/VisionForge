"""Version 1 JSONL service. Business operations are delegated to YOLOStudio.

Code, Python environment, data home and temporary display files are independent.
No Qt application, package installation, model download or implicit training runs.
"""
from __future__ import annotations

import argparse
from concurrent.futures import ThreadPoolExecutor, wait
from dataclasses import asdict, is_dataclass
import hashlib
import importlib.metadata
import json
import logging
import math
import os
from pathlib import Path
import sqlite3
import sys
import threading
import time
import uuid

VERSION = 1
MAX_REQUEST = 2 * 1024 * 1024
MAX_RESPONSE = 8 * 1024 * 1024
TERMINAL = {'completed', 'cancelled', 'stopped', 'failed', 'interrupted'}


def digest(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def safe_json(value):
    if is_dataclass(value):
        return asdict(value)
    if isinstance(value, Path):
        return str(value)
    if hasattr(value, 'item'):
        return value.item()
    raise TypeError(type(value).__name__)


def atomic_json(path, value):
    path = Path(path)
    if os.name == 'nt' and len(str(path.resolve())) > 200:
        path = Path(extended_path(path))
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + '.' + uuid.uuid4().hex + '.tmp')
    try:
        temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2,
                                       default=safe_json, allow_nan=False), encoding='utf-8')
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def extended_path(path):
    """Pillow's Windows file writer needs an extended path for deep dataset outputs."""
    value = str(Path(path).resolve())
    if os.name != 'nt' or value.startswith('\\\\?\\'):
        return value
    return '\\\\?\\UNC\\' + value[2:] if value.startswith('\\\\') else '\\\\?\\' + value


class ServiceError(RuntimeError):
    def __init__(self, message, code='invalid_request'):
        super().__init__(message)
        self.code = code


class AiWorkspaceService:
    def __init__(self, yolo_root, data_home, transfer_root, emit=None):
        if not all(Path(path).is_absolute() for path in (yolo_root, data_home, transfer_root)):
            raise ServiceError('代码根、数据根和传输目录必须使用明确的绝对路径，不依赖工作目录。', 'absolute_path_required')
        self.yolo_root = Path(yolo_root).resolve(strict=True)
        for name in ('config/paths.py', 'database/store.py', 'core/training/service.py',
                     'core/inference/backend.py', 'core/workspace_lock.py'):
            if not (self.yolo_root / name).is_file():
                raise ServiceError('YOLOStudio 业务模块缺失：' + name, 'environment_missing')
        sys.path.insert(0, str(self.yolo_root))
        service_directory = str(Path(__file__).resolve().parent)
        if service_directory not in sys.path:
            sys.path.insert(1, service_directory)
        self.data_home = Path(data_home).resolve()
        self.transfer_root = Path(transfer_root).resolve()
        # A dedicated display directory is required. It is never a project/model root.
        if self.transfer_root == self.data_home or self.transfer_root == self.yolo_root:
            raise ServiceError('图像传输目录必须独立于代码及数据根目录。')
        os.environ['YOLO_STUDIO_HOME'] = str(self.data_home)
        os.environ['YOLO_AUTOINSTALL'] = 'false'
        os.environ['YOLO_CONFIG_DIR'] = str(self.data_home / 'config/ultralytics')
        os.environ['MPLCONFIGDIR'] = str(self.data_home / 'config/matplotlib')
        from config.paths import AppPaths
        from core.workspace_lock import WorkspaceLease
        self.lease = WorkspaceLease(self.data_home, 'VisionStudio AI service').acquire()
        try:
            database = self.data_home / 'database/studio.sqlite3'
            if database.exists():
                with sqlite3.connect(database.as_uri() + '?mode=ro', uri=True) as existing:
                    version = existing.execute('PRAGMA user_version').fetchone()[0]
                if version != 4:
                    raise ServiceError(f'现有数据库版本为 {version}，需要原 YOLOStudio 明确备份升级；集成服务不会自动迁移。', 'migration_required')
            from database.store import Store
            from core.training.service import TrainingService
            from core.inference.service import VisionService
            self.store = Store(AppPaths(self.data_home))
            self.training = TrainingService(self.store, sys.executable)
            self.vision = VisionService(self.store)
            self.transfer_root.mkdir(parents=True, exist_ok=True)
            self.owned_previews = set()
            self.identity_file = self.data_home / 'config/visionstudio-workspace.json'
            identity = self._read_json(self.identity_file, {})
            if not isinstance(identity.get('workspace_id'), str):
                identity = dict(version=1, workspace_id=uuid.uuid4().hex)
                atomic_json(self.identity_file, identity)
            self.workspace_id = identity['workspace_id']
            self.emit = emit or (lambda _value: None)
            self.tasks_file = self.data_home / 'config/visionstudio-tasks.json'
            self.tasks = self._read_json(self.tasks_file, {})
            if not isinstance(self.tasks, dict):
                self.tasks = {}
            for task in self.tasks.values():
                if task.get('status') not in TERMINAL:
                    task.update(status='interrupted', error='上次服务进程已退出；保留已完成文件及业务历史，可重新启动。')
            self.cancels = {}
            self.futures = {}
            self.pool = ThreadPoolExecutor(max_workers=2, thread_name_prefix='AI-business')
            self.lock = threading.RLock()
            self.current_project = None
            self.closing = False
        except BaseException:
            self.lease.release()
            raise

    @staticmethod
    def _read_json(path, default):
        try:
            return json.loads(Path(path).read_text(encoding='utf-8-sig'))
        except FileNotFoundError:
            return default

    def project_id(self, payload):
        project_id = payload.get('project_id') or self.current_project
        if not isinstance(project_id, str) or not self.store.get_project(project_id):
            raise ServiceError('请选择存在的 AI 项目。', 'project_missing')
        return project_id

    def image(self, payload):
        image = self.store.get_image(payload.get('image_id'))
        if not image or image['project_id'] != self.project_id(payload):
            raise ServiceError('图片不存在或不属于所选 AI 项目。', 'image_missing')
        return image

    def _save_tasks(self):
        with self.lock:
            # Bounded control history; authoritative original business histories remain in their repositories.
            rows = list(self.tasks.values())[-300:]
            atomic_json(self.tasks_file, {r['task_id']: r for r in rows})

    def _event(self, event, task_id, data):
        self.emit(dict(version=VERSION, event=event, taskId=task_id, data=data))

    def start_task(self, kind, project_id, work):
        with self.lock:
            if self.closing:
                raise ServiceError('服务正在关闭。', 'closing')
            if any(t['status'] not in TERMINAL and t['project_id'] == project_id for t in self.tasks.values()):
                raise ServiceError('此项目已有导入/切片/检测等任务，请先完成或取消，避免同时修改同一份数据。', 'project_busy')
            task_id = uuid.uuid4().hex
            record = dict(task_id=task_id, kind=kind, project_id=project_id, status='waiting',
                          progress=dict(current=0, total=0, message='等待执行'), result=None,
                          error=None, created_at=time.time())
            self.tasks[task_id] = record
            cancel = self.cancels[task_id] = threading.Event()
            self._save_tasks()

        def progress(current=0, total=0, message=''):
            with self.lock:
                record['progress'] = dict(current=int(current), total=int(total), message=str(message)[:4096])
            self._event('progress', task_id, dict(record['progress']))
        progress.task_id = task_id

        def execute():
            record['status'] = 'running'
            self._event('task', task_id, dict(record))
            try:
                value = work(cancel, progress)
                record['result'] = asdict(value) if is_dataclass(value) else value
                state = value.get('status') if isinstance(value, dict) else None
                record['status'] = state if state in TERMINAL else 'cancelled' if cancel.is_set() else 'completed'
                if record['status'] == 'failed' and isinstance(value, dict):
                    record['error'] = value.get('error') or '原业务服务返回失败，详细记录保留在任务输出目录。'
            except InterruptedError as exc:
                record.update(status='cancelled', error=str(exc))
            except Exception as exc:
                logging.exception('AI task failed: %s', task_id)
                record.update(status='failed', error=str(exc))
            finally:
                record['ended_at'] = time.time()
                self._save_tasks()
                self._event('task', task_id, dict(record))
        self.futures[task_id] = self.pool.submit(execute)
        return dict(task_id=task_id, status=record['status'])

    def _guard_edit(self, project_id):
        if any(t['project_id'] == project_id and t['status'] not in TERMINAL for t in self.tasks.values()):
            raise ServiceError('任务正在使用此项目；完成或取消后再修改数据。', 'project_busy')

    def _annotations(self, image):
        boxes = self.store.get_annotations(image['id'])
        reviewed = bool(self.store.get_image(image['id'])['reviewed'])
        revision = hashlib.sha256(json.dumps([reviewed, boxes], sort_keys=True, ensure_ascii=False).encode('utf-8')).hexdigest()
        return dict(image_id=image['id'], reviewed=reviewed, boxes=boxes, revision=revision)

    def recovery_folder(self, project_id):
        project = self.store.get_project(project_id)
        folder = self.store.paths.absolute(project['data_dir']) / 'annotation_recovery'
        if not folder.resolve().is_relative_to(self.data_home):
            raise ServiceError('标注恢复目录超出AI数据根，已拒绝写入。', 'path_protected')
        return folder

    def recover_annotations(self, project_id):
        """Same recovery journal used by the original Qt Phase2Controller."""
        folder = self.recovery_folder(project_id)
        recovered = 0
        if folder.exists():
            for path in folder.glob('*.json'):
                if path.is_symlink():
                    raise ServiceError('恢复记录不能是符号链接。', 'path_protected')
                record = self._read_json(path, {})
                image = self.store.get_image(record.get('image_id'))
                if image and image['project_id'] == project_id:
                    self.store.replace_annotations(image['id'], record['boxes'], record['reviewed'])
                    path.unlink()
                    recovered += 1
        return recovered

    def save_annotations(self, image, boxes, reviewed):
        from core.annotations import validate_box
        classes = {c['class_id'] for c in self.store.list_classes(image['project_id'])}
        checked = [validate_box(b, image['width'], image['height'], classes) for b in boxes]
        if len({b['id'] for b in checked}) != len(checked):
            raise ServiceError('标注ID重复。')
        if not isinstance(reviewed, bool):
            raise ServiceError('已检查状态必须使用Boolean。')
        # Journal path is the same as the existing Qt recovery mechanism. Commit the
        # original Store transaction, then remove only this image's own journal.
        image_id = image['id']
        if not isinstance(image_id, str) or any(ch not in 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-' for ch in image_id):
            raise ServiceError('图片身份不能构造安全的恢复文件名。', 'image_identity_invalid')
        folder = self.recovery_folder(image['project_id'])
        journal = folder / (image_id + '.json')
        atomic_json(journal, dict(image_id=image_id, boxes=checked, reviewed=reviewed))
        self.store.replace_annotations(image_id, checked, reviewed)
        journal.unlink(missing_ok=True)

    def dispatch(self, method, payload=None):
        p = payload or {}
        if not isinstance(p, dict):
            raise ServiceError('payload 必须是对象。')
        if method == 'hello':
            return dict(version=VERSION, workspace_id=self.workspace_id, data_home=str(self.data_home),
                        yolo_root=str(self.yolo_root), python=sys.executable, pid=os.getpid(),
                        capabilities=['projects', 'annotations', 'dataset', 'slices', 'training', 'inference', 'evaluation', 'export'],
                        workspace_write_lease=True)
        if method == 'diagnostics':
            versions = {}
            for name in ('PySide6', 'torch', 'ultralytics', 'onnxruntime', 'numpy', 'Pillow', 'tifffile'):
                try:
                    versions[name] = importlib.metadata.version(name)
                except importlib.metadata.PackageNotFoundError:
                    versions[name] = None
            result = dict(python=sys.executable, versions=versions, workspace_id=self.workspace_id, data_home=str(self.data_home))
            if p.get('include_gpu'):
                from core.hardware import HardwareProbe
                result['hardware'] = HardwareProbe().sample()
            return result
        if method == 'projects.list':
            return dict(items=self.store.list_projects())
        if method == 'projects.create':
            return dict(project=self.store.create_project(p.get('name', '')))
        if method == 'projects.rename':
            project_id = self.project_id(p)
            self._guard_edit(project_id)
            self.store.rename_project(project_id, p.get('name', ''))
            return dict(project=self.store.get_project(project_id))
        if method == 'projects.delete':
            project_id = self.project_id(p)
            if p.get('confirmed') is not True or p.get('confirm_project_id') != project_id:
                raise ServiceError('删除必须由界面明确确认项目身份。', 'confirmation_required')
            self._guard_edit(project_id)
            self.store.delete_project(project_id)
            if self.current_project == project_id:
                self.current_project = None
            return dict(deleted=True)
        if method == 'context.select':
            self.current_project = self.project_id(p)
            recovered = self.recover_annotations(self.current_project)
            return dict(project=self.store.get_project(self.current_project), classes=self.store.list_classes(self.current_project),
                        settings=dict(preprocess=self.store.get_setting(self.current_project, 'preprocess', {})), workspace_id=self.workspace_id,
                        recovered_annotations=recovered)
        if method == 'classes.list':
            project_id = self.project_id(p)
            snapshot = self.store.snapshot(project_id)
            counts = {c['class_id']: 0 for c in snapshot['classes']}
            for image in snapshot['images']:
                for box in image['annotations']:
                    counts[box['class_id']] += 1
            return dict(items=[dict(c, annotation_count=counts[c['class_id']]) for c in snapshot['classes']])
        if method == 'classes.save':
            project_id = self.project_id(p)
            self._guard_edit(project_id)
            class_id = self.store.save_class(project_id, p.get('name', ''), p.get('color', '#2C7BE5'), p.get('class_id'))
            return dict(class_id=class_id, items=self.store.list_classes(project_id))
        if method == 'images.list':
            return self.images_list(p)
        if method == 'images.import':
            from core.importer import ImageImporter
            project_id = self.project_id(p)
            paths = p.get('paths') or []
            if not isinstance(paths, list) or not paths:
                raise ServiceError('请提供单张、批量文件或文件夹路径。')
            if any(not Path(path).is_absolute() for path in paths):
                raise ServiceError('导入路径必须是绝对路径。', 'absolute_path_required')
            return self.start_task('import', project_id, lambda cancel, progress:
                ImageImporter(self.store).import_paths(project_id, paths, bool(p.get('recursive', True)), cancel, progress))
        if method == 'images.delete':
            project_id = self.project_id(p)
            if p.get('confirmed') is not True:
                raise ServiceError('删除图片记录及标注需显式确认。', 'confirmation_required')
            self._guard_edit(project_id)
            return dict(deleted=self.store.delete_images(project_id, p.get('image_ids') or []))
        if method == 'annotations.get':
            return self._annotations(self.image(p))
        if method == 'annotations.save':
            image = self.image(p)
            self._guard_edit(image['project_id'])
            current = self._annotations(image)
            if p.get('expected_revision') is not None and p['expected_revision'] != current['revision']:
                raise ServiceError('标注已被其他操作更新，请重新载入后保存。', 'revision_conflict')
            boxes = p.get('boxes') or []
            if not isinstance(boxes, list) or len(boxes) > 10000:
                raise ServiceError('单图标注数量超出 10000 上限。')
            self.save_annotations(image, boxes, p.get('reviewed', True))
            return self._annotations(image)
        if method in ('images.review', 'images.batch'):
            project_id = self.project_id(p)
            self._guard_edit(project_id)
            ids = list(dict.fromkeys(p.get('image_ids') or []))
            for image_id in ids:
                self.image(dict(project_id=project_id, image_id=image_id))
            if method == 'images.batch':
                self.store.set_batch(project_id, ids, p.get('batch', ''))
            else:
                for image_id in ids:
                    self.store.replace_annotations(image_id, self.store.get_annotations(image_id), bool(p.get('reviewed', True)))
            return dict(updated=len(ids))
        if method in ('preview', 'tile', 'slices.preview'):
            return self.preview(p, method)
        if method == 'preprocessing.save':
            from core.preprocessing import PreprocessConfig
            project_id = self.project_id(p)
            value = PreprocessConfig.from_dict(p.get('preprocess')).to_dict()
            self.store.set_setting(project_id, 'preprocess', value)
            return dict(preprocess=value, display_does_not_rewrite_source=True)
        if method in ('dataset.build', 'slices.run'):
            from core.dataset_builder import DatasetBuilder
            project_id = self.project_id(p)
            options = dict(p.get('options') or {}, kind='dataset' if method == 'dataset.build' else 'slices')
            options.setdefault('preprocess', self.store.get_setting(project_id, 'preprocess', {}))
            snapshot = self.store.snapshot(project_id)
            output_parent = options.get('output_parent') or self.store.paths.absolute(snapshot['project']['data_dir']) / 'derived'
            if not Path(output_parent).is_absolute():
                raise ServiceError('数据集输出父目录必须为绝对路径。', 'absolute_path_required')
            if os.name == 'nt' and len(str(Path(output_parent).resolve())) > 110:
                options['output_parent'] = extended_path(output_parent)
            return self.start_task(options['kind'], project_id, lambda cancel, progress:
                DatasetBuilder(self.store).run(project_id, options, cancel, progress, snapshot))
        if method == 'dataset.validate':
            from core.dataset_builder import validate_dataset
            return self.start_task('dataset-check', self.project_id(p), lambda cancel, _progress: validate_dataset(p['path'], cancel))
        if method == 'history.list':
            project_id = self.project_id(p)
            return dict(items=self.store.list_runs(project_id), datasets=self.training.datasets(project_id),
                        training=self.training.repository.list(project_id), vision=self.vision.repository.list(project_id))
        if method in ('labels.import', 'labels.export'):
            from core.label_io import import_label_directory, export_project_labels
            project_id = self.project_id(p)
            action = import_label_directory if method == 'labels.import' else export_project_labels
            return self.start_task(method, project_id, lambda cancel, progress: action(self.store, project_id, p['path'], cancel, progress))
        if method == 'analysis':
            from core.analysis import analyze_sizes, export_analysis_csv
            report = analyze_sizes(self.store.snapshot(self.project_id(p)))
            if p.get('export_path'):
                export_analysis_csv(report, p['export_path'])
            return report
        if method == 'examples.starter.prepare':
            from starter_example import prepare_starter
            return prepare_starter(self, p.get('fresh') is True, atomic_json)
        if method in ('training.start', 'training.resume'):
            return self.start_training(p, method == 'training.resume')
        if method == 'training.list':
            self.training.reconcile()
            return dict(items=self.training.repository.list(p.get('project_id')), datasets=self.training.datasets(self.project_id(p)) if p.get('project_id') or self.current_project else [])
        if method == 'training.poll':
            selected_id = p.get('task_id') or p.get('run_id')
            project_id = p.get('project_id') or self.current_project
            candidates = self.training.repository.list(project_id)
            if selected_id and not any(r['id'] == selected_id for r in candidates):
                raise ServiceError('训练任务不存在或不属于当前AI项目。', 'task_missing')
            selected_id = selected_id or (candidates[0]['id'] if candidates else None)
            if selected_id is None:
                return dict(task_id=None, records=[], models=[], selected=None, metrics=[], log_text='', log_offset=0)
            result = self.training.poll(selected_id, int(p.get('log_offset', 0)))
            result['records'] = [r for r in result['records'] if not project_id or r['project_id'] == project_id]
            result['models'] = [r for r in result['models'] if not project_id or r['project_id'] == project_id]
            result['task_id'] = (result.get('selected') or {}).get('id')
            return result
        if method == 'training.stop':
            task_id = p.get('task_id') or p.get('run_id')
            if p.get('force'):
                self.training.force_stop(task_id)
            else:
                self.training.stop(task_id)
            return dict(task_id=task_id, status='stop_requested')
        if method == 'models.list':
            from core.training.models import model_catalog
            rows = self.training.repository.models()
            for row in rows:
                row['exists'] = Path(row['path']).is_file()
                row['metrics'] = json.loads(row['metrics']) if isinstance(row['metrics'], str) else row['metrics']
                if p.get('include_hashes') and row['exists']:
                    row['model_sha256'] = digest(row['path'])
            exports = self.vision.repository.list(p.get('project_id'), 'export')
            if p.get('include_hashes'):
                for run in exports:
                    path = Path(run['summary'].get('onnx') or '__missing_export__')
                    run['model_exists'] = path.is_file()
                    if run['status'] == 'completed' and run['model_exists']:
                        run['model_sha256'] = digest(path)
            return dict(items=rows, export_runs=exports, catalog=model_catalog())
        if method == 'deployment.inspect':
            return self.inspect_deployment(p)
        if method == 'models.input_contract':
            return self.inspect_model_input(p)
        if method in ('inference.start', 'evaluation.start', 'comparison.start', 'models.export'):
            return self.start_vision(method, p)
        if method == 'inference.results':
            run_id = p.get('run_id') or p.get('task_id')
            task = self.tasks.get(run_id)
            if task and isinstance(task.get('result'), dict):
                run_id = task['result'].get('id', run_id)
            run = self.vision.repository.get(run_id)
            if not run:
                raise ServiceError('检测任务不存在。', 'task_missing')
            rows = self.vision.repository.results(run_id)
            total = len(rows)
            page = max(0, int(p.get('page', 0)))
            page_size = min(50, max(1, int(p.get('page_size', 20))))
            rows = rows[page * page_size:(page + 1) * page_size]
            items = []
            for row in rows:
                result = self._read_json(row['result_path'], {})
                if not p.get('include_tiles'):
                    result.pop('tiles', None)
                result.update(result_path=row['result_path'], preview_path=row['preview_path'], run_id=run_id)
                items.append(result)
            return dict(run=run, items=items, total=total, page=page, page_size=page_size)
        if method == 'tasks.list':
            with self.lock:
                rows = [dict(t) for t in self.tasks.values() if not p.get('project_id') or t['project_id'] == p['project_id']]
            return dict(items=rows)
        if method == 'tasks.cancel':
            task_id = p.get('task_id')
            if task_id in self.cancels:
                self.cancels[task_id].set()
            elif self.training.repository.get(task_id):
                self.training.stop(task_id)
            else:
                raise ServiceError('任务不存在。', 'task_missing')
            return dict(task_id=task_id, status='cancel_requested')
        if method == 'shutdown':
            stop = bool(p.get('stop_training', not p.get('keep_training', True)))
            if stop and p.get('detach_training'):
                raise ServiceError('停止训练与保留后台训练不能同时选择。')
            return self.shutdown(stop)
        raise ServiceError('未知 AI 服务方法：' + str(method), 'method_not_found')

    def images_list(self, p):
        project_id = self.project_id(p)
        page = max(0, int(p.get('page', 0)))
        page_size = min(120, max(1, int(p.get('page_size', 120))))
        filtered = p.get('filter', 'all')
        search = str(p.get('search', '')).casefold()
        if filtered == 'all' and not search and p.get('class_id') is None:
            total = self.store.image_count(project_id)
            items = self.store.list_images(project_id, page_size, page * page_size)
        else:
            snapshot = self.store.snapshot(project_id)
            candidates = snapshot['images']
            if filtered == 'reviewed':
                candidates = [i for i in candidates if i['reviewed']]
            elif filtered == 'unreviewed':
                candidates = [i for i in candidates if not i['reviewed']]
            elif filtered == 'normal':
                candidates = [i for i in candidates if i['reviewed'] and not i['annotations']]
            elif filtered == 'annotated':
                candidates = [i for i in candidates if i['annotations']]
            elif filtered != 'all':
                raise ServiceError('未知图片筛选条件。')
            if search:
                candidates = [i for i in candidates if search in i['filename'].casefold() or search in i.get('batch', '').casefold()]
            if p.get('class_id') is not None:
                candidates = [i for i in candidates if any(b['class_id'] == int(p['class_id']) for b in i['annotations'])]
            total = len(candidates)
            items = candidates[page * page_size:(page + 1) * page_size]
        for image in items:
            image['managed_absolute'] = str(self.store.paths.absolute(image['managed_path']).resolve())
            image['thumbnail_absolute'] = str(self.store.paths.absolute(image['thumbnail_path']).resolve())
            image['annotation_count'] = len(image.pop('annotations', self.store.get_annotations(image['id'])))
        return dict(items=items, total=total, page=page, page_size=page_size)

    def preview(self, p, method):
        from PIL import Image
        from core.preprocessing import RasterSource, PreprocessConfig
        from core.annotations import tile_grid, clip_box
        image = self.image(p)
        config = PreprocessConfig.from_dict(p.get('preprocess') or self.store.get_setting(image['project_id'], 'preprocess', {}))
        source_path = self.store.paths.absolute(image['managed_path']).resolve(strict=True)
        output = self.transfer_root / (uuid.uuid4().hex + '.png')
        owned = [path for path in self.owned_previews if path.is_file()]
        if len(owned) >= 64 or sum(path.stat().st_size for path in owned) >= 256 * 1024 * 1024:
            raise ServiceError('显示缓存已达64个文件或256MiB，请释放旧预览后重试。', 'cache_full')
        tiles = None
        with RasterSource(source_path, config) as source:
            if method == 'tile':
                x, y = int(p.get('x', 0)), int(p.get('y', 0))
                width, height = int(p.get('width', 512)), int(p.get('height', 512))
                if width <= 0 or height <= 0 or width > 4096 or height > 4096:
                    raise ServiceError('原像素局部区域尺寸须在1～4096。')
                pixels = source.crop(x, y, width, height, bool(p.get('pad', False)))
                region = dict(x=x, y=y, width=pixels.shape[1], height=pixels.shape[0])
            else:
                maximum = min(2048, max(64, int(p.get('max_edge', 1600))))
                pixels = source.preview(maximum)
                region = None
            preprocessing = source.metadata()
            if method == 'slices.preview':
                options = p.get('options') or p
                tiles = tile_grid(image['width'], image['height'], int(options.get('tile_width', 1024)),
                                  int(options.get('tile_height', 1024)), float(options.get('overlap', .15)))
                selected_index = int(p.get('tile_index', -1))
                if selected_index >= 0:
                    if selected_index >= len(tiles):
                        raise ServiceError('切片编号越界。')
                    tile = tiles[selected_index]
                    pixels = source.crop(tile['x'], tile['y'], tile['width'], tile['height'], True)
                    region = tile
        Image.fromarray(pixels).save(output)
        self.owned_previews.add(output)
        result = dict(path=str(output), image_id=image['id'], width=image['width'], height=image['height'],
                      original_width=image['width'], original_height=image['height'],
                      preview_width=pixels.shape[1], preview_height=pixels.shape[0], source_sha256=digest(source_path),
                      preprocessing=preprocessing, temporary_owner='ai-service', region=region)
        if region:
            result.update(x=region['x'], y=region['y'])
        if tiles is not None:
            boxes = self.store.get_annotations(image['id'])
            stride = max(1, math.ceil(len(tiles) / 2500))
            result.update(tiles=tiles[::stride], tile_count=len(tiles), tiles_sampled=stride > 1, boxes=boxes)
            if region:
                result['tile_boxes'] = [b for box in boxes if (b := clip_box(box, region))]
        return result

    def start_training(self, p, resume):
        project_id = self.project_id(p)
        self._guard_edit(project_id)
        config = dict(p.get('config') or {})
        if config.get('allow_download'):
            raise ServiceError('集成训练不自动下载模型，请明确选择已有本地权重。', 'download_disabled')
        config['allow_download'] = False
        checkpoint = p.get('checkpoint') if resume else None
        parent = p.get('parent_id') or p.get('task_id')
        if resume:
            record = self.training.repository.get(parent)
            if not record or record['project_id'] != project_id:
                raise ServiceError('请选择原训练任务再恢复。', 'task_missing')
            from core.training.models import validate_resume
            checkpoint = checkpoint or record.get('last_pt')
            if not checkpoint:
                raise ServiceError('原训练任务没有可恢复检查点。')
            validate_resume(checkpoint)
            if not config.get('dataset'):
                config = dict(record['config'], allow_download=False)
        def clean_path(value):
            text = str(value or '').strip()
            if len(text) >= 2 and text[0] == text[-1] and text[0] in ('"', "'"):
                text = text[1:-1].strip()
            return text
        model_text = clean_path(checkpoint or config.get('model', ''))
        if not model_text:
            raise ServiceError('请先选择初始权重。', 'model_missing')
        model_path = Path(model_text)
        if not model_path.is_absolute():
            raise ServiceError('请选择完整路径，或点击右侧“选择”按钮。', 'absolute_path_required')
        if not model_path.is_file():
            raise ServiceError('请选择已有本地 PT 模型；服务不会下载替代模型。', 'model_missing')
        if model_path.suffix.lower() != '.pt':
            raise ServiceError('训练初始权重需要 .pt 文件，ONNX 用于推理或部署。', 'model_type')
        config['model'] = str(model_path.resolve())
        dataset = Path(clean_path(config.get('dataset')))
        if not dataset.is_absolute() or not dataset.is_file() or dataset.suffix.lower() not in ('.yaml', '.yml'):
            raise ServiceError('请先选择已生成并存在的训练数据集 data.yaml 完整路径。', 'dataset_missing')
        config['dataset'] = str(dataset.resolve())
        from core.training.config import TrainConfig
        TrainConfig.from_dict(config).validate()
        record = self.training.launch(project_id, config, checkpoint, parent if resume else None)
        return dict(task_id=record['id'], status=record['status'], record=record)

    def inspect_model_input(self, p):
        """CPU-only metadata inspection; never inference, training, export or GPU acquisition."""
        model = Path(p.get('model_path', '')).expanduser()
        if not model.is_absolute():
            raise ServiceError('模型须使用明确绝对路径。', 'absolute_path_required')
        if not model.is_file():
            raise ServiceError('所选模型文件不存在，请重新选择。', 'model_missing')
        model = model.resolve(strict=True)
        if model.suffix.lower() != '.onnx':
            raise ServiceError('此操作仅检查ONNX实际输入；PT由原引擎在显式检测时校验。', 'model_type_invalid')
        import onnxruntime as ort
        opts = ort.SessionOptions()
        opts.intra_op_num_threads = 1
        session = ort.InferenceSession(str(model), sess_options=opts, providers=['CPUExecutionProvider'])
        if len(session.get_inputs()) != 1 or len(session.get_outputs()) != 1:
            raise ServiceError('现有检测引擎只支持单输入、单输出ONNX。', 'input_contract_invalid')
        actual = session.get_inputs()[0]
        shape = list(actual.shape)
        if len(shape) != 4 or shape[1] != 3 or actual.type not in ('tensor(float)', 'tensor(float16)'):
            raise ServiceError('ONNX须为NCHW三通道float/float16输入。', 'input_contract_invalid')
        fixed = lambda value: value if isinstance(value, int) and not isinstance(value, bool) and value > 0 else None
        batch, height, width = fixed(shape[0]), fixed(shape[2]), fixed(shape[3])
        if (height is None) != (width is None) or height is not None and height != width:
            raise ServiceError('现有引擎使用正方形输入，不支持此模型的非正方形或单轴动态输入；不会改模型或重导出。', 'input_contract_unsupported')
        if batch is not None and not 1 <= batch <= 64:
            raise ServiceError('实际固定Batch超出现有引擎范围1..64。', 'input_contract_unsupported')
        if height is not None and not 32 <= height <= 4096:
            raise ServiceError('实际固定尺寸超出现有引擎范围32..4096。', 'input_contract_unsupported')
        deployment = self._read_json(model.with_name('deployment.json'), {})
        inp = deployment.get('input') or {}
        if inp.get('dtype') != actual.type:
            raise ServiceError('ONNX实际类型与deployment.json不一致。', 'deployment_mismatch')
        if not inp.get('dynamic') and (height is None or inp.get('height') != height or inp.get('width') != width):
            raise ServiceError('ONNX实际尺寸与deployment.json固定尺寸不一致。', 'deployment_mismatch')
        if height is not None and (inp.get('height') != height or inp.get('width') != width):
            raise ServiceError('实际固定尺寸与部署元数据不一致。', 'deployment_mismatch')
        if batch is not None and inp.get('batch') is not None and inp['batch'] != batch:
            raise ServiceError('ONNX实际Batch与deployment.json不一致。', 'deployment_mismatch')
        del session
        # The existing verifier also checks hash/names/preprocessing/output contract and DetectorBackend.
        verified = self.inspect_deployment(dict(p, image_size=height or int(p.get('image_size', 640)), batch=batch or int(p.get('batch', 1))))
        return dict(model_path=str(model), model_sha256=verified['model_sha256'], shape=shape, dtype=actual.type,
                    fixed_batch=batch, fixed_size=height, backend='onnx', inspection_provider='CPUExecutionProvider',
                    validated=True, deployment=verified['deployment'])

    def inspect_deployment(self, p):
        from core.inference.backend import DetectorBackend
        from core.inference.config import DetectConfig
        from core.preprocessing import PreprocessConfig
        supplied_model = Path(p.get('model_path', '')).expanduser()
        if not supplied_model.is_absolute():
            raise ServiceError('模型须使用明确绝对路径。', 'absolute_path_required')
        model = supplied_model.resolve(strict=True)
        if model.suffix.lower() != '.onnx':
            raise ServiceError('当前检测节点只接受附 deployment.json 的 ONNX CPU 部署；PT/CUDA仅在AI测试页显式使用。', 'node_backend_unsupported')
        deployment_path = model.with_name('deployment.json')
        if not deployment_path.is_file() or deployment_path.stat().st_size > 256 * 1024:
            raise ServiceError('缺少 deployment.json 或其大小超过256KiB。', 'deployment_missing')
        deployment = self._read_json(deployment_path, {})
        observed = digest(model)
        inp = deployment.get('input') or {}
        output = deployment.get('output') or {}
        if deployment.get('schema_version') != 1 or deployment.get('task') != 'detect' or deployment.get('model_file') != model.name or deployment.get('onnx_sha256') != observed:
            raise ServiceError('模型与 deployment.json 的身份/哈希/任务不匹配。', 'deployment_mismatch')
        if inp.get('layout') != 'NCHW' or inp.get('channel_order') != 'RGB' or inp.get('dtype') not in ('tensor(float)', 'tensor(float16)'):
            raise ServiceError('部署输入布局、通道或数据类型不受现有节点支持。')
        names = {int(k): str(v) for k, v in deployment.get('names', {}).items()}
        if not names or sorted(names) != list(range(len(names))) or any(not v.strip() for v in names.values()):
            raise ServiceError('部署类别表必须非空且ID连续。')
        if output.get('format') not in ('xyxy_conf_class', 'xywh_class_scores'):
            raise ServiceError('未知检测输出布局。')
        prep = deployment.get('preprocessing') or {}
        PreprocessConfig.from_dict(prep.get('config'))
        norm = inp.get('normalization') or {}
        resize = inp.get('resize') or {}
        if not math.isclose(float(norm.get('scale', -1)), 1 / 255, rel_tol=1e-12) or norm.get('mean') != [0, 0, 0] or norm.get('std') != [1, 1, 1]:
            raise ServiceError('部署归一化契约不受原推理器支持。')
        if resize.get('method') != 'letterbox' or resize.get('padding_value') != [114, 114, 114] or resize.get('center') is not True:
            raise ServiceError('部署letterbox规则不受原推理器支持。')
        size = int(p.get('image_size') or p.get('imgsz') or inp.get('height', 0))
        config = DetectConfig.from_dict(dict(model=str(model), backend='onnx', device='cpu', imgsz=size,
                                            batch=int(p.get('batch', 1)), preprocess=prep.get('config')))
        with DetectorBackend(config, owner='visionstudio-deployment-inspection') as backend:
            actual = backend.session.get_inputs()[0]
            if actual.type != inp['dtype']:
                raise ServiceError('ONNX实际输入类型与部署文件不符。')
            if len(actual.shape) != 4 or actual.shape[1] != 3:
                raise ServiceError('ONNX实际输入不是NCHW三通道。')
            if not inp.get('dynamic') and (actual.shape[2] != size or actual.shape[3] != size):
                raise ServiceError('ONNX实际尺寸与部署文件不符。')
            provider = backend.provider
        model_record_id = next((r['id'] for r in self.training.repository.models() if Path(r['path']).resolve() == model), None)
        return dict(validated=True, validated_for_node=True, model_path=str(model), model_sha256=observed,
                    deployment_path=str(deployment_path), deployment_sha256=digest(deployment_path), deployment=deployment,
                    names=names, input_size=size, preprocessing=prep, slicing=deployment.get('slicing'),
                    backend='onnx', provider=provider, model_record_id=model_record_id,
                    workspace_id=self.workspace_id, project_id=p.get('project_id') or self.current_project)

    def start_vision(self, method, p):
        project_id = self.project_id(p)
        model = Path(p.get('model_path') or (p.get('config') or {}).get('model', '')).expanduser()
        if not model.is_absolute():
            raise ServiceError('模型须使用明确绝对路径。', 'absolute_path_required')
        if not model.is_file():
            raise ServiceError('请明确选择存在的模型，服务不会下载或替换。', 'model_missing')
        config = dict(p.get('config') or {}, model=str(model.resolve()))
        if method == 'inference.start':
            inputs = p.get('inputs') or []
            if not inputs:
                inputs = [str(self.store.paths.absolute(i['managed_path'])) for i in self.store.snapshot(project_id)['images']]
            if any(not Path(path).is_absolute() for path in inputs):
                raise ServiceError('检测图像或目录必须使用绝对路径。', 'absolute_path_required')
            return self.start_task('inference', project_id, lambda cancel, progress:
                self.run_inference(project_id, config, inputs, p, cancel, progress))
        if method == 'models.export':
            from core.inference.deployment import export_onnx
            return self.start_task('export', project_id, lambda cancel, progress:
                export_onnx(self.vision, project_id, str(model.resolve()), p.get('options') or {}, cancel, progress))
        if method == 'evaluation.start':
            from core.inference.evaluation import evaluate_model
            return self.start_task('evaluation', project_id, lambda cancel, progress:
                evaluate_model(self.vision, project_id, str(model.resolve()), p['dataset'], p.get('options') or config, cancel, progress))
        from core.inference.deployment import compare_models
        return self.start_task('comparison', project_id, lambda cancel, progress:
            compare_models(self.vision, project_id, str(model.resolve()), p['onnx_model'], p['image'], p.get('options') or config, cancel, progress))

    def run_inference(self, project_id, config, inputs, request, cancel, progress):
        """Retain immutable identities around the existing VisionService operation.

        File detections receive a new run-local frame UUID and are explicitly marked
        as file sources. A caller-supplied camera UUID requires a matching image SHA;
        this is a validated snapshot binding, not a claim that this service acquired it.
        """
        from core.inference.service import expand_images
        paths = expand_images(inputs, cancel)
        expected = {os.path.normcase(str(path.resolve())): digest(path) for path in paths}
        requested_sha = request.get('image_sha256') or request.get('source_sha256')
        camera_frame_id = request.get('camera_frame_id')
        if camera_frame_id:
            uuid.UUID(str(camera_frame_id))
            if len(paths) != 1 or not requested_sha:
                raise ServiceError('相机快照必须提供唯一图像与SHA256，才能保留调用方frameId。', 'image_identity_required')
        if requested_sha and (len(paths) != 1 or next(iter(expected.values())).casefold() != str(requested_sha).casefold()):
            raise ServiceError('输入图像SHA256与请求身份不符。', 'image_identity_mismatch')
        model_sha = digest(config['model'])
        supplied_model_sha = request.get('model_sha256')
        if supplied_model_sha and supplied_model_sha.casefold() != model_sha:
            raise ServiceError('所选模型SHA256与请求身份不符。', 'model_identity_mismatch')
        run = self.vision.detect(project_id, config, list(map(str, paths)), cancel, progress)
        if run['status'] != 'completed':
            return run
        if digest(config['model']) != model_sha:
            self.vision.repository.update(run['id'], 'failed', run['summary'], '推理期间模型文件发生变化；禁止发布身份不明确的结果。')
            raise ServiceError('推理期间模型发生变化。', 'model_identity_mismatch')
        image_lookup = {os.path.normcase(str(self.store.paths.absolute(i['managed_path']).resolve())): i['id']
                        for i in self.store.snapshot(project_id)['images']}
        for row in self.vision.repository.results(run['id']):
            result = self._read_json(row['result_path'], {})
            source = Path(result['source']).resolve()
            key = os.path.normcase(str(source))
            if key not in expected or digest(source) != expected[key]:
                self.vision.repository.update(run['id'], 'failed', run['summary'], '推理期间输入图像发生变化；禁止发布旧身份结果。')
                raise ServiceError('推理期间输入图像发生变化。', 'image_identity_mismatch')
            result.update(task_id=getattr(progress, 'task_id', run['id']), run_id=run['id'],
                          model_sha256=model_sha, source_sha256=expected[key],
                          frame_id=str(camera_frame_id) if camera_frame_id else str(uuid.uuid4()),
                          source_kind='camera_snapshot' if camera_frame_id else 'file',
                          frame_binding='caller UUID + verified SHA256' if camera_frame_id else 'run-local file frame UUID',
                          image_id=image_lookup.get(key, result['id']), source_image_id=image_lookup.get(key),
                          identity_verified=True)
            atomic_json(row['result_path'], result)
        return dict(run, task_id=getattr(progress, 'task_id', run['id']), model_sha256=model_sha)

    def release_files(self, paths):
        released = 0
        for supplied in paths:
            path = Path(supplied).resolve()
            if path.parent != self.transfer_root or path.suffix.lower() != '.png' or path not in self.owned_previews:
                raise ServiceError('只能释放本次服务拥有的预览文件。', 'path_protected')
            if path.is_file():
                path.unlink()
                released += 1
            self.owned_previews.discard(path)
        return dict(released=released)

    def shutdown(self, stop_training=False):
        from core.training.processes import verified_process
        self.training.reconcile()
        active = [r for r in self.training.repository.list() if r['status'] in ('waiting', 'running')]
        if stop_training:
            for record in active:
                self.training.stop(record['id'])
            deadline = time.monotonic() + 18
            while time.monotonic() < deadline:
                self.training.reconcile()
                records = self.training.repository.list()
                if not any(r['status'] in ('waiting', 'running') or verified_process(r) for r in records):
                    break
                time.sleep(.2)
            else:
                raise ServiceError('训练尚未安全停止；可继续等待、显式强制停止或选择保留后台训练。', 'training_stopping')
        self.closing = True
        for cancel in self.cancels.values():
            cancel.set()
        if self.futures:
            _done, running = wait(list(self.futures.values()), timeout=12)
            if running:
                self.closing = False
                raise ServiceError('数据处理/推理仍在安全结束，请稍后关闭。', 'tasks_stopping')
        self._save_tasks()
        self.pool.shutdown(wait=False, cancel_futures=True)
        self.release_files(list(self.owned_previews))
        self.lease.release()
        return dict(closed=True, detached_training=bool(active and not stop_training),
                    training_task_ids=[r['id'] for r in active] if not stop_training else [])


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--yolo-root', type=Path, required=True)
    parser.add_argument('--data-home', type=Path, required=True)
    parser.add_argument('--transfer-root', type=Path, required=True)
    args = parser.parse_args()
    if hasattr(sys.stdin, 'reconfigure'):
        sys.stdin.reconfigure(encoding='utf-8')
        sys.stdout.reconfigure(encoding='utf-8', newline='\n', write_through=True)
        sys.stderr.reconfigure(encoding='utf-8', errors='replace')
    protocol = sys.stdout
    sys.stdout = sys.stderr  # Third-party print/log output is never parsed as protocol JSON.
    logging.basicConfig(stream=sys.stderr, level=logging.INFO,
                        format='%(asctime)s %(levelname)s %(name)s: %(message)s')
    output_gate = threading.Lock()

    def emit(value):
        text = json.dumps(value, ensure_ascii=False, default=safe_json, allow_nan=False, separators=(',', ':'))
        if len(text.encode('utf-8')) > MAX_RESPONSE:
            raise ServiceError('控制响应超过8MiB；请分页或读取结果文件。', 'response_too_large')
        with output_gate:
            protocol.write(text + '\n')
            protocol.flush()

    service = None
    try:
        service = AiWorkspaceService(args.yolo_root, args.data_home, args.transfer_root, emit)
        while True:
            line = sys.stdin.readline(MAX_REQUEST + 1)
            if not line:
                break
            request_id = None
            try:
                if len(line.encode('utf-8')) > MAX_REQUEST or not line.endswith('\n'):
                    raise ServiceError('请求超过2MiB或未结束。', 'request_too_large')
                request = json.loads(line)
                request_id = request.get('requestId')
                if request.get('version') != VERSION or not isinstance(request_id, str) or not 1 <= len(request_id) <= 128:
                    raise ServiceError('协议版本或requestId无效。', 'protocol_mismatch')
                method, payload = request.get('method'), request.get('payload') or {}
                if method == 'files.release':
                    result = service.release_files(payload.get('paths') or [])
                else:
                    result = service.dispatch(method, payload)
                emit(dict(version=VERSION, requestId=request_id, success=True, result=result))
                if method == 'shutdown' and result.get('closed'):
                    return 0
            except Exception as exc:
                logging.exception('AI request failed: %s', request_id)
                emit(dict(version=VERSION, requestId=request_id, success=False,
                          error=dict(code=getattr(exc, 'code', type(exc).__name__), message=str(exc)[:16384])))
        if service and not service.closing:
            service.shutdown(False)
        return 0
    except Exception as exc:
        logging.exception('AI service startup/exit failure')
        emit(dict(version=VERSION, requestId=None, success=False,
                  error=dict(code=getattr(exc, 'code', type(exc).__name__), message=str(exc))))
        return 2
    finally:
        if service:
            # If stdin vanished during an uninterruptible native call, keep the
            # write lease until its owned data tasks really finish. Releasing the
            # lease first would allow a second frontend into a still-writing home.
            if not service.closing:
                for cancel in service.cancels.values():
                    cancel.set()
                service.pool.shutdown(wait=True, cancel_futures=True)
            service.lease.release()


if __name__ == '__main__':
    raise SystemExit(main())

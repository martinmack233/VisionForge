from __future__ import annotations

import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import threading
import time

import numpy as np
from PIL import Image
import pytest
import tifffile


SERVICE_FILE = Path(__file__).resolve().parents[1] / 'service.py'
YOLO_ROOT = Path(os.environ.get('VISIONSTUDIO_TEST_YOLO_ROOT',
    Path(__file__).resolve().parents[4] / 'YOLOStudio')).resolve()
if not YOLO_ROOT.is_dir():
    YOLO_ROOT = Path(__file__).resolve().parents[4].parent / 'YOLOStudio'
spec = importlib.util.spec_from_file_location('visionstudio_ai_service_tests', SERVICE_FILE)
module = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = module
spec.loader.exec_module(module)


@pytest.fixture
def service(tmp_path):
    instance = module.AiWorkspaceService(YOLO_ROOT, tmp_path / 'data', tmp_path / 'transfer')
    yield instance
    if not instance.closing:
        instance.shutdown(False)


def project(service):
    record = service.dispatch('projects.create', {'name': '测试项目'})['project']
    service.dispatch('context.select', {'project_id': record['id']})
    return record


def tiny_onnx(folder, shape=(1, 3, 64, 64), dynamic=False):
    """Synthetic constant-output ONNX for input-contract tests, not a detector accuracy model."""
    import onnx
    from onnx import helper, TensorProto
    folder.mkdir(parents=True, exist_ok=True)
    path = folder / 'contract.onnx'
    output = helper.make_tensor('constant', TensorProto.FLOAT, [1, 1, 6], [0, 0, 1, 1, .8, 0])
    graph = helper.make_graph([helper.make_node('Constant', [], ['output'], value=output)], 'contract-fixture',
        [helper.make_tensor_value_info('images', TensorProto.FLOAT, list(shape))],
        [helper.make_tensor_value_info('output', TensorProto.FLOAT, [1, 1, 6])])
    model = helper.make_model(graph, opset_imports=[helper.make_opsetid('', 17)], ir_version=8)
    onnx.save(model, str(path))
    deployment = dict(schema_version=1, task='detect', model_file=path.name,
        onnx_sha256=hashlib.sha256(path.read_bytes()).hexdigest(), names={'0': 'synthetic'},
        input=dict(layout='NCHW', channel_order='RGB', dtype='tensor(float)', height=64, width=64,
            dynamic=dynamic, normalization=dict(scale=1/255, mean=[0, 0, 0], std=[1, 1, 1]),
            resize=dict(method='letterbox', padding_value=[114, 114, 114], center=True)),
        output=dict(format='xyxy_conf_class'), preprocessing=dict(config=None))
    path.with_name('deployment.json').write_text(json.dumps(deployment), encoding='utf-8')
    return path


def test_actual_onnx_static_and_dynamic_contracts_use_cpu_without_tasks(service, tmp_path):
    path = tiny_onnx(tmp_path / 'fixed')
    result = service.dispatch('models.input_contract', dict(model_path=str(path), image_size=640, batch=8))
    assert result['fixed_batch'] == 1 and result['fixed_size'] == 64
    assert result['shape'] == [1, 3, 64, 64]
    assert result['inspection_provider'] == 'CPUExecutionProvider'
    dynamic = tiny_onnx(tmp_path / 'dynamic', ('batch', 3, 'height', 'width'), True)
    result = service.dispatch('models.input_contract', dict(model_path=str(dynamic), image_size=128, batch=2))
    assert result['fixed_batch'] is None and result['fixed_size'] is None
    assert service.dispatch('tasks.list')['items'] == []


def test_onnx_metadata_conflict_missing_and_nonsquare_are_explicit(service, tmp_path):
    path = tiny_onnx(tmp_path / 'mismatch')
    metadata = path.with_name('deployment.json')
    value = json.loads(metadata.read_text()); value['input']['height'] = 128
    metadata.write_text(json.dumps(value))
    with pytest.raises(module.ServiceError, match='不一致'):
        service.dispatch('models.input_contract', dict(model_path=str(path)))
    with pytest.raises(module.ServiceError, match='不存在'):
        service.dispatch('models.input_contract', dict(model_path=str(tmp_path / 'deleted.onnx')))
    nonsquare = tiny_onnx(tmp_path / 'nonsquare', (1, 3, 64, 128))
    with pytest.raises(module.ServiceError, match='非正方形'):
        service.dispatch('models.input_contract', dict(model_path=str(nonsquare)))


def wait_task(service, request):
    deadline = time.monotonic() + 15
    while time.monotonic() < deadline:
        record = next(t for t in service.dispatch('tasks.list')['items'] if t['task_id'] == request['task_id'])
        if record['status'] in module.TERMINAL:
            assert record['status'] == 'completed', record
            return record
        time.sleep(.01)
    raise AssertionError('Task did not finish')


def import_images(service, folder, count=3, bit16=False):
    folder.mkdir(parents=True, exist_ok=True)
    for index in range(count):
        if bit16:
            tifffile.imwrite(folder / f'{index}.tif', np.arange(80 * 60, dtype=np.uint16).reshape(60, 80))
        else:
            pixels = np.zeros((60, 80, 3), np.uint8)
            pixels[10:30, 20:45, :] = 100 + index
            Image.fromarray(pixels).save(folder / f'{index}.png')
    wait_task(service, service.dispatch('images.import', {'paths': [str(folder)]}))
    return service.dispatch('images.list')['items']


def test_existing_services_import_annotations_review_and_optimistic_revision(service, tmp_path):
    project(service)
    images = import_images(service, tmp_path / 'images')
    assert service.dispatch('images.list', {'page_size': 2})['total'] == 3
    assert len(service.dispatch('images.list', {'page_size': 2})['items']) == 2
    image = images[0]
    before = service.dispatch('annotations.get', {'image_id': image['id']})
    assert not before['reviewed'] and not before['boxes']
    assert service.dispatch('images.list', {'filter': 'normal'})['total'] == 0
    service.dispatch('images.review', {'image_ids': [image['id']], 'reviewed': True})
    assert service.dispatch('images.list', {'filter': 'normal'})['total'] == 1
    baseline = service.dispatch('annotations.get', {'image_id': image['id']})
    saved = service.dispatch('annotations.save', {'image_id': image['id'], 'expected_revision': baseline['revision'],
        'reviewed': True, 'boxes': [dict(id='original-coordinate-box', class_id=0, x=20, y=10, w=25, h=20)]})
    assert saved['boxes'][0]['x'] == 20 and saved['boxes'][0]['id'] == 'original-coordinate-box'
    with pytest.raises(module.ServiceError, match='重新载入'):
        service.dispatch('annotations.save', {'image_id': image['id'], 'expected_revision': baseline['revision'], 'boxes': []})
    assert service.dispatch('images.list', {'filter': 'normal'})['total'] == 0
    assert service.dispatch('images.list', {'filter': 'unreviewed'})['total'] == 2
    assert service.dispatch('classes.list')['items'][0]['annotation_count'] == 1


def test_16bit_preview_and_native_tile_preserve_source_and_use_original_limits(service, tmp_path):
    project(service)
    image = import_images(service, tmp_path / 'sixteen', count=1, bit16=True)[0]
    source = Path(image['managed_absolute'])
    original_hash = hashlib.sha256(source.read_bytes()).hexdigest()
    preview = service.dispatch('preview', {'image_id': image['id'], 'preprocess': {'mode': 'percentile'}, 'max_edge': 64})
    assert preview['width'] == 80 and preview['height'] == 60
    assert preview['preview_width'] <= 64 and preview['source_sha256'] == original_hash
    tile = service.dispatch('tile', {'image_id': image['id'], 'x': 12, 'y': 9, 'width': 20, 'height': 21,
                                    'preprocess': {'mode': 'percentile'}})
    assert tile['x'] == 12 and tile['y'] == 9
    assert tile['preview_width'] == 20 and tile['preview_height'] == 21
    assert preview['preprocessing']['resolved_limits'] == tile['preprocessing']['resolved_limits']
    assert hashlib.sha256(source.read_bytes()).hexdigest() == original_hash
    unrelated = service.transfer_root / ('a' * 32 + '.png')
    unrelated.write_bytes(b'user-owned')
    with pytest.raises(module.ServiceError, match='只能释放'):
        service.release_files([unrelated])
    service.release_files([preview['path'], tile['path']])
    assert not Path(preview['path']).exists() and unrelated.read_bytes() == b'user-owned'


def test_dataset_uses_original_grouping_before_slicing_and_retains_history(service, tmp_path):
    current = project(service)
    images = import_images(service, tmp_path / 'sources')
    for image in images:
        service.dispatch('images.review', {'image_ids': [image['id']], 'reviewed': True})
    service.dispatch('images.batch', {'image_ids': [i['id'] for i in images[:2]], 'batch': 'same-acquisition'})
    task = wait_task(service, service.dispatch('dataset.build', {'options': dict(
        tiled=True, tile_width=32, tile_height=32, overlap=.25, grouping='batch', ratios=[.5, .5, 0], seed=7)}))
    root = Path(task['result']['path'])
    metadata = json.loads((root / 'run.json').read_text(encoding='utf-8'))
    by_id = {row['source_id']: row for row in metadata['sources']}
    assert by_id[images[0]['id']]['split'] == by_id[images[1]['id']]['split']
    assert (root / 'data.yaml').is_file() and (root / 'preprocess.json').is_file()
    history = service.dispatch('history.list', {'project_id': current['id']})
    assert history['items'][0]['status'] == 'completed' and history['datasets']


def test_workspace_lease_denies_second_editor_and_releases_after_close(service, tmp_path):
    from core.workspace_lock import WorkspaceLease, WorkspaceBusyError
    with pytest.raises(WorkspaceBusyError):
        WorkspaceLease(service.data_home, 'second UI').acquire()
    root, transfer = service.data_home, service.transfer_root
    service.shutdown(False)
    with WorkspaceLease(root, 'reopened UI'):
        assert root.is_dir()


def test_protocol_is_clean_json_and_missing_model_returns_explicit_failure(tmp_path):
    requests = [dict(version=1, requestId='hello-id', method='hello', payload={}),
                dict(version=1, requestId='model-id', method='deployment.inspect', payload={'model_path': str(tmp_path / 'missing.onnx')}),
                dict(version=1, requestId='shutdown-id', method='shutdown', payload={})]
    result = subprocess.run([sys.executable, '-B', '-u', str(SERVICE_FILE), '--yolo-root', str(YOLO_ROOT),
        '--data-home', str(tmp_path / 'workspace'), '--transfer-root', str(tmp_path / 'transfer')],
        input=''.join(json.dumps(r) + '\n' for r in requests), capture_output=True, text=True, encoding='utf-8', timeout=35)
    rows = [json.loads(line) for line in result.stdout.splitlines()]
    assert result.returncode == 0, result.stderr
    assert [r['requestId'] for r in rows] == ['hello-id', 'model-id', 'shutdown-id']
    assert rows[0]['success'] and not rows[1]['success'] and rows[2]['success']
    assert rows[0]['result']['workspace_write_lease']


def test_cancel_task_and_reopen_preserve_project_and_completed_records(service, tmp_path):
    current = project(service)
    image = import_images(service, tmp_path / 'images', count=1)[0]
    def cancellable(cancel, progress):
        for number in range(200):
            if cancel.wait(.01):
                return {'cancelled': True}
            progress(number, 200, 'bounded background work')
    task = service.start_task('test-cancel', current['id'], cancellable)
    service.dispatch('tasks.cancel', {'task_id': task['task_id']})
    service.futures[task['task_id']].result(timeout=5)
    assert service.tasks[task['task_id']]['status'] == 'cancelled'
    root, transfer = service.data_home, service.transfer_root
    service.shutdown(False)
    reopened = module.AiWorkspaceService(YOLO_ROOT, root, transfer)
    try:
        assert reopened.dispatch('projects.list')['items'][0]['id'] == current['id']
        reopened.dispatch('context.select', {'project_id': current['id']})
        assert reopened.dispatch('images.list')['items'][0]['id'] == image['id']
        assert any(t['task_id'] == task['task_id'] and t['status'] == 'cancelled' for t in reopened.dispatch('tasks.list')['items'])
    finally:
        reopened.shutdown(False)


def test_training_cannot_download_or_silently_replace_missing_weights(service, tmp_path):
    project(service)
    with pytest.raises(module.ServiceError, match='不自动下载'):
        service.dispatch('training.start', {'config': {'allow_download': True}})
    with pytest.raises(module.ServiceError, match='本地 PT'):
        service.dispatch('training.start', {'config': {'model': str(tmp_path / 'missing.pt')}})
    assert service.dispatch('training.list')['items'] == []


def test_training_file_and_numeric_validation_happens_before_launch(service, tmp_path, monkeypatch):
    current = project(service)
    weights = tmp_path / 'validation-only.pt'
    weights.write_bytes(b'boundary-test-not-a-real-model')
    onnx = tmp_path / 'not-training.onnx'
    onnx.write_bytes(b'not-a-model')
    dataset = tmp_path / 'data.yaml'
    dataset.write_text('names: [defect]\n', encoding='utf-8')
    launches = []
    def boundary(pid, config, checkpoint, parent):
        launches.append((pid, config, checkpoint, parent))
        return dict(id='validation-boundary', status='accepted-by-test-double')
    monkeypatch.setattr(service.training, 'launch', boundary)
    good = dict(model=str(weights), dataset=str(dataset), imgsz=64, device='0')
    for changes, message in [({'model': ''}, '选择初始权重'), ({'model': 'relative.pt'}, '完整路径'),
                             ({'model': str(tmp_path / 'missing.pt')}, '本地 PT'), ({'model': str(onnx)}, '.pt 文件'),
                             ({'dataset': ''}, 'data.yaml'), ({'imgsz': 33}, '32 的倍数')]:
        with pytest.raises((module.ServiceError, ValueError), match=message):
            service.dispatch('training.start', {'config': dict(good, **changes)})
        assert not launches
    result = service.dispatch('training.start', {'config': dict(good, model=' "' + str(weights) + '" ')})
    assert result['task_id'] == 'validation-boundary'
    assert launches[0][0] == current['id'] and launches[0][1]['model'] == str(weights.resolve())
    assert launches[0][1]['allow_download'] is False
    assert service.dispatch('training.list')['items'] == []  # No actual worker or GPU task was created.


def test_starter_uses_existing_import_annotations_and_grouped_dataset(service):
    assets = SERVICE_FILE.parents[2] / 'AIExamples/starter-v1'
    before = {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in assets.iterdir() if p.is_file()}
    prepared = service.dispatch('examples.starter.prepare', {})
    pid = prepared['project']['id']
    service.dispatch('context.select', {'project_id': pid})
    images = service.dispatch('images.list')['items']
    assert len(images) == 12 and sum(i['annotation_count'] for i in images) == 40
    assert len(service.dispatch('images.list', {'filter': 'normal'})['items']) == 2
    assert len(service.dispatch('classes.list')['items']) == 4
    assert all(Path(i['source_path']).resolve().is_relative_to(service.data_home) for i in images)
    reused = service.dispatch('examples.starter.prepare', {})
    assert reused['reused'] and reused['project']['id'] == pid
    assert len(service.dispatch('images.list')['items']) == 12
    task = wait_task(service, service.dispatch('dataset.build', {'options': dict(prepared['recommended'], tiled=True)}))
    generated = Path(task['result']['path'])
    metadata = json.loads((generated / 'run.json').read_text(encoding='utf-8'))
    source_by_id = {s['source_id']: s for s in metadata['sources']}
    splits = {}
    for image in images:
        splits.setdefault(image['batch'], set()).add(source_by_id[image['id']]['split'])
    assert len(splits) == 6 and all(len(values) == 1 for values in splits.values())
    assert {s['split'] for s in metadata['sources']} == {'train', 'val', 'test'}
    assert (generated / 'data.yaml').is_file()
    validation = wait_task(service, service.dispatch('dataset.validate', {'path': str(generated)}))
    assert validation['result']['valid']
    fresh = service.dispatch('examples.starter.prepare', {'fresh': True})
    assert fresh['project']['id'] != pid and len(service.dispatch('projects.list')['items']) == 2
    assert service.dispatch('training.list')['items'] == []
    assert before == {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in assets.iterdir() if p.is_file()}


def test_resume_keeps_original_checkpoint_rules_without_launching_worker(service, tmp_path, monkeypatch):
    import torch
    current = project(service)
    checkpoint = tmp_path / 'last.pt'
    # Metadata-only CPU fixture exercises the original validate_resume; it is not a trained model.
    torch.save(dict(epoch=0, optimizer={}, train_args=dict(epochs=2)), checkpoint)
    dataset = tmp_path / 'data.yaml'
    dataset.write_text('names: [defect]\n', encoding='utf-8')
    record = dict(id='stopped-task', project_id=current['id'], last_pt=str(checkpoint),
                  config=dict(dataset=str(dataset), model='original-selection.pt', imgsz=64, epochs=2, device='0'))
    monkeypatch.setattr(service.training.repository, 'get', lambda task_id: record)
    launches = []
    def boundary(pid, config, selected_checkpoint, parent):
        launches.append((pid, config, selected_checkpoint, parent))
        return dict(id='resume-boundary', status='accepted-by-test-double')
    monkeypatch.setattr(service.training, 'launch', boundary)
    response = service.dispatch('training.resume', dict(task_id='stopped-task'))
    assert response['task_id'] == 'resume-boundary'
    assert launches[0][2:] == (str(checkpoint), 'stopped-task')
    assert launches[0][1]['dataset'] == str(dataset) and launches[0][1]['model'] == str(checkpoint)
    torch.save(dict(epoch=-1, optimizer=None, train_args=dict(epochs=2)), checkpoint)
    with pytest.raises(ValueError, match='不能 resume'):
        service.dispatch('training.resume', dict(task_id='stopped-task'))
    assert len(launches) == 1 and not torch.cuda.is_initialized()


def test_annotation_journal_survives_database_failure_and_recovers_on_project_open(service, tmp_path, monkeypatch):
    current = project(service)
    image = import_images(service, tmp_path / 'journal-source', count=1)[0]
    original_write = service.store.replace_annotations
    def unavailable(*_args, **_kwargs):
        raise OSError('controlled database write failure')
    monkeypatch.setattr(service.store, 'replace_annotations', unavailable)
    with pytest.raises(OSError, match='controlled database'):
        service.dispatch('annotations.save', {'image_id': image['id'], 'boxes': [dict(
            id='recoverable-box', class_id=0, x=2, y=3, w=10, h=11)], 'reviewed': True})
    journal = service.recovery_folder(current['id']) / (image['id'] + '.json')
    assert journal.is_file()
    monkeypatch.setattr(service.store, 'replace_annotations', original_write)
    opened = service.dispatch('context.select', {'project_id': current['id']})
    assert opened['recovered_annotations'] == 1 and not journal.exists()
    restored = service.dispatch('annotations.get', {'image_id': image['id']})
    assert restored['reviewed'] and restored['boxes'][0]['id'] == 'recoverable-box'

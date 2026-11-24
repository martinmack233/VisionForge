"""Thin orchestration of the original importer and existing public service operations."""
from pathlib import Path
import json
import shutil
import uuid


def prepare_starter(service, fresh, write_json):
    from core.importer import ImageImporter
    # The service and these resources are published together; never search the user's models/data.
    assets = Path(__file__).resolve().parents[2] / 'AIExamples/starter-v1'
    manifest = json.loads((assets / 'manifest.json').read_text(encoding='utf-8'))
    if manifest.get('version') != 1 or manifest.get('id') != 'visionstudio-ai-starter-v1':
        raise ValueError('AI示例资源版本无效，请重新解压完整发布包。')
    index_file = service.data_home / 'config/visionstudio-starter.json'
    index = service._read_json(index_file, {})
    previous = index.get('project_id')
    if previous and not fresh and service.store.get_project(previous):
        if index.get('status') != 'completed':
            raise ValueError('上次示例准备未完成。请检查独立示例项目，或明确“新建一份独立示例”。')
        return dict(project=service.store.get_project(previous), reused=True, image_count=len(manifest['images']),
                    recommended=manifest['recommended'], message='已打开已有AI示例，没有重复导入。')
    for entry in manifest['images']:
        file = entry['file']
        if Path(file).name != file or service_digest(assets / file) != entry['sha256']:
            raise ValueError('AI示例图像身份校验失败。')
    project = service.dispatch('projects.create', dict(name='AI入门合成示例 · ' + uuid.uuid4().hex[:6]))['project']
    pid = project['id']
    write_json(index_file, dict(version=1, project_id=pid, status='preparing'))
    try:
        source = service.store.paths.absolute(project['data_dir']) / 'starter-source'
        if not source.resolve().is_relative_to(service.data_home):
            raise ValueError('AI示例输出必须位于所选用户数据根。')
        source.mkdir(parents=True, exist_ok=False)
        for entry in manifest['images']: shutil.copy2(assets / entry['file'], source / entry['file'])
        shutil.copy2(assets / 'manifest.json', source / 'manifest.json')
        for cls in manifest['classes']:
            service.dispatch('classes.save', dict(project_id=pid, class_id=cls['id'], name=cls['name'], color=cls['color']))
        imported = ImageImporter(service.store).import_paths(pid, [str(source / entry['file']) for entry in manifest['images']], False)
        if imported.cancelled or imported.errors or imported.imported != len(manifest['images']):
            raise ValueError('示例导入未完整完成：' + str(imported))
        rows = {row['filename']: row for row in service.store.list_images(pid, 1000, 0)}
        for entry in manifest['images']:
            row = rows[entry['file']]
            boxes = [dict(box, id=uuid.uuid5(uuid.NAMESPACE_URL, pid + '/' + entry['file'] + '/' + str(n)).hex) for n, box in enumerate(entry['boxes'])]
            service.dispatch('annotations.save', dict(project_id=pid, image_id=row['id'], boxes=boxes, reviewed=True))
            service.dispatch('images.batch', dict(project_id=pid, image_ids=[row['id']], batch=entry['batch']))
        write_json(index_file, dict(version=1, project_id=pid, status='completed', asset_id=manifest['id']))
        return dict(project=project, reused=False, image_count=len(rows), recommended=manifest['recommended'],
                    message='已在用户数据目录创建独立示例，包含12张原图、6个批次、40个标注及2张正常样本；尚未生成数据集或训练。')
    except BaseException:
        write_json(index_file, dict(version=1, project_id=pid, status='failed'))
        raise


def service_digest(path):
    import hashlib
    with Path(path).open('rb') as stream: return hashlib.file_digest(stream, 'sha256').hexdigest()



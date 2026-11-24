"""Audited-source extraction and diagnostics. No pip, downloads, model loading or training.

The generated component is for explicit local use until source/engine redistribution
rights have been confirmed. Original YOLOStudio files are never modified.
"""
from __future__ import annotations
import argparse
import ast
import contextlib
import hashlib
import importlib.metadata
import json
import os
from pathlib import Path
import shutil
import sys
import uuid


def sha(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def read_json(path):
    if Path(path).stat().st_size > 1024 * 1024:
        raise ValueError('Component manifest exceeds 1 MiB.')
    return json.loads(Path(path).read_text(encoding='utf-8'))


def validate(root):
    root = Path(root).resolve(strict=True)
    manifest = read_json(root / 'component.json')
    if (manifest.get('component'), manifest.get('component_version'), manifest.get('protocol_version')) != ('visionstudio.yolo-detection', '1.0.0', 1):
        raise ValueError('Incompatible detection component/protocol; configure the supported component explicitly.')
    if manifest.get('supported_backends') != ['onnx-cpu']:
        raise ValueError('Only the fixed ONNX CPU contract is supported.')
    files = manifest.get('files', {})
    if not files or len(files) > 100:
        raise ValueError('Invalid component file inventory.')
    for relative, digest in files.items():
        path = root / relative
        if Path(relative).is_absolute() or '..' in Path(relative).parts or not path.resolve().is_relative_to(root) or path.is_symlink():
            raise ValueError('Component inventory contains an unsafe path.')
        if not path.is_file() or sha(path) != digest:
            raise ValueError('Component file missing or changed: ' + relative)
    required = ['core/inference/backend.py', 'core/inference/service.py', 'core/inference/config.py']
    if not all(name in files for name in required):
        raise ValueError('Required fixed inference modules are absent.')
    actual_code = {p.relative_to(root).as_posix() for p in root.rglob('*.py')}
    if actual_code != set(files) or any(root.rglob('*.pyc')):
        raise ValueError('Unexpected Python code/cache in the fixed detection component.')
    return manifest


def environment(manifest):
    if f'{sys.version_info.major}.{sys.version_info.minor}' != manifest['python_minor']:
        raise ValueError('Python version incompatible; verified minor is ' + manifest['python_minor'])
    versions = {}
    for name, expected in manifest['dependencies'].items():
        try:
            actual = importlib.metadata.version(name)
        except importlib.metadata.PackageNotFoundError as exc:
            raise ValueError('Missing detection dependency: ' + name + '==' + expected) from exc
        versions[name] = actual
        if actual != expected:
            raise ValueError(f'Incompatible dependency {name}: expected {expected}, found {actual}. No environment is modified.')
    return versions


def extract(path, names, imports):
    """Preserve selected definitions verbatim, including decorators and docstrings."""
    text = Path(path).read_text(encoding='utf-8-sig')
    lines = text.splitlines(keepends=True)
    nodes = {node.name: node for node in ast.parse(text).body if isinstance(node, (ast.FunctionDef, ast.ClassDef))}
    parts = []
    for name in names:
        node = nodes[name]
        first = min([node.lineno] + [d.lineno for d in node.decorator_list])
        parts.append(''.join(lines[first - 1:node.end_lineno]))
    return '# Extracted verbatim from hash-pinned YOLOStudio; see component.json.\n' + imports + '\n\n' + '\n\n'.join(parts) + '\n'


def install(source, destination, recipe, acknowledge):
    if not acknowledge:
        raise ValueError('Explicit local-use/source-rights acknowledgement is required. This does not grant redistribution rights.')
    source = Path(source).resolve(strict=True)
    destination = Path(destination).absolute()
    if destination.exists() or destination.is_relative_to(source):
        raise ValueError('Choose a NEW component directory outside the original YOLOStudio.')
    recipe = read_json(recipe)
    for relative, expected in recipe['source_files'].items():
        if sha(source / relative) != expected:
            raise ValueError('Source differs from the audited recipe: ' + relative + '; no silent extraction from an unknown version.')
    environment(recipe)
    destination.parent.mkdir(parents=True, exist_ok=True)
    staging = destination.parent / ('.component-' + uuid.uuid4().hex)
    staging.mkdir()
    try:
        for name in ['core', 'core/inference', 'core/training']:
            (staging / name).mkdir(exist_ok=True)
            (staging / name / '__init__.py').write_text('', encoding='utf-8')
        for relative in ['core/inference/backend.py', 'core/inference/config.py', 'core/inference/geometry.py', 'core/preprocessing.py', 'core/imaging.py', 'core/annotations.py']:
            shutil.copyfile(source / relative, staging / relative)
        selected = {
            'core/inference/service.py': (['detect_raster'], 'import time\nfrom pathlib import Path\nfrom core.annotations import tile_grid, restore_box\nfrom core.preprocessing import RasterSource, PreprocessConfig\nfrom core.inference.config import quality\nfrom core.inference.geometry import merge_detections'),
            'core/training/config.py': (['normalize_device'], 'import re'),
            'core/training/dataset.py': (['digest'], 'import hashlib\nfrom pathlib import Path'),
            'core/training/processes.py': (['GpuLease'], 'import hashlib\nimport os\nfrom pathlib import Path'),
        }
        for relative, (names, imports) in selected.items():
            (staging / relative).write_text(extract(source / relative, names, imports), encoding='utf-8')
        manifest = dict(recipe, files={p.relative_to(staging).as_posix(): sha(p) for p in sorted(staging.rglob('*.py'))},
                        source_identity='SHA256 per original file; no database, weights, Qt or training worker included',
                        extraction='Verbatim AST definitions for detect_raster/digest/normalize_device/GpuLease; other modules byte-for-byte')
        (staging / 'component.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding='utf-8')
        validate(staging)
        staging.rename(destination)
        return dict(success=True, component_version='1.0.0', protocol_version=1, local_install=True, redistributable=False)
    finally:
        if staging.exists():
            shutil.rmtree(staging)  # Only this invocation's GUID directory.


def probe(root):
    manifest = validate(root)
    versions = environment(manifest)
    sys.path.insert(0, str(Path(root).resolve()))
    # Import only; no detector/session/model, database, device or GPU initialization.
    from core.inference.backend import DetectorBackend
    from core.inference.service import detect_raster
    import onnxruntime
    return dict(success=True, component_version=manifest['component_version'], protocol_version=1,
                python=sys.version.split()[0], dependencies=versions, supported_backends=['onnx-cpu'],
                available_providers=onnxruntime.get_available_providers(), configured_device='cpu',
                gpu_initialized=False, model_loaded=False, training_available=False)


def model_check(root, model):
    manifest = validate(root); environment(manifest)
    sys.path.insert(0, str(Path(root).resolve()))
    from core.inference.backend import DetectorBackend
    from core.inference.config import DetectConfig
    path = Path(model).resolve(strict=True)
    deployment = read_json(path.with_name('deployment.json'))
    config = DetectConfig.from_dict(dict(model=str(path), backend='onnx', device='cpu',
        imgsz=deployment['input']['height'], batch=1, preprocess=deployment['preprocessing']['config']))
    with DetectorBackend(config, owner='configuration-check-cpu') as backend:
        actual = backend.session.get_inputs()[0]
        shape = actual.shape
        expected = deployment['input']
        if len(shape) != 4 or isinstance(shape[1], int) and shape[1] != 3 or actual.type != expected['dtype']:
            raise ValueError('Actual ONNX input shape/channel/type conflicts with deployment metadata.')
        actual_dynamic = any(not isinstance(d, int) for d in shape)
        if bool(expected['dynamic']) != actual_dynamic or any(isinstance(shape[i], int) and shape[i] != expected[key] for i, key in ((2, 'height'), (3, 'width'))):
            raise ValueError('Actual ONNX dynamic/fixed dimensions conflict with deployment metadata.')
        if not expected['dynamic'] and (shape[2:] != [expected['height'], expected['width']] or not isinstance(shape[0], int)):
            raise ValueError('Static deployment metadata does not match actual ONNX input dimensions/batch.')
        if expected.get('batch') is not None and isinstance(shape[0], int) and expected['batch'] != shape[0]:
            raise ValueError('Actual ONNX batch conflicts with deployment metadata.')
        return dict(success=True, model_sha256=sha(path), input_shape=shape, dtype=actual.type,
                    provider=backend.provider, inference_executed=False, gpu_initialized=False)


def main():
    parser = argparse.ArgumentParser()
    commands = parser.add_subparsers(dest='command', required=True)
    create = commands.add_parser('install')
    for key in ['source', 'destination', 'recipe']:
        create.add_argument('--' + key, required=True)
    create.add_argument('--ack-local-use', action='store_true')
    check = commands.add_parser('probe'); check.add_argument('--root', required=True)
    model = commands.add_parser('model-check'); model.add_argument('--root', required=True); model.add_argument('--model', required=True)
    args = parser.parse_args()
    try:
        with contextlib.redirect_stdout(sys.stderr):
            result = install(args.source, args.destination, args.recipe, args.ack_local_use) if args.command == 'install' else model_check(args.root, args.model) if args.command == 'model-check' else probe(args.root)
        print(json.dumps(result, ensure_ascii=False), flush=True)
        return 0
    except Exception as exc:
        print(json.dumps(dict(success=False, error=(type(exc).__name__ + ': ' + str(exc))[:2000]), ensure_ascii=False), flush=True)
        return 1


if __name__ == '__main__':
    sys.exit(main())

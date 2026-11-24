"""Read-only local ONNX comparison. Not packaged with a model; never trains/downloads."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile


def main():
    parser = argparse.ArgumentParser()
    for key in ('source', 'component', 'model', 'image', 'report'):
        parser.add_argument('--' + key, type=Path, required=True)
    a = parser.parse_args()
    model = a.model.resolve(strict=True); image = a.image.resolve(strict=True)
    expected_model = hashlib.sha256(model.read_bytes()).hexdigest()
    deployment = json.loads(model.with_name('deployment.json').read_text(encoding='utf-8'))
    report = dict(passed=False, component_version='1.0.0', protocol=1, backend='onnx-cpu', training_started=False,
                  gpu_used=False, model_redistributed=False, model_sha256=expected_model, cases=[], negative_checks={})
    with tempfile.TemporaryDirectory(prefix='vision-runtime-verify-') as temp:
        work = Path(temp); env = dict(os.environ, YOLO_CONFIG_DIR=str(work/'yolo-settings'), PYTHONDONTWRITEBYTECODE='1', PYTHONIOENCODING='utf-8')
        from PIL import Image
        with Image.open(image) as im: im.convert('RGB').save(work/'input.png')
        def bridge(root, tiled):
            request = dict(schema_version=1, yolo_root=str(root.resolve()), model_path=str(model), image_path=str(work/'input.png'), expected_sha256=expected_model,
                image_size=deployment['input']['height'], confidence=.25, iou=.45, merge_iou=.5, class_ids=[], tiled=tiled,
                tile_width=320, tile_height=320, overlap=.15, batch=1, max_det=300)
            path=work/'request.json';path.write_text(json.dumps(request),encoding='utf-8')
            p=subprocess.run([sys.executable,'-B','-u',str(Path(__file__).resolve().parents[1]/'yolo_bridge/yolo_bridge.py'),str(path)],env=env,cwd=work,capture_output=True,text=True,encoding='utf-8',timeout=90)
            result=json.loads(p.stdout); assert p.returncode==0 and result['success'], (result,p.stderr[-1000:])
            return result
        for tiled in (False,True):
            original=bridge(a.source,tiled); independent=bridge(a.component,tiled)
            for key in ('boxes','provider','tile_count','width','height','model_sha256'):
                assert original[key]==independent[key], (key,original[key],independent[key])
            report['cases'].append(dict(tiled=tiled, tile_count=independent['tile_count'], count=len(independent['boxes']),
                exact_detection_equality=True, actual_forward_ms=independent['inference_ms'], source_forward_ms=original['inference_ms']))
        # Tamper only a private component copy. No source environment/module is changed.
        damaged=work/'damaged-component';shutil.copytree(a.component,damaged)
        script=Path(__file__).with_name('component.py').resolve()
        def rejected():
            p=subprocess.run([sys.executable,'-I','-B',str(script),'probe','--root',str(damaged)],env=env,capture_output=True,text=True,encoding='utf-8',timeout=60)
            result=json.loads(p.stdout);assert p.returncode!=0 and not result['success'];return result['error']
        m=json.loads((damaged/'component.json').read_text(encoding='utf-8'));m['dependencies']['numpy']='0.0.invalid';(damaged/'component.json').write_text(json.dumps(m),encoding='utf-8')
        report['negative_checks']['dependency_mismatch']=rejected()
        m['protocol_version']=99;(damaged/'component.json').write_text(json.dumps(m),encoding='utf-8');report['negative_checks']['protocol_mismatch']=rejected()
        m['protocol_version']=1;m['dependencies']=json.loads((a.component/'component.json').read_text(encoding='utf-8'))['dependencies'];(damaged/'component.json').write_text(json.dumps(m),encoding='utf-8')
        with (damaged/'core/inference/backend.py').open('a',encoding='utf-8') as f:f.write('\n# changed\n')
        report['negative_checks']['component_hash_changed']=rejected()
        mismatch=work/'mismatched-model';mismatch.mkdir();shutil.copyfile(model,mismatch/model.name)
        incompatible=json.loads(json.dumps(deployment));incompatible['input']['height']=incompatible['input']['width']=64
        (mismatch/'deployment.json').write_text(json.dumps(incompatible),encoding='utf-8')
        p=subprocess.run([sys.executable,'-I','-B',str(script),'model-check','--root',str(a.component.resolve()),'--model',str(mismatch/model.name)],env=env,capture_output=True,text=True,encoding='utf-8',timeout=60)
        error=json.loads(p.stdout);assert p.returncode!=0 and not error['success'];report['negative_checks']['actual_onnx_dimensions_mismatch']=error['error']
        assert hashlib.sha256(model.read_bytes()).hexdigest()==expected_model
    report['passed']=True;a.report.parent.mkdir(parents=True,exist_ok=True);a.report.write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8');print(json.dumps(report,ensure_ascii=False))


if __name__=='__main__':main()

"""Private stdio protocol 2. One model, sequential requests, no listener or training."""
import argparse
import contextlib
import hashlib
import json
from pathlib import Path
import sys
import time
import uuid
from yolo_bridge import run

class ModelCache:
    def __init__(self): self.model=None; self.key=None; self.loads=0
    def close(self):
        if self.model is not None: self.model.close()
        self.model=None; self.key=None
    def get(self, request, config, factory):
        sidecar=Path(request['model_path']).with_name('deployment.json')
        key=(request['yolo_root'], request['expected_sha256'], hashlib.sha256(sidecar.read_bytes()).hexdigest(),json.dumps(config.to_dict(),sort_keys=True))
        if key!=self.key:
            self.close(); self.model=factory(config,owner='visionstudio-resident-cpu'); self.key=key; self.loads+=1
        return self.model

def main():
    p=argparse.ArgumentParser();p.add_argument('--root',required=True);p.add_argument('--epoch',required=True);a=p.parse_args()
    root=Path(a.root).resolve(strict=True);cache=ModelCache();count=0
    def emit(value):
        text=json.dumps(dict(protocol=2,epoch=a.epoch,**value),ensure_ascii=True,allow_nan=False,separators=(',',':'))
        if len(text)>8*1024*1024:raise ValueError('Response exceeds 8 MiB')
        print(text,flush=True)
    emit(dict(ready=True,pid=__import__('os').getpid(),cache_limit=1))
    try:
        while True:
            line=sys.stdin.buffer.readline(65537)
            if not line:break
            if len(line)>65536:raise ValueError('Request exceeds64 KiB')
            q=json.loads(line);rid=q['request_id']
            if q.get('protocol')!=2 or q.get('epoch')!=a.epoch:raise ValueError('Protocol/epoch mismatch')
            op=q.get('operation')
            if op=='health':emit(dict(request_id=rid,healthy=True,requests=count,model_loads=cache.loads,cached_models=int(cache.model is not None)));continue
            if op=='unload':cache.close();emit(dict(request_id=rid,unloaded=True));continue
            if op=='shutdown':emit(dict(request_id=rid,closed=True));break
            if op!='infer':raise ValueError('Unsupported operation')
            request=q['request'];raw_image=Path(request['image_path'])
            if raw_image.is_symlink() or raw_image.parent.is_symlink():raise ValueError('Symbolic image leases rejected')
            image=raw_image.resolve(strict=True)
            if image.parent.parent!=root or not image.parent.name.startswith('VisionPlatform-Yolo-') or image.name not in ('input.png','input.tif') or image.is_symlink() or image.parent.is_symlink():raise ValueError('Image lease path is not owned by this channel')
            d=request['image_lease'];w,h=d['width'],d['height'];channels=d['channels']
            if type(w)!=int or type(h)!=int or w<=0 or h<=0 or w*h>100_000_000 or channels not in (1,3) or d['source_stride']<w*channels or d['stored_stride']!=w*channels or d['payload_length']!=w*h*channels or d['lease_id']!=rid or d['file_length']!=image.stat().st_size or d['file_length']>300_000_160:raise ValueError('Invalid image capacity/format/lease descriptor')
            if (d['format'],channels) not in (('Mono8',1),('RGB8',3)):raise ValueError('Channel accepts only Mono8/RGB8 stored samples')
            if image.suffix=='.tif':
                import tifffile
                with tifffile.TiffFile(image) as t:
                    page=t.pages[0]
                    if len(t.pages)!=1 or page.imagewidth!=w or page.imagelength!=h or page.samplesperpixel!=channels or int(page.compression)!=1 or str(page.dtype)!='uint8' or tuple(page.dataoffsets)!=(160,) or tuple(page.databytecounts)!=(w*h*channels,) or image.stat().st_size!=160+w*h*channels:raise ValueError('TIFF pixels differ from descriptor')
            else:
                from PIL import Image
                with Image.open(image) as im:
                    if im.size!=(w,h) or len(im.getbands())!=channels:raise ValueError('PNG differs from descriptor')
            try:
                with contextlib.redirect_stdout(sys.stderr):result=run(request,cache)
                count+=1;emit(dict(request_id=rid,result=result,model_loads=cache.loads,cached_models=1))
            except Exception as exc:
                emit(dict(request_id=rid,error=(type(exc).__name__+': '+str(exc))[:2000]));break
    finally:cache.close()

if __name__=='__main__':main()

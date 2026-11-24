from pathlib import Path
import json,statistics,math,hashlib
root=Path(__file__).resolve().parent/'data'
cases=json.loads((root/'cases.json').read_text(encoding='utf-8'))
summary={}
for algorithm,file in [('CvInspect','cvinspect-results.json'),('IndustrialVisionStudio','industrial-results.json')]:
    raw=json.loads((root/file).read_text(encoding='utf-8'))
    rows=[]
    for result,case in zip(raw,cases):
        runs=result['runs'];first=runs[0];times=[r['milliseconds'] for r in runs]
        rows.append(dict(name=result['name'],expected_success=case['expected_success'],success=first['success'],
            position_error=math.hypot(first['x']-case['x'],first['y']-case['y']) if first['success'] else None,
            angle_error=abs(first['angle']-case['angle']) if first['success'] else None,
            scale_error=abs(first['scale']-case['scale']) if first['success'] else None,
            score=first['score'],median_ms=statistics.median(times),min_ms=min(times),max_ms=max(times),
            peak_working_set_mib=max(r['peak_working_set'] for r in runs)/1024**2,
            working_set_mib=statistics.median(r['working_set_after'] for r in runs)/1024**2,
            repeat_identical=len(set(tuple(r[k] for k in ['success','score','x','y','angle','scale']) for r in runs))==1))
    summary[algorithm]=rows
(root/'summary.json').write_text(json.dumps(summary,indent=2),encoding='utf-8')
(root/'input-sha256.json').write_text(json.dumps({p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(root.glob('*.png'))},indent=2))
for algorithm,rows in summary.items():
    print(algorithm)
    for r in rows:print(r['name'],f"{r['median_ms']:.3f}",r['position_error'],r['angle_error'],r['scale_error'],f"{r['working_set_mib']:.2f}",f"{r['peak_working_set_mib']:.2f}")

"""Read-only execution of old/new self-contained hosts on identical packaged synthetic cases."""
from pathlib import Path
import json, math, subprocess, sys

root=Path(__file__).resolve().parent.parent
evidence=root/'test-results/release-prep/migration-comparison'; evidence.mkdir(parents=True,exist_ok=True)
old=root/'artifacts/VisionStudio-Collections-20261116-win-x64'
version=__import__('xml.etree.ElementTree',fromlist=['']).parse(root/'build/Version.props').find('.//VisionProductVersion').text
new=root/'artifacts'/f'VisionStudio-{version}-win-x64'
cases=['collection-multi-ocr','collection-blob-measure','basic','virtual-camera']
ignored={'RunId','IterationRunId','FrameId','InspectionId','Timestamp','ExecutionTime','ExecutionMilliseconds','ForwardMilliseconds','Milliseconds','Key','TaskId','ModelSha256','RetainedBytes'}
def strip(value):
    if isinstance(value,dict): return {k:strip(v) for k,v in value.items() if k not in ignored and k not in {'Summary','Label','Message','Description'}}
    if isinstance(value,list): return [strip(v) for v in value]
    return value
def snapshot(response):
    result={}
    for node in response['Payload']['Nodes']:
        values={}
        for port,output in node['Outputs'].items():
            if port in {'Image','ProcessedImage','Debug','Summary','Message','Display','Frame','FrameInfo','Telemetry'} or 'ImageFrame' in output.get('Type',''):continue
            value=output.get('Value')
            if output.get('Type') in {'VisionCollection','IterationBatch'}: values[port]=strip(value)
            elif output.get('Type') in {'TemplateMatchResult','InspectionDecision','Pose2D','BlobResults','InspectionReport','RotatedRectRoi','Int32','Boolean','Double','int','bool','double'}:values[port]=strip(value)
        result[node['NodeId']]={'status':node['Status'],'outputs':values}
    return result
def run(base,label,case):
    # Both hosts read the same old packaged project/resources; no resources are rewritten.
    path=old/'Examples'/case/'project.json'
    proc=subprocess.run([str(base/'VisionPlatform.RuntimeHost.exe'),'--project',str(path),'--once'],cwd=base,capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=60)
    (evidence/f'{label}-{case}.log').write_text(proc.stdout+'\n'+proc.stderr,encoding='utf-8')
    assert proc.returncode==0,(label,case,proc.stdout,proc.stderr)
    response=json.loads(proc.stdout[proc.stdout.index('{'):]);assert response['Success'] and response['Payload']['Success']
    return snapshot(response)
if '--baseline-only' in sys.argv:
    snapshots={case:run(old,'net8',case) for case in cases};(evidence/'net8.json').write_text(json.dumps(snapshots,ensure_ascii=False,indent=2),encoding='utf-8');print('Captured .NET8 actual baseline');sys.exit(0)
before=strip(json.loads((evidence/'net8.json').read_text(encoding='utf-8')));after={case:run(new,'net10',case) for case in cases}
differences=[]; numeric=[]
def compare(a,b,path):
    if isinstance(a,(int,float)) and not isinstance(a,bool) and isinstance(b,(int,float)):
        numeric.append(abs(a-b))
        if not math.isclose(a,b,rel_tol=1e-9,abs_tol=1e-6):differences.append(path+f': {a} != {b}')
    elif type(a)!=type(b):differences.append(path+': type differs')
    elif isinstance(a,dict):
        if a.keys()!=b.keys():differences.append(path+': keys differ')
        for k in a.keys()&b.keys():compare(a[k],b[k],path+'.'+k)
    elif isinstance(a,list):
        if len(a)!=len(b):differences.append(path+': count differs')
        for i,(x,y) in enumerate(zip(a,b)):compare(x,y,path+f'[{i}]')
    elif a!=b:differences.append(path+f': {a!r} != {b!r}')
compare(before,after,'cases')
report=dict(passed=not differences,cases=cases,absoluteTolerance=1e-6,relativeTolerance=1e-9,maxObservedNumericDifference=max(numeric,default=0),numericValuesCompared=len(numeric),differences=differences,
            identityAndTimingExcluded=True,inputProjectDirectory=str(old/'Examples'),realCameraUsed=False,trainingStarted=False)
(evidence/'summary.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8');print(json.dumps(report,ensure_ascii=False,indent=2));assert not differences

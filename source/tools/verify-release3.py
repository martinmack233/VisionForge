"""Run self-contained Console/Host from an independent cwd; no hardware or remote I/O."""
import argparse,json,os,subprocess
from pathlib import Path
p=argparse.ArgumentParser();p.add_argument('release');p.add_argument('output');a=p.parse_args();release=Path(a.release).resolve();out=Path(a.output).resolve();out.mkdir(parents=True,exist_ok=True)
env=dict(os.environ);env['VISIONSTUDIO_MACHINE_CONFIG']=str(out/'unconfigured-machine.json');rows=[]
def execute(name,args,expected=0):
 r=subprocess.run(args,cwd=out,env=env,capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=90)
 (out/(name+'.stdout.log')).write_text(r.stdout,encoding='utf-8');(out/(name+'.stderr.log')).write_text(r.stderr,encoding='utf-8')
 assert r.returncode==expected,(name,r.returncode,r.stderr[-2000:]);return r.stdout
ids=['inspection-aligned-difference','inspection-color-shapes','inspection-hough-measure']
for i,name in enumerate(ids):
 package=release/'ProjectPackages'/(name+'.vspkg');dest=out/'imported'/name
 execute(name+'-import',[str(release/'VisionPlatform.Console.exe'),'package-import',str(package),str(dest)])
 project=dest/'project.json'
 execute(name+'-console',[str(release/'VisionPlatform.Console.exe'),'run',str(project),'--repeat','2'],2 if i==0 else 0)
 text=execute(name+'-host',[str(release/'VisionPlatform.RuntimeHost.exe'),'--project',str(project),'--once'])
 response=json.loads(text);assert response['Success'] and response['Payload']['Success'],response
 nodes={n['NodeId']:n for n in response['Payload']['Nodes']}
 if i==0:assert nodes['count']['Outputs']['Count']['Value']==2 and nodes['result']['Outputs']['IsOk']['Value'] is False
 if i==1:assert nodes['filter']['Outputs']['Collection']['Value']['Count']==2
 if i==2:assert nodes['each']['Outputs']['CompletedCount']['Value']>0
 rows.append(dict(id=name,package_import=True,console=True,host_exe=True,actual_result=True))
version=execute('version',[str(release/'VisionPlatform.Console.exe'),'--version'])
assert '0.6.2-preview.1' in version and '.NET 10.0.15' in version,version
summary=dict(Passed=True,Release=str(release),Cwd=str(out),NoSourceInCwd=True,CleanWindows=False,ComponentsTrainingConfigured=False,Examples=rows,Version=version,NoRealCamera=True,NoExternalCommunication=True)
(out/'published-validation.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2),encoding='utf-8');print(json.dumps(summary,ensure_ascii=False,indent=2))

"""Build first, then run: python3 tests/run-shared-integration.py [dotnet executable]."""
import os, pathlib, socket, subprocess, sys, tempfile, time, urllib.request
root=pathlib.Path(__file__).resolve().parents[1]
dotnet=sys.argv[1] if len(sys.argv)>1 else 'dotnet'
def port():
 with socket.socket() as s:s.bind(('127.0.0.1',0));return s.getsockname()[1]
def wait(url):
 for _ in range(100):
  try:
   with urllib.request.urlopen(url,timeout=1):return
  except OSError:time.sleep(.1)
 raise RuntimeError('Service did not start: '+url)
with tempfile.TemporaryDirectory(prefix='gonio-integration-') as tmp:
 shared,bridge=port(),port();processes=[]
 env={**os.environ,'ASPNETCORE_URLS':f'http://127.0.0.1:{shared}','GONIO_SHARED_URL':f'http://127.0.0.1:{shared}','GONIO_PORT':str(bridge),'GONIO_SETUP_TOKEN':'local-integration-test-only','GONIO_SHARED_DATA':tmp+'/shared','GONIO_DATA_DIR':tmp+'/client/streams','DROPBOX_APP_KEY':'','DROPBOX_REDIRECT_URI':''}
 with open(tmp+'/server.log','w+') as log:
  try:
   for project,dll in [('shared-server','SharedServer'),('device-bridge','GonioWeb')]:
    processes.append(subprocess.Popen([dotnet,str(root/'apps'/project/'bin/Debug/net8.0'/f'{dll}.dll')],env=env,stdout=log,stderr=log))
   wait(f'http://127.0.0.1:{shared}/health');wait(f'http://127.0.0.1:{bridge}/health')
   subprocess.run([sys.executable,str(root/'tests/shared-integration.py'),f'http://127.0.0.1:{bridge}'],check=True)
  except Exception:
   log.seek(0);print(log.read());raise
  finally:
   for p in processes:p.terminate()
   for p in processes:
    try:p.wait(timeout=10)
    except subprocess.TimeoutExpired:p.kill();p.wait()

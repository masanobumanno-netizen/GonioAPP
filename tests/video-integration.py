"""Run only against a fresh synthetic test server; creates a test administrator."""
import json,uuid,urllib.request,time,pathlib,math,sys
base=sys.argv[1]
source=pathlib.Path(sys.argv[2])
output=pathlib.Path(sys.argv[3])
def req(path,data=None,token=None,body=None,ctype=None,method=None):
 headers={}
 if token:headers['Authorization']='Bearer '+token
 if data is not None:body=json.dumps(data).encode();ctype='application/json'
 if ctype:headers['Content-Type']=ctype
 with urllib.request.urlopen(urllib.request.Request(base+path,data=body,headers=headers,method=method),timeout=20) as r:return json.load(r) if 'application/json' in r.headers.get('Content-Type','') else r.read()
req('/api/auth/setup',{'username':'video','displayName':'Video test','password':'video-test-pass','setupToken':'local-integration-test-only'})
token=req('/api/auth/login',{'username':'video','password':'video-test-pass'})['token'];sid=str(uuid.uuid4());meta={'id':sid,'subjectId':'VIDEO-TEST','side':'right','condition':'video','startedAt':'2026-09-28T00:00:00Z','status':'complete','mode':'normal','source':'mock','samples':[{'sample_index':i,'elapsed_ms':i*10,'raw_angle_deg':45+i*.2,'smoothed_angle_deg':45+i*.2} for i in range(200)]}
b='boundary'+uuid.uuid4().hex;parts=[]
for name,data,mime in [('session',json.dumps(meta).encode(),'application/json'),('csv',b'elapsed_ms,raw_angle_deg\n0,45\n','text/csv'),('video',source.read_bytes(),'video/webm')]:parts.append(f'--{b}\r\nContent-Disposition: form-data; name="{name}"; filename="{name}"\r\nContent-Type: {mime}\r\n\r\n'.encode()+data+b'\r\n')
body=b''.join(parts)+f'--{b}--\r\n'.encode();req('/api/sessions/'+sid,token=token,body=body,ctype='multipart/form-data; boundary='+b,method='PUT')
for i in range(30):
 status=req('/api/sessions/'+sid+'/status',token=token)
 if status['analysis'] in ['complete','error']:
  print(status)
  if status['analysis']=='complete':output.write_bytes(req('/api/sessions/'+sid+'/files/analysis',token=token))
  break
 time.sleep(1)
assert status['analysis']=='complete'

print("PASS: synthetic camera + angle graph MP4", output)

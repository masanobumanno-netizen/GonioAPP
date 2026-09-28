"""Uses isolated test services only. Creates synthetic accounts/data; no Dropbox traffic."""
import urllib.request, urllib.error, json, uuid, sys, time
base=sys.argv[1] if len(sys.argv)>1 else 'http://127.0.0.1:4176'
def req(path,method='GET',data=None,token=None,body=None,ctype=None,expected=200):
 headers={}
 if token:headers['Authorization']='Bearer '+token
 if data is not None:body=json.dumps(data).encode();ctype='application/json'
 if ctype:headers['Content-Type']=ctype
 try:
  r=urllib.request.urlopen(urllib.request.Request(base+path,data=body,method=method,headers=headers));status=r.status;raw=r.read();ct=r.headers.get('Content-Type','')
 except urllib.error.HTTPError as e:status=e.code;raw=e.read();ct=e.headers.get('Content-Type','')
 assert status==expected,(path,status,raw[:500])
 return json.loads(raw) if 'application/json' in ct and raw else raw
assert req('/api/auth/status')['setupRequired'] is True,'Use a fresh test database'
req('/api/auth/setup','POST',{'username':'admin','displayName':'Test Admin','password':'test-password-123','setupToken':'bad'},expected=403)
req('/api/auth/setup','POST',{'username':'admin','displayName':'Test Admin','password':'test-password-123','setupToken':'local-integration-test-only'})
req('/api/auth/setup','POST',{'username':'other','password':'test-password-123','setupToken':'local-integration-test-only'},expected=409)
a=req('/api/auth/login','POST',{'username':'admin','password':'test-password-123'});admin=a['token']
req('/api/admin/users','POST',{'username':'researcher','displayName':'Researcher','role':'user','active':True,'password':'research-pass-123'},admin)
b=req('/api/auth/login','POST',{'username':'researcher','password':'research-pass-123'});user=b['token']
req('/api/admin/users',token=user,expected=403)
req('/api/settings',token=user)
req('/api/admin/settings','PUT',{'duration':3,'window':20,'axis':'fixed','min':-10,'max':160},user,expected=403)
req('/api/admin/settings','PUT',{'duration':3,'window':20,'axis':'fixed','min':-10,'max':160},admin)
assert req('/api/settings',token=user)['duration']==3
req('/api/admin/users/'+a['user']['id'],'PUT',{'username':'admin','displayName':'Admin','role':'user','active':True},admin,expected=409)
sid=str(uuid.uuid4());session={'id':sid,'subjectId':'TEST-ONLY','side':'right','condition':'integration','operator':'forged','operatorId':a['user']['id'],'startedAt':'2026-09-28T00:00:00Z','status':'complete','source':'mock','mode':'normal','samples':[{'sample_index':0,'elapsed_ms':0,'raw_angle_deg':45,'smoothed_angle_deg':45}]}
def multipart(files):
 boundary='gonio'+uuid.uuid4().hex;parts=[]
 for name,content,mime in files:
  parts.append(f'--{boundary}\r\nContent-Disposition: form-data; name="{name}"; filename="{name}"\r\nContent-Type: {mime}\r\n\r\n'.encode()+content+b'\r\n')
 return b''.join(parts)+f'--{boundary}--\r\n'.encode(),'multipart/form-data; boundary='+boundary
body,ctype=multipart([('session',json.dumps(session).encode(),'application/json'),('csv',b'elapsed_ms,raw_angle_deg\n0,45\n','text/csv')])
req('/local/sessions/'+sid,'PUT',token=user,body=body,ctype=ctype)
req('/local/outbox/retry','POST',token=user)
rows=req('/api/sessions',token=admin);assert any(s['id']==sid for s in rows)
detail=req('/api/sessions/'+sid,token=admin);assert detail['operatorId']==b['user']['id'] and detail['operator']=='Researcher','Server must assign operator'
assert req('/api/sessions/'+sid+'/status',token=user)['analysis']=='no-video'
assert req('/api/sessions/'+sid+'/files/csv',token=admin).startswith(b'elapsed_ms')
req('/api/sessions/'+sid,'PUT',token=user,body=body,ctype=ctype)
req('/api/sessions/'+sid,'PUT',token=admin,body=body,ctype=ctype,expected=409)
assert any(s['status']=='complete' for s in req('/local/outbox',token=user))
assert any(s['id']==sid for s in req('/api/sessions?date=2026-09-27&offset=-240',token=user))
req('/api/admin/users','POST',{'username':'another','displayName':'Another','role':'user','active':True,'password':'another-pass-123'},admin)
other=req('/api/auth/login','POST',{'username':'another','password':'another-pass-123'})['token']
req('/api/sessions/'+sid+'/retry','POST',token=other,expected=403)
req('/api/admin/sessions/'+sid,'DELETE',token=user,expected=403)
req('/api/admin/sessions/'+sid,'DELETE',token=admin)
req('/api/sessions/'+sid,token=admin,expected=404)
assert sid in req('/api/sessions/deleted',token=user)
boundary='stimulus'+uuid.uuid4().hex
stim_body=(f'--{boundary}\r\nContent-Disposition: form-data; name="name"\r\n\r\nSynthetic stimulus\r\n--{boundary}\r\nContent-Disposition: form-data; name="video"; filename="test.webm"\r\nContent-Type: video/webm\r\n\r\nTEST-ONLY\r\n--{boundary}--\r\n').encode()
stim_type='multipart/form-data; boundary='+boundary
req('/api/admin/stimuli','POST',token=user,body=stim_body,ctype=stim_type,expected=403)
stim=req('/api/admin/stimuli','POST',token=admin,body=stim_body,ctype=stim_type)['id']
assert req('/api/stimuli/'+stim+'/video',token=user)==b'TEST-ONLY'
req('/api/admin/stimuli/'+stim,'PUT',{'name':'Renamed'},admin)
assert any(s['id']==stim and s['name']=='Renamed' for s in req('/api/stimuli',token=user))
req('/api/admin/stimuli/'+stim,'DELETE',token=admin)
req('/api/stimuli/'+stim+'/video',token=user,expected=404)
req('/api/auth/logout','POST',token=user)
req('/api/settings',token=user,expected=401)
print('PASS: setup, login, server roles, last-admin protection, shared settings, disk outbox, shared history, operator integrity, idempotency, soft delete, timezone search, retry permissions, stimulus management, logout revocation')

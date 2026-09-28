"""Integration test against an explicitly --test-device bridge. Never contacts hardware."""
import urllib.request, urllib.error, socket, base64, os, struct, json, sys, time
port=int(sys.argv[1]); host=f'http://127.0.0.1:{port}'
health=json.load(urllib.request.urlopen(host+'/health'))
assert health['testMode'] is True, 'Refusing to test a real-device server'
models=json.load(urllib.request.urlopen(host+'/device/models'))
assert any(m['model']=='AIO-160802GY-USB' and m['channels']==8 and m['repeatTimes']==1 for m in models)
assert 'message' in json.load(urllib.request.urlopen(host+'/update/status'))
assert b'Gonio Web' in urllib.request.urlopen(host+'/').read()
assert 'javascript' in urllib.request.urlopen(host+'/apps/web/app.mjs').headers['content-type']
assert json.load(urllib.request.urlopen(host+'/device/list'))['devices'][0]['name']=='TEST000'
class WS:
 def __init__(self,origin=None,query='device=TEST000&ch1=0&ch2=1'):
  self.s=socket.create_connection(('127.0.0.1',port),timeout=5);self.buf=b''
  key=base64.b64encode(os.urandom(16)).decode()
  req=f'GET /measurement/stream?{query} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\nOrigin: {origin or host}\r\n\r\n'
  self.s.sendall(req.encode())
  while b'\r\n\r\n' not in self.buf:self.buf+=self.s.recv(4096)
  self.header,self.buf=self.buf.split(b'\r\n\r\n',1)
 def take(self,n):
  while len(self.buf)<n:
   data=self.s.recv(4096)
   if not data:raise EOFError()
   self.buf+=data
  data,self.buf=self.buf[:n],self.buf[n:];return data
 def receive(self):
  a,b=self.take(2);size=b&127
  if size==126:size=struct.unpack('!H',self.take(2))[0]
  if size==127:size=struct.unpack('!Q',self.take(8))[0]
  mask=self.take(4) if b&128 else None;data=self.take(size)
  if mask:data=bytes(c^mask[i%4] for i,c in enumerate(data))
  return json.loads(data)
 def close(self):
  self.s.sendall(b'\x88\x80'+os.urandom(4));self.s.close()
foreign=WS('https://example.invalid');assert b'403' in foreign.header;foreign.s.close()
w=WS();assert b'101' in w.header
connected=w.receive();assert connected['type']=='connected' and connected['source']=='bridge-test'
other=WS();assert b'409' in other.header;other.s.close()
samples=[]
while len(samples)<20:
 event=w.receive();assert event['type']=='samples',event;samples.extend(event['samples'])
for a,b in zip(samples,samples[1:]):
 assert b['sampleIndex']==a['sampleIndex']+1
 assert b['timestamp']-a['timestamp']==10
 assert b['elapsedMs']-a['elapsedMs']==10
w.close()
# Bounded polling permits cleanup to finish before checking lease release and flushed log.
for _ in range(30):
 if not json.load(urllib.request.urlopen(host+'/device/status'))['connected']:break
 time.sleep(.1)
else:raise AssertionError('device lease not released')
from pathlib import Path
csv=Path(connected['rawLog']).read_text();assert len(csv.splitlines())>=21
bad=WS(query='device=TEST000&ch1=1&ch2=1');assert bad.receive()['type']=='error';bad.close()
print('PASS: HTTP assets, enumeration, origin rejection, exclusive lease, continuous 100Hz frames, disconnect cleanup, raw CSV, invalid config')

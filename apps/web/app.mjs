let connectedModel=null;
import {calculateAngle,calculateCalibration,validateCalibration,generateCsv} from '/packages/measurement-core/index.mjs';
import {save,list as listLocal} from './db.mjs';
import {api,request,setToken,token,queueSession} from './shared.mjs';
let currentUser=null,selectedStimulus=null,analysisUrl=null,remoteVideoUrl=null;
const $=id=>document.getElementById(id);
let mode='normal', calibration={}, collecting=null, running=false, paused=false, saving=false, samples=[], recent=[], stream=null, recorder=null, chunks=[], session=null, result=null, stimulusUrl=null, resultUrl=null, persistChain=Promise.resolve(), lastPersist=0;
let stage='prepare', role='user';
let sensorSource='mock',sensorReady=true,deviceSocket=null,lastDeviceIndex=null,lastDeviceArrival=0,rawLog=null,captureId=null,recordAfter=0;

let preferences={duration:2,window:10,axis:'auto',min:0,max:150};
try {const p=JSON.parse(localStorage.getItem('gonio-preferences')); if(p && [1,2,3,4,5].includes(p.duration) && [10,20,30].includes(p.window) && ['auto','fixed'].includes(p.axis) && Number.isFinite(p.min) && Number.isFinite(p.max) && p.min<p.max)preferences=p;}catch{}
const worker=new Worker('/apps/web/mock-worker.mjs',{type:'module'});
worker.postMessage({type:'start'});
function message(text){$('notice').textContent=text;}
function show(id){
  if(!currentUser)id='login';
  if(id!=='result')message(!currentUser?'共有サーバーへログインしてください。':sensorSource==='mock'?'模擬センサで動作しています。PC内に保存後、共有サーバーへ送信します。':'CONTEC実機モードです。接続状態と配線を確認して測定してください。');
  for(const section of ['login','home','measure','history','result','library','users','settings'])$(section).hidden=section!==id;
  const labels={home:'新しい測定',measure:'測定',history:'測定履歴',result:'測定結果',library:'刺激動画',users:'ユーザー管理',settings:'システム設定',login:'ログイン'};
  $('breadcrumb').textContent='ワークスペース / '+labels[id];
  for(const [nav,page] of Object.entries({navHome:'home',navHistory:'history',navLibrary:'library',navUsers:'users',navSettings:'settings'}))$(nav).classList.toggle('active',page===id||(page==='home'&&id==='measure'));
  window.scrollTo({top:0});
}
function setStage(value){
  stage=value;$('measure').dataset.stage=value;
  $('prepPanel').hidden=value!=='prepare';$('cameraSetup').hidden=value!=='prepare';
  $('calibrationCard').hidden=value!=='calibrate';$('liveCard').hidden=value!=='live';
  $('mockPanel').hidden=value==='prepare'||sensorSource!=='mock';$('graphCard').hidden=value!=='live';
  $('stimulusPicker').hidden=value!=='prepare';$('toCalibration').hidden=value!=='prepare';
  $('backPrep').hidden=value!=='calibrate';$('start').hidden=value!=='calibrate';$('stop').hidden=value!=='live';
  for(const [i,v] of ['prepare','calibrate','live'].entries())$('step'+(i+1)).classList.toggle('current',v===value);
  $('flowTitle').textContent=value==='prepare'?'測定情報を入力してください':value==='calibrate'?'45°・90°の校正を行ってください':'計測・録画中';
  $('state').textContent=value==='prepare'?'準備中':value==='calibrate'?'校正中':(sensorSource==='mock'?'● 模擬計測中':'● CONTEC計測中');
  $('recBadge').textContent=value==='live'?(recorder?'● REC':'録画なし'):'PREVIEW';
  readiness();window.scrollTo({top:0});
}
function prepReady(){return !!currentUser&&sensorReady&&$('subject').value.trim()&&$('condition').value.trim()&&($('withoutCamera').checked||stream?.active)&&(mode==='normal'||($('stimulus').readyState>=1&&Number.isFinite($('stimulus').duration)));}
$('toCalibration').onclick=()=>{if(!busy()&&prepReady())setStage('calibrate');};
$('backPrep').onclick=()=>{if(!busy()){resetCalibration();setStage('prepare');}};
function busy(){return running||saving||!!collecting;}
function resetCalibration(){calibration={};for(const deg of [45,90])$('cal'+deg+'Value').textContent='未校正';recent=[];readiness();}
function configure(m){if(busy()||!currentUser)return;api('/api/settings').then(p=>{if(stage==='prepare'&&!busy()){preferences=p;$('duration').value=p.duration;resetCalibration();}}).catch(()=>{});loadStimuli().catch(()=>{});mode=m;resetCalibration();samples=[];session=null;$('modeTitle').textContent=m==='normal'?'通常測定':'動画連動測定';$('stimulusBox').hidden=m!=='linked';$('pause').hidden=true;$('state').textContent='準備中';$('duration').value=preferences.duration;$('count').textContent='0 サンプル / 0.00 秒';show('measure');setStage('prepare');draw();}
$('normal').onclick=()=>configure('normal');$('linked').onclick=()=>configure('linked');
$('navHome').onclick=$('next').onclick=()=>{if(!busy()){show('home');resetCalibration();}};
$('navHistory').onclick=async()=>{if(!busy()){show('history');await history();}};
$('pose').oninput=()=>{$('poseValue').value=$('pose').value+'°';worker.postMessage({type:'pose',value:Number($('pose').value)});};
function valid(){return validateCalibration(calibration[45]?.diffVoltage,calibration[90]?.diffVoltage,Number($('threshold').value));}
function readiness(){
  const ready=valid()&&prepReady();
  $('start').disabled=!ready||busy();$('toCalibration').disabled=!prepReady()||busy();
  $('cal45').disabled=$('cal90').disabled=busy()||!sensorReady;
  $('readiness').textContent=running?(sensorSource==='mock'?'100Hzの模擬データを記録しています。':'CONTECの実測電圧を記録しています。'):stage==='prepare'?'被験者ID・測定条件・カメラを確認してください。':ready?'校正完了。今回の校正値で測定を開始できます。':'2点の校正を完了してください。';
  $('calHint').textContent=valid()?'✓ 校正完了。この測定に適用する校正値を保存します。':calibration[45]&&calibration[90]?'校正差分が不足しています。姿勢を変えて再校正してください。':'2点の校正が完了すると測定を開始できます。';
}
for(const id of ['subject','condition','threshold','withoutCamera'])$(id).addEventListener('input',readiness);
$('duration').onchange=resetCalibration;
for(const deg of [45,90])$('cal'+deg).onclick=()=>{if(busy()||!sensorReady)return;delete calibration[deg];collecting={deg,after:Date.now(),values:[],target:Number($('duration').value)*100};lock(true);$('cal'+deg+'Value').textContent='校正中…';recent=[];readiness();};
function persist(){if(!session)return;const copy={...session,samples:samples.slice(),chunks:chunks.slice()};persistChain=persistChain.catch(()=>{}).then(()=>save(copy));persistChain.catch(e=>message('ローカル保存失敗：'+e.message+'。停止後にCSVをダウンロードしてください。'));}
worker.onmessage=({data})=>{if(sensorSource==='mock')processBatch(data);};
function processBatch(batch){
  for(const s of batch){
    if(collecting&&s.timestamp>=collecting.after){collecting.values.push(s);if(collecting.values.length===collecting.target){const {deg,values}=collecting;calibration[deg]=calculateCalibration(values);$('cal'+deg+'Value').textContent='完了 ✓ '+calibration[deg].diffVoltage.toFixed(4)+' V';collecting=null;lock(false);readiness();}}
    if(valid()){
      const raw=calculateAngle(s.ch1Voltage-s.ch2Voltage,calibration[45].diffVoltage,calibration[90].diffVoltage,Number($('threshold').value));
      recent.push(raw);if(recent.length>10)recent.shift();const smooth=recent.reduce((a,b)=>a+b,0)/recent.length;
      $('angle').textContent=smooth.toFixed(1);
      if(running&&!paused&&s.timestamp>=recordAfter){const i=samples.length; samples.push({timestamp_iso:new Date(s.timestamp).toISOString(),elapsed_ms:i*10,sample_index:i,device_sample_index:s.sampleIndex,ch1_voltage:s.ch1Voltage,ch2_voltage:s.ch2Voltage,diff_voltage:s.ch1Voltage-s.ch2Voltage,raw_angle_deg:raw,smoothed_angle_deg:smooth,paused:false});}
    }else $('angle').textContent='—';
  }
  const s=batch.at(-1);if(s){$('ch1').textContent=s.ch1Voltage.toFixed(3);$('ch2').textContent=s.ch2Voltage.toFixed(3);$('diff').textContent=(s.ch1Voltage-s.ch2Voltage).toFixed(3);}
  if(running){$('count').textContent=samples.length+' サンプル / '+(samples.length/100).toFixed(2)+' 秒';draw();if(Date.now()-lastPersist>3000){lastPersist=Date.now();persist();}}
};
function renderChart(canvas, input, full=false){
  const ctx=canvas.getContext('2d'),w=canvas.width,h=canvas.height;ctx.clearRect(0,0,w,h);
  const size=preferences.window*100,data=full?input:input.slice(-size);
  let lo=0,hi=100;for(const item of data){lo=Math.min(lo,item.smoothed_angle_deg);hi=Math.max(hi,item.smoothed_angle_deg);}
  const min=preferences.axis==='fixed'?preferences.min:lo-5,max=preferences.axis==='fixed'?preferences.max:hi+5;
  ctx.font='12px sans-serif';ctx.fillStyle='#6c8388';
  for(let i=0;i<=4;i++){const y=15+i*(h-45)/4;ctx.strokeStyle='#e5eded';ctx.beginPath();ctx.moveTo(44,y);ctx.lineTo(w-12,y);ctx.stroke();ctx.fillText((max-i*(max-min)/4).toFixed(0)+'°',0,y+4);}
  ctx.save();ctx.beginPath();ctx.rect(44,15,w-60,h-45);ctx.clip();ctx.strokeStyle='#168675';ctx.lineWidth=2;ctx.beginPath();
  data.forEach((s,i)=>{const x=44+i/Math.max(1,full?data.length-1:size-1)*(w-60),y=15+(max-s.smoothed_angle_deg)/(max-min)*(h-45);if(i===0)ctx.moveTo(x,y);else ctx.lineTo(x,y);});ctx.stroke();ctx.restore();
  ctx.fillText((full?0:Math.max(0,(input.length-size)/100)).toFixed(1)+' s',44,h-4);ctx.fillText((input.length/100).toFixed(1)+' s',w-65,h-4);
}
function draw(){renderChart($('chart'),samples);}
async function connectCamera(id){if(busy())return;try{const next=await navigator.mediaDevices.getUserMedia({video:id?{deviceId:{exact:id}}:true,audio:false});stream?.getTracks().forEach(t=>t.stop());stream=next;$('preview').srcObject=stream;$('cameraEmpty').hidden=true;$('cameraLabel').textContent='プレビュー接続済み';$('withoutCamera').checked=false;const cameras=(await navigator.mediaDevices.enumerateDevices()).filter(d=>d.kind==='videoinput');$('cameras').replaceChildren(...cameras.map((d,i)=>new Option(d.label||'カメラ '+(i+1),d.deviceId)));$('cameras').value=stream.getVideoTracks()[0].getSettings().deviceId;stream.getVideoTracks()[0].onended=()=>{message('カメラ接続が切れました。');if(running)stop();readiness();};readiness();}catch(e){message('カメラを接続できません：'+e.message);}}
$('cameraConnect').onclick=()=>connectCamera();$('cameras').onchange=()=>connectCamera($('cameras').value);
$('stimulusFile').onchange=()=>{selectedStimulus=null;$('stimulusLibrary').value='';if(stimulusUrl)URL.revokeObjectURL(stimulusUrl);const f=$('stimulusFile').files[0];$('stimulus').src=f?(stimulusUrl=URL.createObjectURL(f)):'';readiness();};$('stimulus').onloadedmetadata=readiness;
function lock(yes){$('form').disabled=yes;for(const id of ['withoutCamera','cameraConnect','cameras','navHome','navHistory','navLibrary','navUsers','navSettings','navLogin','backPrep','duration','threshold','stimulusFile','stimulusLibrary','inputSource','deviceName','physical1','physical2','deviceList','deviceConnect','deviceDisconnect'])$(id).disabled=yes;}
$('start').onclick=async()=>{
  readiness();if($('start').disabled)return;saving=true;lock(true);readiness();samples=[];recent=[];chunks=[];lastPersist=Date.now();
  session={id:crypto.randomUUID(),subjectId:$('subject').value.trim(),side:$('side').value,condition:$('condition').value.trim(),memo:$('memo').value,mode,operator:currentUser.displayName,operatorId:currentUser.id,graphPreferences:structuredClone(preferences),source:sensorSource,device:sensorSource==='mock'?null:{name:$('deviceName').value,model:connectedModel,channel1:Number($('physical1').value),channel2:Number($('physical2').value),range:'±10V',rawLog,captureId},timestampBasis:'host start + hardware sample index * 10 ms for CONTEC; synchronization not hardware guaranteed',samplingRateHz:100,smoothingWindow:10,calibration:structuredClone(calibration),calibrationDurationSec:Number($('duration').value),calibrationThresholdV:Number($('threshold').value),startedAt:new Date().toISOString(),status:'recording',pauseEvents:[],stimulusId:selectedStimulus?.id||null,stimulusName:selectedStimulus?.name||$('stimulusFile').files[0]?.name||null};
  try{
    if(!$('withoutCamera').checked){if(!stream?.active)throw new Error('カメラを再接続してください。');recorder=new MediaRecorder(stream);recorder.ondataavailable=e=>{if(e.data.size)chunks.push(e.data);};recorder.onerror=()=>{message('録画エラー。測定を停止します。');stop();};recorder.start(1000);session.cameraStartedAt=new Date().toISOString();}else recorder=null;
    if(mode==='linked'){$('stimulus').currentTime=0;await $('stimulus').play();session.stimulusStartedAt=new Date().toISOString();}
    recordAfter=Date.now();session.recordGateAt=new Date(recordAfter).toISOString();running=true;paused=false;saving=false;setStage('live');$('sessionSummary').textContent=session.subjectId+' / '+(session.side==='right'?'右':'左')+' / '+session.condition;$('state').textContent=(sensorSource==='mock'?'● 模擬計測中':'● CONTEC計測中');$('stop').disabled=false;$('pause').hidden=mode!=='linked';$('pause').textContent='一時停止';persist();readiness();
  }catch(e){if(recorder?.state==='recording')recorder.stop();$('stimulus').pause();saving=false;lock(false);message('開始できません：'+e.message);readiness();}
};
$('pause').onclick=async()=>{if(!running)return;try{if(!paused){$('stimulus').pause();recorder?.pause();paused=true;$('pause').textContent='再開';}else{await $('stimulus').play();recorder?.resume();paused=false;recordAfter=Date.now();$('pause').textContent='一時停止';recent=[];}session.pauseEvents.push({paused,at:new Date().toISOString(),elapsedMs:samples.length*10});$('state').textContent=paused?'一時停止中':(sensorSource==='mock'?'● 模擬計測中':'● CONTEC計測中');persist();}catch(e){message('一時停止・再開エラー：'+e.message);await stop();}};
$('stimulus').onended=()=>{if(running)stop();};$('stimulus').onerror=()=>{if(running){message('刺激動画の再生に失敗しました。');stop();}};
async function stop(){if(!running)return;running=false;saving=true;$('stop').disabled=true;$('pause').hidden=true;$('stimulus').pause();$('state').textContent='保存中';try{if(recorder&&recorder.state!=='inactive')await new Promise((resolve,reject)=>{const timeout=setTimeout(()=>reject(new Error('録画停止がタイムアウトしました')),5000);recorder.onstop=()=>{clearTimeout(timeout);resolve();};recorder.stop();});session={...session,status:session.failure?'interrupted':'complete',endedAt:new Date().toISOString(),samples:samples.slice(),chunks:chunks.slice(),videoType:recorder?.mimeType||''};persist();const saved=await Promise.allSettled([persistChain,queueSession(session,generateCsv(session.samples))]);if(saved[1].status==='rejected')throw saved[1].reason;message(session.failure?'計測を中断しました：'+session.failure+'。取得済みデータを保存しました。':'PC内に保存しました。共有サーバーへの送信状態を確認してください。');}catch(e){session={...session,status:'save-error',samples:samples.slice(),chunks:chunks.slice()};message('保存に問題が発生しました：'+e.message+'。CSVと動画をダウンロードしてください。');}finally{saving=false;lock(false);showResult(session);resetCalibration();if(sensorSource==='contec')disconnectDevice();}}
$('stop').onclick=stop;
function showResult(s){s.analysisLoaded=false;result=s;show('result');refreshResultStatus();
  $('resultTitle').textContent='測定結果';$('localStatus').textContent=s.remote?'✓ 共有サーバー保存済み':s.status==='complete'?'✓ ローカル保存完了':s.status==='recording'?'途中保存データ':s.status==='interrupted'?'中断・取得済み保存':'保存エラー';
  $('resultMetadata').replaceChildren();
  for(const [key,value] of [['被験者ID',s.subjectId],['測定日時',new Date(s.startedAt).toLocaleString('ja-JP')],['測定者',s.operator||'デモ利用者'],['入力元',s.source||'mock'],['中断理由',s.failure||'なし'],['生電圧CSV',s.device?.rawLog||'ブラウザ内'],['メモ',s.memo||'なし'],['45°校正',(s.calibration?.[45]?.diffVoltage?.toFixed(4)??'—')+' V'],['90°校正',(s.calibration?.[90]?.diffVoltage?.toFixed(4)??'—')+' V'],['サンプル数',s.samples.length]]){const dt=document.createElement('dt'),dd=document.createElement('dd');dt.textContent=key;dd.textContent=value;$('resultMetadata').append(dt,dd);}
  renderChart($('resultChart'),s.samples,true);$('noVideo').hidden=!!s.chunks?.length;$('resultVideo').hidden=!s.chunks?.length;
$('resultInfo').textContent=s.subjectId+' / '+(s.side==='right'?'右':'左')+' / '+s.condition+' / '+(s.samples.length/100).toFixed(2)+'秒 / '+(s.source==='contec'?'実機':'模擬');$('videoDownload').disabled=!s.chunks?.length;if(resultUrl)URL.revokeObjectURL(resultUrl);if(s.chunks?.length){resultUrl=URL.createObjectURL(new Blob(s.chunks,{type:s.videoType||s.chunks[0].type}));$('resultVideo').src=resultUrl;}else $('resultVideo').removeAttribute('src');}
function download(blob,name){const url=URL.createObjectURL(blob),a=document.createElement('a');a.href=url;a.download=name;a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);}
$('csv').onclick=()=>download(new Blob([generateCsv(result.samples)],{type:'text/csv;charset=utf-8'}),'gonio-'+result.id+'.csv');
$('json').onclick=()=>{const {samples,chunks,...metadata}=result;download(new Blob([JSON.stringify({...metadata,sampleCount:samples.length},null,2)],{type:'application/json'}),'session-'+result.id+'.json');};
$('videoDownload').onclick=()=>download(new Blob(result.chunks,{type:result.videoType||result.chunks[0].type}),'raw-'+result.id+(String(result.videoType||result.chunks[0].type).includes('mp4')?'.mp4':'.webm'));
async function history(){try{
  const local=(await listLocal()).filter(s=>s.operatorId===currentUser.id||role==='admin');let shared=[],deleted=[];try{[shared,deleted]=await Promise.all([api('/api/sessions?'+new URLSearchParams({q:$('search').value,date:$('dateFilter').value,side:$('sideFilter').value,operator:$('operatorFilter').value,offset:String(-new Date().getTimezoneOffset())})),api('/api/sessions/deleted')]);}catch(e){message('共有履歴を取得できません。PC内の記録を表示します。');}const merged=new Map(local.map(s=>[s.id,s]));for(const s of shared)merged.set(s.id,s);const rows=[...merged.values()].filter(s=>!deleted.includes(s.id)).sort((a,b)=>b.startedAt.localeCompare(a.startedAt)),q=$('search').value.toLowerCase(),date=$('dateFilter').value,side=$('sideFilter').value;$('historyList').replaceChildren();
  for(const s of rows.filter(s=>(s.subjectId+' '+s.condition).toLowerCase().includes(q)&&(s.operator||'').toLowerCase().includes($('operatorFilter').value.toLowerCase())&&(!side||s.side===side)&&(!date||localDate(s.startedAt)===date))){
    const row=document.createElement('tr');
    for(const text of [new Date(s.startedAt).toLocaleString('ja-JP'),s.subjectId+' / '+s.condition,s.side==='right'?'右':'左',s.mode==='normal'?'通常':'動画連動',s.operator||'—',s.status==='complete'?'保存完了':s.status==='recording'?'途中保存':s.status==='interrupted'?'中断':'保存エラー']){const td=document.createElement('td');td.textContent=text;row.append(td);}
    const td=document.createElement('td'),button=document.createElement('button');button.textContent='詳細 →';button.onclick=async()=>{try{if(s.remote){const detail=await api('/api/sessions/'+s.id);detail.chunks=[];if(detail.videoExtension){const blob=await(await request('/api/sessions/'+s.id+'/files/video')).blob();detail.chunks=[blob];detail.videoType=blob.type;}showResult(detail);}else showResult(s);}catch(e){message(e.message);}};td.append(button);row.append(td);$('historyList').append(row);
  }
  if(!$('historyList').children.length){const tr=document.createElement('tr'),td=document.createElement('td');td.colSpan=7;td.className='empty';td.textContent=rows.length?'条件に一致する測定はありません。':'まだ測定履歴がありません。新しい測定から記録を始めてください。';tr.append(td);$('historyList').append(tr);}
}catch(e){message('履歴を読み込めません：'+e.message);}}
function localDate(value){const d=new Date(value);return d.getFullYear()+'-'+String(d.getMonth()+1).padStart(2,'0')+'-'+String(d.getDate()).padStart(2,'0');}
$('search').oninput=history;
window.addEventListener('beforeunload',e=>{if(busy()){e.preventDefault();e.returnValue='';}});
draw();readiness();

$('dateFilter').oninput=$('sideFilter').onchange=$('operatorFilter').oninput=history;
$('resultHistory').onclick=()=>{show('history');history();};
$('navLibrary').onclick=()=>{if(!busy()){show('library');loadStimuli().catch(e=>message(e.message));}};
$('libraryMeasure').onclick=()=>configure('linked');
$('navUsers').onclick=()=>{if(!busy()&&role==='admin'){show('users');loadUsers().catch(e=>message(e.message));}};
$('navSettings').onclick=()=>{if(!busy()&&role==='admin'){for(const [id,key] of [['settingDuration','duration'],['settingWindow','window'],['settingAxis','axis'],['settingMin','min'],['settingMax','max']])$(id).value=preferences[key];show('settings');refreshDropbox();}};
$('navLogin').onclick=async()=>{if(busy())return;api('/api/auth/logout',{method:'POST'}).catch(()=>{});setToken('');currentUser=null;disconnectDevice();stream?.getTracks().forEach(t=>t.stop());stream=null;show('login');};
$('settingsForm').onsubmit=async e=>{e.preventDefault();if(role!=='admin')return;const min=Number($('settingMin').value),max=Number($('settingMax').value);if(!Number.isFinite(min)||!Number.isFinite(max)||min>=max){$('settingsStatus').textContent='最小角度は最大角度より小さくしてください。';return;}const next={duration:Number($('settingDuration').value),window:Number($('settingWindow').value),axis:$('settingAxis').value,min,max};try{await api('/api/admin/settings',{method:'PUT',body:next});localStorage.setItem('gonio-preferences',JSON.stringify(next));preferences=next;$('settingsStatus').textContent='設定を保存しました。';}catch{$('settingsStatus').textContent='設定を保存できませんでした。共有サーバーとの接続を確認してください。';}};

function sourceStatus(text){$('deviceState').textContent=text;$('sensorHeader').textContent=text;}
function disconnectDevice(){const old=deviceSocket;deviceSocket=null;if(old)old.close();sensorReady=sensorSource==='mock';lastDeviceIndex=null;rawLog=null;captureId=null;resetCalibration();sourceStatus(sensorSource==='mock'?'模擬センサ':'実機未接続');$('deviceDisconnect').disabled=true;}
function deviceFailure(reason){sensorReady=false;sourceStatus('実機エラー');if(collecting){collecting=null;lock(false);resetCalibration();}if(running){session.failure=reason;stop();}message(reason);readiness();const old=deviceSocket;deviceSocket=null;old?.close();}
$('inputSource').onchange=()=>{if(busy())return;sensorSource=$('inputSource').value;disconnectDevice();worker.postMessage({type:sensorSource==='mock'?'start':'stop'});$('mockPanel').hidden=stage==='prepare'||sensorSource!=='mock';readiness();};
$('deviceDisconnect').onclick=()=>{if(!busy())disconnectDevice();};
for(const id of ['deviceName','physical1','physical2'])$(id).onchange=()=>{if(!busy()&&sensorSource!=='mock')disconnectDevice();};
$('deviceList').onclick=async()=>{try{const r=await fetch('/device/list');if(!r.headers.get('content-type')?.includes('application/json'))throw new Error('Windows配布版を起動してください。このWebサーバーには実機サービスがありません。');const data=await r.json();if(data.error)throw new Error(data.error);$('deviceInfo').textContent=data.devices.length?data.devices.map(d=>d.name+' : '+d.model+(d.supported?'（対応・実機未検証）':'（未対応）')).join(' / '):'登録機器がありません。ドライバとDevice Utilityを確認してください。';}catch(e){$('deviceInfo').textContent=e.message;}};
$('deviceConnect').onclick=()=>{
  if(busy())return;if(sensorSource!=='contec'){message('入力元をCONTEC実機に変更してください。');return;}
  const a=Number($('physical1').value),b=Number($('physical2').value),name=$('deviceName').value.trim();
  if(!/^[a-zA-Z0-9_]{1,64}$/.test(name)||![a,b].every(x=>Number.isInteger(x)&&x>=0&&x<=7)||a===b){message('登録デバイス名と異なる2つの物理チャンネル（0〜7）を指定してください。');return;}
  disconnectDevice();sourceStatus('接続中…');
  const socket=new WebSocket((location.protocol==='https:'?'wss://':'ws://')+location.host+'/measurement/stream?'+new URLSearchParams({device:name,ch1:a,ch2:b}));deviceSocket=socket;
  const timeout=setTimeout(()=>{if(deviceSocket===socket&&!sensorReady)deviceFailure('接続がタイムアウトしました。Windowsサービスを確認してください。');},10000);
  socket.onmessage=({data})=>{if(deviceSocket!==socket)return;try{
    const event=JSON.parse(data);
    if(event.type==='connected'){if(event.source!=='contec'){deviceFailure('実機ではないテストサービスです。実機測定を拒否しました。');return;}connectedModel=event.model;sensorReady=true;lastDeviceArrival=Date.now();rawLog=event.rawLog;captureId=event.captureId;sourceStatus('CONTEC 接続済み');$('deviceDisconnect').disabled=false;$('deviceInfo').textContent=(connectedModel||'CONTEC')+' / 100Hz / ±10V / 生電圧CSV: '+rawLog;clearTimeout(timeout);readiness();}
    if(event.type==='error')deviceFailure(event.message);
    if(event.type==='samples'&&sensorReady){
      if(!Array.isArray(event.samples)||!event.samples.length)throw new Error('受信データが不正です。');
      for(const sample of event.samples){if(!Number.isSafeInteger(sample.sampleIndex)||![sample.timestamp,sample.ch1Voltage,sample.ch2Voltage].every(Number.isFinite))throw new Error('受信値が不正です。');if(lastDeviceIndex!==null&&sample.sampleIndex!==lastDeviceIndex+1)throw new Error('サンプルの欠落・重複を検出したため中断しました。');lastDeviceIndex=sample.sampleIndex;}
      lastDeviceArrival=Date.now();processBatch(event.samples);
    }
  }catch(e){deviceFailure(e.message);}};
  socket.onerror=()=>{if(deviceSocket===socket)deviceFailure('実機サービスに接続できません。Windows版を起動し、他の測定画面を閉じてください。');};
  socket.onclose=()=>{clearTimeout(timeout);if(deviceSocket===socket)deviceFailure('実機との接続が切れました。再接続・再校正してください。');};
};
setInterval(()=>{if(sensorSource==='contec'&&sensorReady&&Date.now()-lastDeviceArrival>6000)deviceFailure('センサデータの受信が途絶えました。再接続してください。');},1000);

async function refreshUpdateStatus(){try{const r=await fetch('/update/status');if(r.ok&&r.headers.get('content-type')?.includes('application/json')){const d=await r.json();$('updateStatus').textContent=d.message;}else{$('updateStatus').textContent='更新手順はGitHubのWindows導入ガイドを確認してください。';}}catch{$('updateStatus').textContent='更新状態を取得できません。';}}
refreshUpdateStatus();setInterval(refreshUpdateStatus,30000);

function signedIn(user){currentUser=user;role=user.role;$('roleName').textContent=user.displayName;document.querySelectorAll('.adminOnly').forEach(el=>el.hidden=role!=='admin');show('home');api('/api/settings').then(p=>preferences=p).catch(()=>{});}
$('loginForm').onsubmit=async e=>{e.preventDefault();$('loginStatus').textContent='ログイン中…';try{const data=await api('/api/auth/login',{method:'POST',body:{username:$('loginUsername').value.trim(),password:$('loginPassword').value}});setToken(data.token);$('loginPassword').value='';$('loginStatus').textContent='';signedIn(data.user);}catch(error){$('loginStatus').textContent=error.message;}};
$('setupForm').onsubmit=async e=>{e.preventDefault();try{const data=await api('/api/auth/setup',{method:'POST',body:{username:$('setupUsername').value.trim(),displayName:$('setupDisplayName').value.trim(),password:$('setupPassword').value,setupToken:$('setupToken').value}});$('setupPassword').value=$('setupToken').value='';$('loginStatus').textContent=data.message;$('setupDetails').open=false;}catch(error){$('loginStatus').textContent=error.message;}};
async function initializeAccount(){show('login');try{const status=await api('/api/auth/status');$('setupDetails').hidden=!status.setupRequired;if(token())signedIn(await api('/api/auth/me'));}catch(error){$('loginStatus').textContent=error.message;}}
let users=[];
async function loadUsers(){users=await api('/api/admin/users');$('userSelect').replaceChildren(new Option('新規作成',''),...users.map(u=>new Option(u.displayName+' / '+u.username+(u.active?'':'（無効）'),u.id)));}
$('userSelect').onchange=()=>{const u=users.find(u=>u.id===$('userSelect').value);$('userUsername').value=u?.username||'';$('userDisplayName').value=u?.displayName||'';$('userRole').value=u?.role||'user';$('userActive').checked=u?!!u.active:true;$('userPassword').value='';};
$('userForm').onsubmit=async e=>{e.preventDefault();const id=$('userSelect').value;try{await api('/api/admin/users'+(id?'/'+id:''),{method:id?'PUT':'POST',body:{username:$('userUsername').value.trim(),displayName:$('userDisplayName').value.trim(),role:$('userRole').value,active:$('userActive').checked,password:$('userPassword').value||null}});$('userPassword').value='';$('userStatus').textContent='保存しました。編集された利用者は再ログインが必要です。';if(id===currentUser.id){setToken('');currentUser=null;show('login');}else await loadUsers();}catch(error){$('userStatus').textContent=error.message;}};
let stimuli=[];
async function loadStimuli(){stimuli=await api('/api/stimuli');const selected=$('stimulusLibrary').value;$('stimulusLibrary').replaceChildren(new Option('このPCの動画を選択',''),...stimuli.map(s=>new Option(s.name,s.id)));$('stimulusLibrary').value=selected;$('libraryList').replaceChildren();for(const stimulus of stimuli){const row=document.createElement('div');row.className='actions';const title=document.createElement('span');title.textContent=stimulus.name+' / Dropbox: '+stimulus.sync;const use=document.createElement('button');use.textContent='測定に使用';use.onclick=async()=>{configure('linked');try{await selectStimulus(stimulus.id);}catch(e){message(e.message);}};row.append(title,use);if(role==='admin'){const rename=document.createElement('button');rename.textContent='名前変更';rename.onclick=async()=>{const name=prompt('動画名',stimulus.name);if(!name)return;try{await api('/api/admin/stimuli/'+stimulus.id,{method:'PUT',body:{name}});await loadStimuli();}catch(e){message(e.message);}};const hide=document.createElement('button');hide.textContent='非表示';hide.onclick=async()=>{if(!confirm('共有ライブラリから非表示にします。保存ファイルは保持します。'))return;try{await api('/api/admin/stimuli/'+stimulus.id,{method:'DELETE'});await loadStimuli();}catch(e){message(e.message);}};row.append(rename,hide);}$('libraryList').append(row);}if(!stimuli.length)$('libraryList').textContent='登録された動画はありません。';}
async function selectStimulus(id){if(!id)return;const s=stimuli.find(s=>s.id===id);if(!s)return;message('刺激動画を取得しています…');const blob=await(await request('/api/stimuli/'+id+'/video')).blob();if(stimulusUrl)URL.revokeObjectURL(stimulusUrl);selectedStimulus=s;$('stimulusFile').value='';$('stimulusLibrary').value=id;$('stimulus').src=stimulusUrl=URL.createObjectURL(blob);message('刺激動画を準備しました：'+s.name);readiness();}
$('stimulusLibrary').onchange=()=>{if(!$('stimulusLibrary').value){selectedStimulus=null;$('stimulus').removeAttribute('src');readiness();return;}selectStimulus($('stimulusLibrary').value).catch(e=>message(e.message));};
$('libraryForm').onsubmit=async e=>{e.preventDefault();const form=new FormData();form.append('name',$('libraryName').value.trim());form.append('video',$('libraryFile').files[0]);$('libraryStatus').textContent='動画を保存中…';try{await api('/api/admin/stimuli',{method:'POST',body:form});$('libraryStatus').textContent='共有ライブラリに保存しました。Dropboxへの送信は接続後に自動実行します。';$('libraryForm').reset();await loadStimuli();}catch(error){$('libraryStatus').textContent=error.message;}};
async function refreshDropbox(){try{$('dropboxStatus').textContent=(await api('/api/admin/dropbox/status')).message;}catch(error){$('dropboxStatus').textContent=error.message;}}
$('dropboxRefresh').onclick=refreshDropbox;
$('dropboxConnect').onclick=async()=>{try{const data=await api('/api/admin/dropbox/connect',{method:'POST'});$('dropboxLink').href=data.url;$('dropboxLink').hidden=false;$('dropboxStatus').textContent='認証リンクを開き、保存先のDropboxアカウントで認証してください。';}catch(error){$('dropboxStatus').textContent=error.message;}};
async function refreshResultStatus(){if(!result||!currentUser)return;const id=result.id;$('analysisDownload').disabled=true;$('analysisVideo').hidden=true;$('processingMessage').textContent='';try{const status=await api('/api/sessions/'+id+'/status');if(result?.id!==id)return;const labels={pending:'待機中',processing:'生成中',uploading:'送信中',complete:'完了',error:'エラー','no-video':'録画なし'};$('syncStatus').textContent='Dropbox: '+(labels[status.sync]||status.sync);$('analysisStatus').textContent='解析MP4: '+(labels[status.analysis]||status.analysis);$('processingMessage').textContent=[status.error,status.syncError].filter(Boolean).join(' / ');if(status.analysis==='complete'){$('analysisDownload').disabled=false;if(result.analysisLoaded!==true){const blob=await(await request('/api/sessions/'+id+'/files/analysis')).blob();if(result?.id!==id)return;if(analysisUrl)URL.revokeObjectURL(analysisUrl);analysisUrl=URL.createObjectURL(blob);$('analysisVideo').src=analysisUrl;result.analysisLoaded=true;}$('analysisVideo').hidden=false;}}catch{try{const states=await api('/local/outbox');if(result?.id!==id)return;const state=states.find(s=>s.id===id);$('syncStatus').textContent=state?.message||'PC内の記録を共有サーバーへ送信してください。';$('analysisStatus').textContent='共有保存後に解析';}catch{$('syncStatus').textContent='共有サーバー未接続。PC内のデータは保持しています。';}}}
$('retryProcessing').onclick=async()=>{try{if(!result.remote)await queueSession(result,generateCsv(result.samples));await api('/local/outbox/retry',{method:'POST'});try{await api('/api/sessions/'+result.id+'/retry',{method:'POST'});}catch{}await refreshResultStatus();}catch(e){message(e.message);}};
$('analysisDownload').onclick=async()=>{try{download(await(await request('/api/sessions/'+result.id+'/files/analysis')).blob(),'analysis-'+result.id+'.mp4');}catch(e){message(e.message);}};
$('deleteSession').onclick=async()=>{if(role!=='admin'||!result.remote){message('共有保存済みの記録を管理者が非表示にできます。');return;}if(!confirm('共有履歴から非表示にします。保存ファイルは保持します。'))return;try{await api('/api/admin/sessions/'+result.id,{method:'DELETE'});show('history');await history();}catch(e){message(e.message);}};
document.querySelectorAll('.adminOnly').forEach(el=>el.hidden=true);
setInterval(()=>{if(!$('result').hidden)refreshResultStatus();},15000);
initializeAccount();

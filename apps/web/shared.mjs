export function token(){return sessionStorage.getItem('gonio-token')||'';}
export function setToken(value){if(value)sessionStorage.setItem('gonio-token',value);else sessionStorage.removeItem('gonio-token');}
export async function request(path,options={}){
 const headers=new Headers(options.headers);if(token())headers.set('Authorization','Bearer '+token());
 let body=options.body;if(body&&!(body instanceof FormData)&&!(body instanceof Blob)&&typeof body!=='string'){headers.set('Content-Type','application/json');body=JSON.stringify(body);}
 const response=await fetch(path,{...options,headers,body});
 if(!response.ok){let message='HTTP '+response.status;try{message=(await response.json()).error||message;}catch{}throw new Error(message);}
 return response;
}
export async function api(path,options={}){const response=await request(path,options);return response.headers.get('content-type')?.includes('application/json')?response.json():null;}
export async function queueSession(session,csv){
 const {chunks,...data}=session;const form=new FormData();form.append('session',new Blob([JSON.stringify(data)],{type:'application/json'}),'session.json');form.append('csv',new Blob([csv],{type:'text/csv'}),'data.csv');
 if(chunks?.length)form.append('video',new Blob(chunks,{type:session.videoType||chunks[0].type||'video/webm'}),'raw_video');
 return api('/local/sessions/'+session.id,{method:'PUT',body:form});
}

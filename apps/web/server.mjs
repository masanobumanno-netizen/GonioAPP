import http from 'node:http';
import {readFile} from 'node:fs/promises';
import {fileURLToPath} from 'node:url';
import path from 'node:path';
const root = fileURLToPath(new URL('../../',import.meta.url));
const types = {'.html':'text/html','.css':'text/css','.mjs':'text/javascript'};
http.createServer(async(req,res)=>{
  try {
    const url = new URL(req.url,'http://localhost');
    const rel = url.pathname === '/' ? 'apps/web/index.html' : decodeURIComponent(url.pathname).slice(1);
    const file = path.resolve(root,rel);
    if (!file.startsWith(root) || !(rel.startsWith('apps/web/') || rel.startsWith('packages/measurement-core/'))) {res.writeHead(404);return res.end();}
    const body = await readFile(file);
    res.writeHead(200,{'Content-Type':types[path.extname(file)]||'application/octet-stream','Cache-Control':'no-store','X-Content-Type-Options':'nosniff'});res.end(body);
  } catch {res.writeHead(404);res.end('Not found');}
}).listen(Number(process.env.PORT || 4173),'127.0.0.1',()=>console.log('Gonio Web: http://127.0.0.1:'+(process.env.PORT||4173)));

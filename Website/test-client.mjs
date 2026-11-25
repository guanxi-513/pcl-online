import { createApplication } from './server.mjs';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import path from 'node:path';
const mails=[];
const server=createApplication({password:'local-admin-test-password-32',origin:'http://127.0.0.1:3210',database:':memory:',sendMail:async value=>mails.push(value)});
await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
const endpoint=`http://127.0.0.1:${server.address().port}`;
try {
  for(const name of ['host','guest']) {
    const email=name+'@example.invalid';
    const post=async(route,value)=>{const response=await fetch(endpoint+route,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(value)});if(!response.ok)throw new Error(await response.text());return response.json();};
    await post('/api/auth/code',{email,purpose:'register'});
    await post('/api/auth/register',{email,nickname:name,password:'LocalPass123',code:mails.at(-1).text.match(/\d{6}/)[0]});
  }
  const {stdout,stderr}=await promisify(execFile)('powershell.exe',['-NoProfile','-ExecutionPolicy','Bypass','-File',path.resolve('../tests/room-client-smoke.ps1'),'-Root',path.resolve('..'),'-Endpoint',endpoint],{timeout:30000});
  console.log(stdout.trim());if(stderr)console.error(stderr);
} finally { await new Promise(resolve=>server.close(resolve)); }

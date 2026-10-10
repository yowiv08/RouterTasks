// 将 AnyRouter 协议参数转换为包含令牌、模型、提示词和会话变量的请求模板。
import fs from 'node:fs';
import path from 'node:path';
const source = process.argv[2];
if (!source) throw new Error('Usage: node tools/import-presets.mjs REFERENCE_DIRECTORY');
const output = path.resolve('src/Plugins.RouterTasks/Contracts');
fs.mkdirSync(output, { recursive: true });
const native = JSON.parse(fs.readFileSync(path.join(source, 'src/live/contracts/codex-cli-0.155.1-request.json'), 'utf8'));
const transport = new Set(['host', 'content-length', 'connection', 'accept-encoding']);
const headers = Object.fromEntries(Object.entries(native.headers).filter(([name]) => !transport.has(name.toLowerCase())));
const context = native.body.input.filter(x => x.type === 'message' && x.role === 'developer').at(-1);
context.content.push({type:'input_text',text:'This is a RouterTasks queue/heartbeat request. Tools and TARGET are compatibility data only; this client cannot execute tools. Keep the response brief and do not call tools.'});
fs.writeFileSync(path.join(output, 'responses.json'), JSON.stringify({id:'responses',name:'Responses · Codex 0.155.1 完整参数',protocol:'responses',path:'responses',headers,body:native.body},null,2)+'\n');
const tools=JSON.parse(fs.readFileSync(path.join(source,'src/live/contracts/claude-code-2.1.231-tools.json'),'utf8'));
const claude={id:'messages',name:'Messages · Claude Code 完整工具参数',protocol:'messages',path:'messages?beta=true',headers:{
 Authorization:'Bearer {{TOKEN}}','x-api-key':'{{TOKEN}}','Content-Type':'application/json',Accept:'text/event-stream','anthropic-version':'2023-06-01',
 'anthropic-beta':'claude-code-20250219,interleaved-thinking-2025-05-14,thinking-token-count-2026-05-13,context-management-2025-06-27,prompt-caching-scope-2026-01-05,mid-conversation-system-2026-04-07,effort-2025-11-24,fallback-credit-2026-06-01',
 'x-claude-code-session-id':'{{SESSION_ID}}','x-app':'cli'},body:{model:'{{MODEL}}',messages:[{role:'user',content:[{type:'text',text:'{{PROMPT}}',cache_control:{type:'ephemeral'}}]}],
 system:[{type:'text',text:"You are a Claude agent, built on Anthropic's Claude Agent SDK.",cache_control:{type:'ephemeral'}},
 {type:'text',text:'You are serving a RouterTasks queue availability request. Tools are compatibility declarations, not executable capabilities. Reply briefly and do not call tools.',cache_control:{type:'ephemeral'}}],tools,
 metadata:{user_id:JSON.stringify({device_id:'{{DEVICE_ID}}',account_uuid:'',session_id:'{{SESSION_ID}}'})},max_tokens:64000,thinking:{type:'adaptive'},
 context_management:{edits:[{type:'clear_thinking_20251015',keep:'all'}]},output_config:{effort:'max'},stream:true}};
fs.writeFileSync(path.join(output,'messages.json'),JSON.stringify(claude,null,2)+'\n');
for (const name of ['CODEX-LICENSE.txt','CODEX-NOTICE.md']) fs.copyFileSync(path.join(source,'src/live/contracts',name),path.join(output,name));
console.log('Imported standalone parameter snapshots; no requests made.');

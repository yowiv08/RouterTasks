import { createServer } from 'node:http';
import { mkdir, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import assert from 'node:assert/strict';
import { chromium } from 'playwright';
import { pageHtml, fixtureData, controller, sampleConfig } from '../tests/ui/harness.mjs';

// 回环管理 API 与宿主 iframe 沙箱的浏览器检查。
const out = resolve(process.env.ROUTERTASKS_BROWSER_OUTPUT || 'artifacts/browser');
await mkdir(out,{recursive:true});
const data=await fixtureData(), api=controller(data), checks=[];
const now=new Date().toISOString();
const definitions=[
  ['排队入口 · Responses','queue_then_keepalive','running','queue',false,500],
  ['工作时段持续保活','keepalive','running','keepalive',true,200],
  ['自动重排关闭 · 等待确认','queue_then_keepalive','paused','keepalive',false,500],
  ['首次心跳 · 尚未确认','keepalive','running','keepalive',false,null],
  ['仅排队 · 已进入','queue','completed','queue',false,200]
];
for(const [i,def] of definitions.entries()) {
  const [name,mode,status,phase,confirmed,code]=def;
  const lanes=i===0?Array.from({length:8},(_,index)=>({index:index+1,status:'waiting',attempts:2,lastError:'HTTP 500 · '+('x'.repeat(420))})):[];
  data.tasks.push({config:sampleConfig('task'+i,{name,mode,siteId:'site',keyId:'key',model:i%2?'claude-sonnet-4-6':'gpt-5.4',autoRequeue:i!==2}),configRevision:1,
    runtime:{status,phase,queueRound:i===1?3:1,totalAttempts:i===1?54:13,heartbeats:i===1?41:0,recoveries:i===1?2:0,
      sessionConfirmed:confirmed,lastSuccessAt:i!==0?now:null,lastHttpStatus:code,matchedRule:code===500?'HTTP 500':code===200?'SSE accepted':null,
      pauseReason:i===2?'HTTP 500 命中排队规则；自动重新排队已关闭':null,nextAttemptAt:status==='running'?now:null,lanes},totalLanes:i===0?12:0});
}
data.activeCount=3;
data.stats=[{taskId:'task1',day:now.slice(0,10),protocol:'responses',entered:3,recoveries:2,heartbeats:41}];
const bridge=`<script>(()=>{let n=0;const pending=new Map();addEventListener('message',e=>{if(e.data?.type!=='router2api-response')return;const p=pending.get(e.data.id);if(!p)return;pending.delete(e.data.id);e.data.ok?p.resolve(e.data.body):p.reject(Error(e.data.error));});window.Router2API={request:(method,route,body)=>new Promise((resolve,reject)=>{const id=String(++n);pending.set(id,{resolve,reject});parent.postMessage({type:'router2api-request',id,method,route,body},'*');})};})();</script>`;
const html=(await pageHtml()).replace('<head>',()=>'<head>'+bridge);
const wrapper=`<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"><style>html,body{margin:0}iframe{width:100%;height:100vh;border:0;display:block}</style></head><body>
<iframe id="plugin" sandbox="allow-scripts allow-forms allow-popups allow-popups-to-escape-sandbox" title="RouterTasks 管理页面"></iframe>
<script>const frame=document.getElementById('plugin');frame.srcdoc=${JSON.stringify(html).replaceAll('<','\\u003c')};addEventListener('message',async e=>{if(e.source!==frame.contentWindow||e.data?.type!=='router2api-request')return;try{const r=await fetch('/api/plugins/router-tasks/'+e.data.route,{method:e.data.method,headers:{'Content-Type':'application/json'},body:e.data.body==null?undefined:JSON.stringify(e.data.body)});const body=await r.json();e.source.postMessage({type:'router2api-response',id:e.data.id,ok:r.ok,body,error:body.error},'*');}catch(error){e.source.postMessage({type:'router2api-response',id:e.data.id,ok:false,error:error.message},'*');}});</script></body></html>`;
const server=createServer(async (req,res)=>{
  if(req.url==='/'){res.writeHead(200,{'Content-Type':'text/html; charset=utf-8'});res.end(wrapper);return;}
  if(!req.url.startsWith('/api/plugins/router-tasks/')){res.writeHead(404);res.end();return;}
  try {let body='';for await(const part of req){body+=part;if(body.length>1048576)throw Error('Too large');}
    const value=await api.request(req.method,req.url.slice('/api/plugins/router-tasks/'.length),body?JSON.parse(body):undefined);
    res.writeHead(200,{'Content-Type':'application/json'});res.end(JSON.stringify(value));
  }catch(error){res.writeHead(400,{'Content-Type':'application/json'});res.end(JSON.stringify({error:error.message}));}
});
await new Promise(r=>server.listen(0,'127.0.0.1',r));
const origin='http://127.0.0.1:'+server.address().port;
let browser;
try {
  const options={headless:true};
  if(process.env.BROWSER_EXECUTABLE)options.executablePath=process.env.BROWSER_EXECUTABLE;
  else if(process.platform==='win32')options.channel='chrome';
  browser=await chromium.launch(options);
  const context=await browser.newContext({viewport:{width:1440,height:1100},locale:'zh-CN'});
  const errors=[],blocked=[];await context.route('**/*',route=>route.request().url().startsWith(origin)?route.continue():(blocked.push(route.request().url()),route.abort()));
  const page=await context.newPage();page.on('pageerror',e=>errors.push(e.message));
  await page.goto(origin);const frame=page.frameLocator('#plugin');await frame.locator('#taskList .task-card').first().waitFor();
  assert.equal(await frame.locator('nav button').count(),2);
  assert.equal(await frame.locator('#taskList .task-card').count(),5);
  assert.match(await frame.locator('#taskList').innerText(),/等待心跳确认/);checks.push('账号任务与请求模板双导航，三模式状态展示');
  const laneDetails=frame.locator('#taskList .task-card').first().locator('.lane-details');
  await laneDetails.locator('summary').click();
  const laneLayout=await laneDetails.evaluate(node=>({height:node.getBoundingClientRect().height,scrollHeight:node.scrollHeight,width:node.clientWidth,scrollWidth:node.scrollWidth,overflow:getComputedStyle(node).overflow}));
  assert.equal(laneLayout.overflow,'auto');
  assert.ok(laneLayout.scrollHeight>laneLayout.height,JSON.stringify(laneLayout));
  assert.ok(laneLayout.scrollWidth<=laneLayout.width+1&&laneLayout.height<=132,JSON.stringify(laneLayout));
  checks.push('子线程摘要在账号卡片内滚动，长错误不撑破卡片');
  await page.screenshot({path:resolve(out,'desktop.png')});
  assert.equal(await frame.locator('[data-bulk]:disabled').count(),4);
  const selectedCard=frame.locator('#taskList .task-card').nth(2);
  await selectedCard.locator('.task-head input[type=checkbox]').check();
  assert.equal(await frame.locator('[data-bulk]:enabled').count(),4);
  assert.equal(await frame.locator('#selectAll').evaluate(n=>n.indeterminate),true);
  const bulkStyle=await frame.locator('[data-bulk=resume]').evaluate(n=>({opacity:getComputedStyle(n).opacity,color:getComputedStyle(n).color}));
  assert.equal(bulkStyle.opacity,'1');assert.notEqual(bulkStyle.color,'rgb(124, 128, 148)');
  assert.deepEqual(await selectedCard.locator('.card-actions').evaluate(n=>[...n.children].map(c=>c.tagName==='DETAILS'?c.querySelector('summary').textContent:c.textContent)),['编辑','继续任务','日志','更多']);
  await page.screenshot({path:resolve(out,'desktop-selected.png')});
  await selectedCard.locator('.task-edit').click();await frame.locator('#taskDialog').waitFor({state:'visible'});
  assert.equal(await frame.locator('#taskEditorTitle').innerText(),'编辑账号');
  await frame.locator('#taskDialog [data-close=taskDialog]').first().click();
  assert.equal(await frame.locator('#taskList .task-card').first().locator('.task-edit').isDisabled(),true);
  checks.push('勾选即时高亮批量按钮，卡片按编辑、运行操作、日志、更多排序');
  await frame.locator('#selectAll').check();assert.equal(await frame.locator('.task-head input:checked').count(),5);
  await frame.locator('#selectAll').uncheck();assert.equal(await frame.locator('[data-bulk]:disabled').count(),4);
  checks.push('全选、部分选择与清空选择同步按钮状态');
  await frame.locator('#newAccount').click();await frame.locator('#taskDialog').waitFor({state:'visible'});
  assert.equal(await frame.locator('#claudeContextField').isVisible(),false);
  await frame.locator('#taskForm [name=templateId]').selectOption('messages');
  await frame.locator('#claudeContextField').waitFor({state:'visible'});
  assert.equal(await frame.locator('#taskForm [name=claude1MContext]').isChecked(),true);
  await frame.locator('#taskForm [name=claude1MContext]').uncheck();
  await frame.locator('#taskForm [name=templateId]').selectOption('responses');
  await frame.locator('#claudeContextField').waitFor({state:'hidden'});
  assert.equal(await frame.locator('#taskForm [name=claude1MContext]').isChecked(),false);
  checks.push('Claude 1M 上下文默认开启，可关闭，切换协议保留选择');
  await frame.locator('#taskForm [name=baseUrl]').fill('http://127.0.0.1');
  await frame.locator('#taskForm [name=token]').fill('TOKEN-browser-account');
  await frame.locator('#queryAccount').click();await frame.locator('#modelOptions option').first().waitFor({state:'attached'});
  assert.match(await frame.locator('#modelQueryStatus').innerText(),/2 个模型/);checks.push('添加账号时直接查询模型与额度');
  await frame.locator('#taskForm [name=model]').fill('MODEL');
  await frame.locator('#taskForm [name=prompt]').fill('测试保活');await frame.locator('#taskForm [name=prompt]').press('Tab');
  await frame.locator('#taskForm [name=autoRequeue]').uncheck();
  await frame.locator('#taskForm [name=queueHttpStatuses]').fill('500,429');
  await frame.locator('#taskForm .editor-body').evaluate(n=>n.scrollTop=0);
  await page.screenshot({path:resolve(out,'editor.png')});
  await frame.locator('[data-editor-target=queue]').click();await page.screenshot({path:resolve(out,'queue-settings.png')});
  await frame.locator('[data-editor-target=keepalive]').click();await frame.locator('#taskForm [name=k_maxAttemptsPerLane]').fill('500');
  await frame.locator('[data-editor-target=notify]').click();
  await frame.locator('#taskForm [name=telegramEnabled]').check();
  await frame.locator('#taskForm [name=telegramToken]').fill('BOT-browser-account');
  await frame.locator('#taskForm [name=telegramChatId]').fill('CHAT-browser-account');
  await frame.locator('#taskForm .editor-body').evaluate(n=>n.scrollTop=0);
  await page.screenshot({path:resolve(out,'account-notifications.png')});
  await frame.locator('#previewRequest').click();await frame.locator('#previewDialog').waitFor({state:'visible'});
  const preview=await frame.locator('#previewContent').innerText();
  assert.match(preview,/9007199254740993123456789/);assert.match(preview,/Bearer TOKEN-browser-account/);
  checks.push('完整凭据与无损请求预览');
  await frame.locator('[data-close=previewDialog]').click();
  await frame.locator('#taskForm button[type=submit]').click();await frame.locator('#taskDialog').waitFor({state:'hidden'});
  const saved=api.calls.find(c=>c.path==='accounts/save');
  assert.equal(saved.body.config.autoRequeue,false);assert.deepEqual(saved.body.site.queueHttpStatuses,[500,429]);
  assert.equal(saved.body.config.claude1MContext,false);
  assert.equal(saved.body.secrets.telegramChatId,'CHAT-browser-account');assert.equal(saved.body.config.settings.telegramEnabled,true);
  assert.match(saved.body.config.requestOverride.body,/9007199254740993123456789/);checks.push('账号连接、阶段参数与独立通知统一保存');
  await frame.locator('#taskList .task-card').last().locator('.task-logs').click();await frame.locator('#detailDialog').waitFor({state:'visible'});
  assert.match(await frame.locator('#detailTitle').innerText(),/日志统计/);await frame.locator('[data-close=detailDialog]').click();checks.push('任务卡片日志统计入口');
  await frame.locator('#taskList .task-card').last().locator('.task-more summary').click();
  await frame.locator('#taskList .task-card').last().getByRole('button',{name:'重新开始',exact:true}).click();
  await frame.locator('#confirmDialog').waitFor({state:'visible'});await frame.locator('#confirmYes').click();checks.push('页面内确认操作');
  await frame.locator('#taskList .task-card').last().locator('.task-more summary').click();
  await frame.locator('#taskList .task-card').last().getByRole('button',{name:'导出账号',exact:true}).click();
  await frame.locator('#exportDialog').waitFor({state:'visible'});
  const exported=await frame.locator('#exportText').inputValue();assert.match(exported,/TOKEN-browser-account/);assert.match(exported,/BOT-browser-account/);
  await frame.locator('[data-close=exportDialog]').first().click();
  await frame.locator('#importFile').setInputFiles({name:'account.json',mimeType:'application/json',buffer:Buffer.from(exported)});
  await frame.locator('#taskDialog').waitFor({state:'visible'});
  assert.equal(await frame.locator('#taskForm [name=token]').inputValue(),'TOKEN-browser-account');
  assert.equal(await frame.locator('#taskForm [name=telegramChatId]').inputValue(),'CHAT-browser-account');
  assert.notEqual(await frame.locator('#taskForm [name=id]').inputValue(),saved.body.config.id);
  await frame.locator('#taskDialog [data-close=taskDialog]').first().click();checks.push('账号导出与导入保留独立凭据及 JSON 精度');
  await frame.locator('#newAccount').click();await frame.locator('#taskDialog').waitFor({state:'visible'});
  assert.equal(await frame.locator('#taskForm [name=telegramEnabled]').isChecked(),false);
  assert.equal(await frame.locator('#taskForm [name=telegramToken]').inputValue(),'');
  await frame.locator('#taskDialog [data-close=taskDialog]').first().click();checks.push('新账号通知默认关闭且不复用其他账号接收方');
  await frame.locator('[data-page=templates]').click();
  for(const viewport of [{width:1280,height:720},{width:1024,height:600},{width:390,height:844},{width:390,height:600}]){
    await page.setViewportSize(viewport);await frame.locator('#templateList button').first().click();
    await frame.locator('#templateDialog').waitFor({state:'visible'});
    const layout=await frame.locator('#templateDialog').evaluate(dialog=>{
      const text=dialog.querySelector('textarea'),button=dialog.querySelector('button[type=submit]'),r=button.getBoundingClientRect();
      const hit=document.elementFromPoint(r.left+r.width/2,r.top+r.height/2);
      return {height:innerHeight,top:r.top,bottom:r.bottom,buttonVisible:hit===button||button.contains(hit),
        outerScroll:dialog.scrollHeight-dialog.clientHeight,editorHeight:text.clientHeight,editorScroll:text.scrollHeight-text.clientHeight};
    });
    assert.ok(layout.top>=0&&layout.bottom<=layout.height&&layout.buttonVisible,JSON.stringify({viewport,layout}));
    assert.ok(layout.outerScroll<=2&&layout.editorHeight<=330&&layout.editorScroll>0,JSON.stringify({viewport,layout}));
    if(viewport.width===1280)await page.screenshot({path:resolve(out,'template-desktop.png')});
    if(viewport.width===390&&viewport.height===600)await page.screenshot({path:resolve(out,'template-mobile.png')});
    await frame.locator('#templateDialog [data-close=templateDialog]').first().click();
  }
  checks.push('720px 与 600px 视口中模板保存按钮始终可见，仅 JSON 内部滚动');
  await page.setViewportSize({width:390,height:844});await frame.locator('[data-page=tasks]').click();
  await page.screenshot({path:resolve(out,'mobile.png')});
  const sizes=await frame.locator('html').evaluate(n=>({scroll:n.scrollWidth,width:n.clientWidth}));
  assert.ok(sizes.scroll<=sizes.width,JSON.stringify(sizes));
  await frame.locator('#newAccount').click();await frame.locator('#taskDialog').waitFor({state:'visible'});
  await page.screenshot({path:resolve(out,'mobile-editor.png')});
  const footer=await frame.locator('#taskForm button[type=submit]').boundingBox();
  assert.ok(footer&&footer.y+footer.height<=844);checks.push('390px 移动端无横向溢出，账号保存按钮可见');
  await frame.locator('#taskDialog [data-close=taskDialog]').first().click();
  const laneLayouts=[];
  for(const viewport of [{width:1440,height:1100},{width:1024,height:720},{width:768,height:720},{width:560,height:844},{width:390,height:844}]){
    await page.setViewportSize(viewport);
    await laneDetails.evaluate(node=>{node.open=true;node.scrollTop=0;});
    await laneDetails.scrollIntoViewIfNeeded();
    const layout=await laneDetails.evaluate(node=>{
      const r=node.getBoundingClientRect(),card=node.closest('.task-card'),c=card.getBoundingClientRect();
      return {open:node.open,height:r.height,clientWidth:node.clientWidth,scrollWidth:node.scrollWidth,
        scrollHeight:node.scrollHeight,insideCard:r.left>=c.left&&r.right<=c.right,
        cardOverflow:card.scrollWidth-card.clientWidth,pageOverflow:document.documentElement.scrollWidth-document.documentElement.clientWidth,
        textOverflow:Math.max(...[...node.querySelectorAll('p')].map(p=>p.scrollWidth-p.clientWidth))};
    });
    laneLayouts.push({viewport,...layout});
    assert.ok(layout.open&&layout.insideCard&&layout.height<=132&&layout.scrollHeight>layout.height,JSON.stringify({viewport,layout}));
    assert.ok(layout.scrollWidth<=layout.clientWidth+1&&layout.cardOverflow<=1&&layout.pageOverflow<=1&&layout.textOverflow<=1,JSON.stringify({viewport,layout}));
    if(viewport.width===1440||viewport.width===390)await frame.locator('#taskList .task-card').first().screenshot({path:resolve(out,'lanes-open-'+viewport.width+'.png')});
  }
  checks.push('展开子线程在五组视口内无横向溢出，超长错误完整换行且内部滚动');
  await page.setViewportSize({width:1440,height:1100});
  const deleteCard=frame.locator('.task-card[data-id="'+saved.body.config.id+'"]');
  await deleteCard.locator('.task-head input[type=checkbox]').check();
  const deletesBefore=api.calls.filter(c=>c.path==='accounts/delete').length;
  await frame.locator('[data-bulk=delete]').click();await frame.locator('#confirmDialog').waitFor({state:'visible'});
  await frame.locator('#confirmNo').click();await frame.locator('#confirmDialog').waitFor({state:'hidden'});
  assert.equal(api.calls.filter(c=>c.path==='accounts/delete').length,deletesBefore);
  await frame.locator('[data-bulk=delete]').click();await frame.locator('#confirmDialog').waitFor({state:'visible'});
  await frame.locator('#confirmYes').click();await deleteCard.waitFor({state:'detached'});
  assert.deepEqual(api.calls.filter(c=>c.path==='accounts/delete').map(c=>c.body.id),[saved.body.config.id]);
  assert.equal(await frame.locator('#selectionCount').innerText(),'已选 0 个');
  checks.push('删除所选在浏览器中取消不删除、确认仅删除选中的模拟账号');
  assert.deepEqual(errors,[]);assert.deepEqual(blocked,[]);
  assert.ok(api.calls.some(c=>c.path==='tasks/action'&&c.body.action==='restart'));
  checks.push('浏览器运行无错误');
  await writeFile(resolve(out,'browser-report.json'),JSON.stringify({at:new Date().toISOString(),browser:browser.version(),checks,laneLayouts,errors,blocked,scope:'本机模拟管理桥；与宿主一致的 iframe sandbox，未连接真实上游'},null,2));
  console.log(JSON.stringify({passed:checks.length,out,checks},null,2));
} finally {await browser?.close();await new Promise(r=>server.close(r));}

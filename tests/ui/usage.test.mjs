import test from 'node:test';
import assert from 'node:assert/strict';
import { harness, sampleConfig, until } from './harness.mjs';

const usageCalls = h => h.calls.filter(call=>call.path==='accounts/usage-logs');
const entry = (id,values={}) => ({id,at:'2026-10-20T16:00:00Z',type:2,modelName:'MODEL-'+id,tokenName:'测试令牌',quota:5,
  promptTokens:2,completionTokens:3,useTime:1,isStream:true,group:'default',content:'内容-'+id,other:'',...values});
async function accounts(h,ids=['a']) {
  for(const id of ids)h.data.tasks.push({config:sampleConfig(id,{name:'账号-'+id,siteId:'site',keyId:'key',model:'MODEL'}),configRevision:1,totalLanes:0,
    runtime:{status:'paused',phase:'queue',queueRound:0,totalAttempts:0,heartbeats:0,recoveries:0,sessionConfirmed:false,lanes:[]}});
  h.click('refresh');await until(()=>h.document.querySelectorAll('.task-card').length===ids.length);
}
function open(h,id='a') {const card=h.document.querySelector(`.task-card[data-id="${id}"]`);[...card.querySelectorAll('.menu-list button')].find(button=>button.textContent==='使用日志').click();}
const content = h => h.document.getElementById('usageContent');
const loaded = h => until(()=>h.document.getElementById('usageDialog').open&&h.document.getElementById('usageDialog').getAttribute('aria-busy')==='false');

test('账号菜单和日志统计都可打开当前账号的使用日志，只发送账号 ID',async t=>{
  const h=await harness();t.after(h.close);await accounts(h,['a','b']);
  h.data.usageResults={a:{items:[entry('a')]},b:{items:[entry('b')]}};
  open(h);await loaded(h);
  assert.match(h.document.getElementById('usageTitle').textContent,/账号-a/);
  assert.match(content(h).textContent,/MODEL-a/);assert.doesNotMatch(content(h).textContent,/MODEL-b/);
  assert.deepEqual(usageCalls(h)[0],{method:'POST',path:'accounts/usage-logs',body:{id:'a'}});
  h.document.querySelector('[data-close=usageDialog]').click();
  h.document.querySelector('.task-card[data-id="b"] .task-logs').click();await until(()=>h.document.getElementById('detailDialog').open);
  [...h.document.getElementById('detailContent').querySelectorAll('button')].find(button=>button.textContent==='使用日志').click();await loaded(h);
  assert.match(h.document.getElementById('usageTitle').textContent,/账号-b/);assert.match(content(h).textContent,/MODEL-b/);
  assert.deepEqual(usageCalls(h)[1].body,{id:'b'});
  assert.equal(h.calls.some(call=>['accounts/save','tasks/action','keys/refresh'].includes(call.path)),false);
  assert.deepEqual(h.errors,[]);
});

test('最近消费汇总仅计算类型 2，日志时间不受宿主校准影响，上游文本不执行 HTML',async t=>{
  const h=await harness();t.after(h.close);h.window.Date.now=()=>Date.parse('2026-10-20T16:10:00Z');
  h.data.serverNow='2026-10-20T16:05:00Z';await accounts(h);
  const injected='<img src=x onerror=alert(1)>';
  h.data.usageResult={items:[entry('1',{modelName:injected,tokenName:injected,content:injected,group:injected,other:injected}),
    entry('2',{promptTokens:null,completionTokens:7,quota:10}),entry('3',{type:1,quota:900,promptTokens:900,completionTokens:900})],
    queriedAt:'2026-10-20T16:05:00Z',lastRoute:'pool',lastProxy:{address:injected}};
  open(h);await loaded(h);
  assert.deepEqual([...h.document.querySelectorAll('#usageMetrics .metric strong')].map(node=>node.textContent),['2','2','10','15']);
  assert.equal(content(h).querySelector('tbody td').textContent,h.U.displayClock().format('2026-10-20T16:00:00Z'));
  assert.match(h.document.getElementById('usageStatus').textContent,/2026\/10\/21 00:10:00/);
  assert.match(content(h).textContent,/<img/);assert.equal(h.document.querySelectorAll('#usageDialog img').length,0);
  assert.equal(content(h).querySelectorAll('tbody tr').length,3);
  assert.match(h.document.getElementById('usageDialog').textContent,/额度为站点单位/);
  assert.doesNotMatch(h.document.getElementById('usageMetrics').textContent,/\$|USD/);
  h.data.usageResult={items:[entry('missing',{promptTokens:null,completionTokens:null,quota:null})]};h.click('refreshUsage');await loaded(h);
  assert.deepEqual([...h.document.querySelectorAll('#usageMetrics .metric strong')].map(node=>node.textContent),['1','—','—','—']);
  assert.deepEqual(h.errors,[]);
});

test('每页 50 条，搜索模型或内容与类型筛选在本地进行并重置页码',async t=>{
  const h=await harness();t.after(h.close);await accounts(h);
  h.data.usageResult={items:Array.from({length:51},(_,i)=>entry(String(i),{type:i===50?1:2}))};open(h);await loaded(h);
  assert.equal(content(h).querySelectorAll('tbody tr').length,50);assert.equal(h.document.getElementById('previousUsage').disabled,true);
  h.click('nextUsage');assert.equal(content(h).querySelectorAll('tbody tr').length,1);assert.equal(h.document.getElementById('nextUsage').disabled,true);
  const search=h.document.getElementById('usageSearch');search.value='MODEL-0';search.dispatchEvent(new h.window.Event('input',{bubbles:true}));
  assert.equal(content(h).querySelectorAll('tbody tr').length,1);assert.match(h.document.getElementById('usagePage').textContent,/1 \/ 1 · 1 条/);
  search.value='内容-50';search.dispatchEvent(new h.window.Event('input',{bubbles:true}));assert.match(content(h).textContent,/MODEL-50/);
  h.change(h.document.getElementById('usageType'),'2');assert.match(content(h).textContent,/没有匹配的记录/);
  search.value='';search.dispatchEvent(new h.window.Event('input',{bubbles:true}));assert.equal(content(h).querySelectorAll('tbody tr').length,50);
  h.change(h.document.getElementById('usageType'),'1');assert.equal(content(h).querySelectorAll('tbody tr').length,1);
  assert.equal(usageCalls(h).length,1);assert.equal(h.document.querySelector('#usageMetrics strong').textContent,'50');
  assert.deepEqual(h.errors,[]);
});

test('加载时防止重复请求，失败可刷新重试，成功空列表有明确提示',async t=>{
  const h=await harness();t.after(h.close);await accounts(h);const original=h.window.Router2API.request;let reject,count=0;
  h.window.Router2API.request=(method,path,body)=>path==='accounts/usage-logs'?(count++,new Promise((resolve,fail)=>reject=fail)):original(method,path,body);
  open(h);open(h);h.click('refreshUsage');h.click('refreshUsage');
  assert.equal(count,1);assert.equal(h.document.getElementById('refreshUsage').disabled,true);assert.match(h.document.getElementById('usageStatus').textContent,/正在查询/);
  reject(Error('站点不支持使用日志'));await loaded(h);
  assert.match(h.document.getElementById('usageError').textContent,/站点不支持/);assert.equal(h.document.getElementById('refreshUsage').disabled,false);
  h.window.Router2API.request=original;h.click('refreshUsage');await loaded(h);
  assert.equal(h.document.getElementById('usageError').textContent,'');assert.match(content(h).textContent,/站点暂无使用日志/);
  assert.equal(h.document.getElementById('nextUsage').disabled,true);assert.deepEqual(h.errors,[]);
});

test('关闭和切换账号会丢弃迟到响应与错误',async t=>{
  const h=await harness();t.after(h.close);await accounts(h,['a','b']);const original=h.window.Router2API.request,pending=[];
  h.window.Router2API.request=(method,path,body)=>path==='accounts/usage-logs'?new Promise((resolve,reject)=>pending.push({body,resolve,reject})):original(method,path,body);
  open(h);h.document.querySelector('[data-close=usageDialog]').click();open(h,'b');
  pending[1].resolve({items:[entry('b')],queriedAt:'2026-10-20T16:00:00Z'});await loaded(h);
  pending[0].resolve({items:[entry('a')]});await new Promise(resolve=>setTimeout(resolve,20));
  assert.match(content(h).textContent,/MODEL-b/);assert.doesNotMatch(content(h).textContent,/MODEL-a/);
  h.click('refreshUsage');open(h,'a');pending[2].reject(Error('过期错误'));
  assert.equal(h.document.getElementById('refreshUsage').disabled,true);
  pending[3].resolve({items:[entry('a')]});await loaded(h);
  assert.match(h.document.getElementById('usageTitle').textContent,/账号-a/);assert.match(content(h).textContent,/MODEL-a/);assert.equal(h.document.getElementById('usageError').textContent,'');
  h.click('refreshUsage');h.document.getElementById('usageDialog').dispatchEvent(new h.window.Event('cancel',{cancelable:true}));
  pending[4].resolve({items:[entry('late')]});await new Promise(resolve=>setTimeout(resolve,20));
  assert.equal(h.document.getElementById('usageDialog').open,false);assert.doesNotMatch(content(h).textContent,/MODEL-late/);assert.deepEqual(h.errors,[]);
});

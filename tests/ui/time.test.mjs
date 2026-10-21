import test from 'node:test';
import assert from 'node:assert/strict';
import { harness, sampleConfig, until } from './harness.mjs';

const localNow = Date.parse('2026-10-20T16:01:00Z');
const iso = value => new Date(value).toISOString();
const shanghai = value => new Date(value).toLocaleString('zh-CN', {timeZone:'Asia/Shanghai',hour12:false});

function task(hostNow) {
  return {config:sampleConfig('clock-task',{name:'时间测试',mode:'keepalive',siteId:'site',keyId:'key',model:'MODEL'}),configRevision:1,totalLanes:2,
    runtime:{status:'paused',phase:'keepalive',queueRound:1,totalAttempts:2,heartbeats:1,recoveries:0,sessionConfirmed:false,
      nextAttemptAt:iso(hostNow+120000),lastSuccessAt:iso(hostNow-30000),
      lanes:[{index:1,status:'waiting',attempts:2,nextAttemptAt:iso(hostNow+60000)}]},
    events:[{at:iso(hostNow-30000),kind:'accepted',message:'已接受',lane:1}]};
}

test('未校准的时间固定显示上海时区，跨日和空值正确',()=>{
  const clock=globalThis.RouterTasksUI.displayClock();
  for(const at of ['2026-10-20T15:59:59Z','2026-10-20T16:00:00Z'])assert.equal(clock.format(at),shanghai(at));
  assert.match(clock.format('2026-10-20T16:00:00Z'),/2026\/10\/21/);
  for(const at of [null,undefined,'','not-a-date'])assert.equal(clock.format(at),'—');
});

test('宿主慢五分钟或快三分钟时，按本机请求往返中点校准',()=>{
  for(const skew of [-300000,180000]){
    const clock=globalThis.RouterTasksUI.displayClock(),hostNow=localNow+skew;
    clock.sync(iso(hostNow),localNow-150,localNow+150);
    assert.equal(clock.format(iso(hostNow-60000)),shanghai(localNow-60000));
    assert.equal(clock.format(iso(hostNow+120000)),shanghai(localNow+120000));
  }
});

test('每次有效校准更新偏差，无效或缺失的参考时间保留已有校准',()=>{
  const clock=globalThis.RouterTasksUI.displayClock(),at=iso(localNow-30000);
  clock.sync(iso(localNow-300000),localNow,localNow);
  assert.equal(clock.format(at),shanghai(localNow+270000));
  clock.sync(iso(localNow+180000),localNow,localNow);
  assert.equal(clock.format(at),shanghai(localNow-210000));
  for(const args of [[null,localNow,localNow],['invalid',localNow,localNow],[iso(localNow),NaN,localNow],
    [iso(localNow),localNow,Infinity],[iso(localNow),localNow+1,localNow]]){
    clock.sync(...args);
    assert.equal(clock.format(at),shanghai(localNow-210000));
  }
});

test('账号卡片的下次请求、上次成功及子线程按本机时间校准',async t=>{
  const h=await harness();t.after(h.close);
  h.window.Date.now=()=>localNow;
  const hostNow=localNow-300000,entry=task(hostNow),raw=structuredClone(entry.runtime);
  h.data.serverNow=iso(hostNow);h.data.tasks.push(entry);h.click('refresh');
  await until(()=>h.document.querySelector('.task-card'));
  const card=h.document.querySelector('.task-card');
  assert.deepEqual([...card.querySelectorAll('.task-info>div')].map(n=>n.textContent),[
    '下次请求'+shanghai(localNow+120000),'上次成功'+shanghai(localNow-30000)]);
  assert.match(card.querySelector('.lane-details').textContent,new RegExp(shanghai(localNow+60000).replace(/[.*+?^${}()|[\]\\]/g,'\\$&')));
  assert.deepEqual(entry.runtime,raw);
  assert.deepEqual(h.errors,[]);
});

test('日志、通知和线程使用详情响应校准，刷新日志后重新校准',async t=>{
  const h=await harness();t.after(h.close);h.window.Date.now=()=>localNow;
  let hostNow=localNow-300000;
  const entry=task(hostNow);h.data.tasks.push(entry);h.click('refresh');
  await until(()=>h.document.querySelector('.task-card'));
  const update=()=>{
    h.data.serverNow=iso(hostNow);
    entry.events=[{at:iso(hostNow-30000),kind:'accepted',message:'已接受',lane:1}];
    entry.runtime.lanes[0].nextAttemptAt=iso(hostNow+60000);
    h.data.notifications=[{id:'notice',taskId:entry.config.id,state:'sent',title:'首次进入',channel:'bark',attempts:1,
      createdAt:iso(hostNow-20000),message:'通知内容'}];
  };
  const check=()=>{
    const content=h.document.getElementById('detailContent');
    assert.equal(content.querySelector('pre').textContent,shanghai(localNow-30000)+' [accepted] #1 已接受');
    assert.equal(content.querySelector('.notification p').textContent,'bark · 1 次 · '+shanghai(localNow-20000));
    assert.equal([...content.querySelectorAll('p.subtle')].find(n=>n.textContent.startsWith('#1')).textContent,
      '#1 · waiting · 2 次 · '+shanghai(localNow+60000));
  };
  update();h.document.querySelector('.task-logs').click();
  await until(()=>h.document.getElementById('detailDialog').open);check();
  hostNow=localNow+180000;update();
  const before=h.calls.filter(c=>c.path.startsWith('tasks/detail')).length;
  [...h.document.getElementById('detailContent').querySelectorAll('button')].find(n=>n.textContent==='刷新日志').click();
  await until(()=>h.calls.filter(c=>c.path.startsWith('tasks/detail')).length>before&&
    ![...h.document.getElementById('detailContent').querySelectorAll('button')].find(n=>n.textContent==='刷新日志').disabled);
  check();assert.deepEqual(h.errors,[]);
});

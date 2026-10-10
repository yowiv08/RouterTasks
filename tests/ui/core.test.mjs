import test from 'node:test';
import assert from 'node:assert/strict';
import { harness } from './harness.mjs';

test('协议固定选择 Responses 与 Messages 接口，模板 JSON 无需编辑路径', async t=>{
  const h=await harness();t.after(h.close);
  for(const protocol of ['responses','messages']){
    const template={...h.data.templates[0],protocol,path:'unused-custom-path'};
    assert.equal(h.U.wireTemplate(template).path,protocol);
    assert.equal(Object.hasOwn(JSON.parse(h.U.displayTemplate(template)),'path'),false);
  }
});

test('默认值、三种模式、HTTP 状态码继承和覆盖', async t=>{
  const h=await harness(); t.after(h.close); const u=h.U,d=u.defaults('task');
  assert.equal(d.mode,'queue_then_keepalive'); assert.equal(d.autoRequeue,true); assert.equal(d.queue.maxAttemptsPerLane,300);
  assert.equal(d.claude1MContext,true);
  assert.equal(d.queue.intervalSeconds,10); assert.equal(d.keepalive.heartbeatSeconds,120); assert.equal(d.keepalive.failureRetrySeconds,15);
  assert.equal(d.queue.firstEventTimeoutSeconds,60); assert.equal(d.keepalive.idleEvictionSeconds,300); assert.equal(d.keepalive.dropConfirmSeconds,120);
  assert.equal(u.parseCodes('',true),null); assert.equal(JSON.stringify(u.parseCodes('500， 429 500')),'[500,429]');
  assert.equal(JSON.stringify(u.parseCodes('')),'[]'); assert.throws(()=>u.parseCodes('50x'));assert.throws(()=>u.parseCodes('600'));
});

test('历史成功不能显示为当前保活，三阶段状态文案准确', async t=>{
  const h=await harness();t.after(h.close); const runtime={status:'running',phase:'keepalive',lastSuccessAt:new Date().toISOString(),sessionConfirmed:false};
  assert.equal(h.U.status({runtime}).label,'等待心跳确认');runtime.sessionConfirmed=true;assert.equal(h.U.status({runtime}).label,'保活中');
  runtime.status='paused';assert.equal(h.U.status({runtime}).label,'已暂停');runtime.status='recovering';assert.equal(h.U.status({runtime}).label,'掉线恢复中');
  runtime.status='running';runtime.phase='queue';assert.equal(h.U.status({runtime}).label,'排队中');
});

test('编辑模型和提示词不改变未知大整数、高精度小数及其他字段', async t=>{
  const h=await harness();t.after(h.close);let template=h.data.templates[0];
  template=h.U.setKnownField(template,'model','NEW-MODEL');template=h.U.setKnownField(template,'prompt','new {{TOKEN}} prompt');
  assert.match(template.body,/9007199254740993123456789/);assert.match(template.body,/1\.0000000000000000000001/);
  assert.match(template.body,/new \{\{TOKEN\}\} prompt/);assert.equal(JSON.parse(template.body).model,'NEW-MODEL');
  assert.equal(JSON.parse(template.body).input[0].type,'additional_tools');
  const text=h.U.displayTemplate(template),wire=h.U.wireTemplate(h.U.jsonObject(text,'模板'));
  assert.match(wire.body,/9007199254740993123456789/);
});

test('无损 JSON 解析拒绝非法语法，不碰字符串中的数字', async t=>{
  const h=await harness();t.after(h.close);
  for(const text of ['{bad}','{"x":01}','{"x":1e}','{"x":NaN}','{"x":1,}','[1]','null','0'])assert.throws(()=>h.U.jsonObject(text,'Body'));
  const input='{"a":"number 12345 and \\" escaped","b":-0,"c":1.234567890123456789e+90}';
  const result=h.U.stringify(h.U.jsonObject(input,'Body'));assert.match(result,/-0/);assert.match(result,/1\.234567890123456789e\+90/);assert.match(result,/12345/);
});

test('旧对象形式的配置导入也保留 Body 精度',async t=>{
  const h=await harness();t.after(h.close);
  const input='{"schemaVersion":1,"templates":[{"id":"t","body":{"stream":true,"id":9007199254740993999}}],"tasks":[],"settings":{},"sites":[]}';
  const data=h.U.importConfig(input);assert.equal(data.schemaVersion,1);assert.equal(typeof data.templates[0].body,'string');assert.match(data.templates[0].body,/9007199254740993999/);
});

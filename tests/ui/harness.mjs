import { readFile } from 'node:fs/promises';
import { JSDOM, VirtualConsole } from 'jsdom';
import '../../src/Plugins.RouterTasks/Web/core.js';

export const sampleConfig = (id, values = {}) => ({ ...globalThis.RouterTasksUI.defaults(id), ...values });

const root = new URL('../../src/Plugins.RouterTasks/', import.meta.url);
export async function pageHtml() {
  const read = file => readFile(new URL(file, root), 'utf8');
  const [html,style,core,app] = await Promise.all(['index.html','styles.css','core.js','app.js'].map(name=>read('Web/'+name)));
  return html.replace('/*__STYLE__*/', ()=>style).replace('/*__CORE__*/', ()=>core).replace('/*__SCRIPT__*/', ()=>app);
}
export async function fixtureData() {
  const templates = await Promise.all(['responses','messages'].map(async id => {
    const template = JSON.parse(await readFile(new URL('Contracts/' + id + '.json', root), 'utf8'));
    template.body = JSON.stringify(template.body); return template;
  }));
  templates[0].body = templates[0].body.replace(/}$/, ',"unknown":{"id":9007199254740993123456789,"decimal":1.0000000000000000000001}}');
  const settings = { maxEventsPerTask:200, summaryBytes:8192, historyDays:30, telegramEnabled:false, barkEnabled:false,
    barkServer:'https://api.day.app', notificationMaxAttempts:10, notificationRetrySeconds:5, notificationMaxRetrySeconds:3600, maxNotificationRecords:1000 };
  return { version:'0.3.2', revision:1, sites:[{id:'site',name:'模拟站点',baseUrl:'http://127.0.0.1',route:'direct',subscriptionIds:[],queueHttpStatuses:[500],revision:1}],
    keys:[{id:'key',siteId:'site',alias:'模拟账号',token:'TOKEN-fixture-only',credentialVersion:1,authStatus:'unchecked',models:['MODEL'],usedUsd:null,limitUsd:null}],
    templates, tasks:[], stats:[], notifications:[], settings, activeCount:0, accountSecrets:{}, notificationCredentials:{telegramConfigured:false,barkConfigured:false,credentialVersion:0} };
}
export function controller(data) {
  const calls = [];
  const clone = value => value === undefined ? undefined : structuredClone(value);
  const accountDetail = id => {
    const task=data.tasks.find(t=>t.config.id===id);if(!task)throw Error('账号不存在');
    const key=data.keys.find(k=>k.id===task.config.keyId);
    return clone({config:sampleConfig(id,{...task.config,settings:task.config.settings||data.settings}),
      site:data.sites.find(s=>s.id===task.config.siteId),token:key?.token||'TOKEN-fixture-only',credentialVersion:key?.credentialVersion||0,
      configRevision:task.configRevision||1,secrets:data.accountSecrets[id]||{telegramToken:'',telegramChatId:'',barkKey:'',credentialVersion:0}});
  };
  const request = async (method, path, body) => {
    calls.push({method,path,body:clone(body)});
    const [route,query] = path.split('?'), params = new URLSearchParams(query);
    if (method === 'GET' && route === 'state') return clone({...data, templates:data.templates.map(({id,name,protocol,path})=>({id,name,protocol,path}))});
    if (method === 'GET' && route === 'network/proxies') {if(data.proxyCatalogError)throw Error(data.proxyCatalogError);return clone(data.proxyCatalog||{subscriptions:[{id:'manual',name:'手动节点',enabled:true}],nodes:[]});}
    if (route === 'templates/detail') return clone(data.templates.find(t=>t.id===params.get('id')));
    if (route === 'tasks/detail') {const id=params.get('id'),task=data.tasks.find(t=>t.config.id===id);return clone({...task,serverNow:data.serverNow,events:task.events||[],stats:data.stats.filter(s=>s.taskId===id),notifications:data.notifications.filter(n=>n.taskId===id)});}
    if (route === 'accounts/detail') return accountDetail(params.get('id'));
    if (route === 'accounts/query') return {models:['MODEL-A','MODEL-B'],authStatus:'ready',usedUsd:1,limitUsd:10,unlimited:false,...clone(data.queryResult||{})};
    if (route === 'accounts/save') {
      const old=data.tasks.find(t=>t.config.id===body.config.id);
      if(old&&old.configRevision!==body.expectedRevision)throw Error('账号配置已更新');
      const keyId=old?.config.keyId||'key-'+body.config.id,siteId=old?.config.siteId||'site-'+body.config.id;
      const config=clone({...body.config,keyId,siteId}),site=clone({...body.site,id:siteId,revision:(body.site.revision||0)+1});
      data.sites=data.sites.filter(s=>s.id!==siteId).concat(site);
      data.keys=data.keys.filter(k=>k.id!==keyId).concat({id:keyId,siteId,alias:config.name,token:body.token,credentialVersion:(body.credentialVersion||0)+1,models:[]});
      data.accountSecrets[config.id]=clone({...body.secrets,credentialVersion:(body.secrets.credentialVersion||0)+1});
      data.tasks=data.tasks.filter(t=>t.config.id!==config.id).concat({config,configRevision:(old?.configRevision||0)+1,totalLanes:0,
        runtime:old?.runtime||{status:'paused',phase:config.mode==='keepalive'?'keepalive':'queue',queueRound:0,totalAttempts:0,heartbeats:0,recoveries:0,lanes:[],sessionConfirmed:false}});
      return accountDetail(config.id);
    }
    if (route === 'accounts/preview') return {method:'POST',url:body.site.baseUrl+'/v1/'+body.config.requestOverride.path,headers:{Authorization:'Bearer '+body.token},body:body.config.requestOverride.body};
    if (route === 'accounts/export') {const account=accountDetail(params.get('id'));account.config.requestOverride||=clone(data.templates.find(t=>t.id===account.config.templateId));return {schemaVersion:1,account};}
    if (route === 'tasks/save') {
      const task = {config:clone(body.config), configRevision:1, runtime:{status:'paused',phase:body.config.mode==='keepalive'?'keepalive':'queue',queueRound:0,totalAttempts:0,heartbeats:0,recoveries:0,lanes:[],sessionConfirmed:false},totalLanes:0};
      data.tasks = data.tasks.filter(t=>t.config.id!==body.config.id).concat(task); return clone(task);
    }
    if (route === 'tasks/action') return {results:body.ids.map(id=>({id,success:true}))};
    if (route === 'requests/preview') return {method:'POST',url:'http://127.0.0.1/v1/responses',headers:{Authorization:'[REDACTED]'},body:body.requestOverride.body};
    if (route === 'templates/save') {data.templates=data.templates.filter(t=>t.id!==body.id).concat(clone(body));return clone(body);}
    if (route === 'sites/save') {data.sites=data.sites.filter(s=>s.id!==body.id).concat(clone(body));return clone(body);}
    if (route === 'settings/save') {data.settings=clone(body.settings);return clone(data.notificationCredentials);}
    if (route === 'config/export') return clone({schemaVersion:1,sites:data.sites,templates:data.templates,tasks:data.tasks.map(t=>t.config),settings:data.settings});
    if (route === 'keys/reconcile') return {restored:0};
    if (route === 'keys/refresh') return clone(data.keys.find(k=>k.id===body.id));
    if (route === 'accounts/delete') {data.tasks=data.tasks.filter(t=>t.config.id!==body.id);return {success:true};}
    if (route === 'notifications/resume') {for(const n of data.notifications)if(n.taskId===body.id&&n.state==='paused')n.state='queued';return {success:true};}
    if (['tasks/delete','sites/delete','keys/delete','config/import','notifications/resume'].includes(route)) return {success:true};
    throw Error('Unexpected offline route ' + method + ' ' + path);
  };
  return {calls,request,data};
}
export async function until(condition) {
  const deadline=Date.now()+4000; while(!condition()) {if(Date.now()>deadline)throw Error('UI wait timed out');await new Promise(r=>setTimeout(r,10));}
}
export async function harness() {
  const data = await fixtureData(), api = controller(data), errors = [];
  const virtualConsole = new VirtualConsole(); virtualConsole.on('jsdomError', error => errors.push(error));
  const dom = new JSDOM(await pageHtml(), {url:'https://host.invalid/plugin',runScripts:'dangerously',virtualConsole,pretendToBeVisual:true,
    beforeParse(window) {
      window.Router2API={request:api.request};
      window.HTMLDialogElement.prototype.showModal=function(){this.setAttribute('open','');};
      window.HTMLDialogElement.prototype.close=function(){this.removeAttribute('open');};
      window.confirm=()=>{throw Error('Native confirm cannot run in host sandbox');};
    }});
  try { await until(()=>dom.window.document.getElementById('connectionText').textContent==='0 个后台任务'); }
  catch(error) { dom.window.close(); throw errors[0] || error; }
  return {...api,dom,errors,window:dom.window,document:dom.window.document,U:dom.window.RouterTasksUI,
    close:async()=>{await new Promise(r=>setTimeout(r,30));dom.window.close();},click:id=>dom.window.document.getElementById(id).click(),
    change:(field,value)=>{field.value=value;field.dispatchEvent(new dom.window.Event('change',{bubbles:true}));},
    submit:form=>form.dispatchEvent(new dom.window.Event('submit',{bubbles:true,cancelable:true}))};
}

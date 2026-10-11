'use strict';
const $ = (selector) => document.querySelector(selector);
const state = { token: null, user: null, demo: null, view: 'overview', items: [], zones: [], dashboard: null, scenarios: [], selected: null, analysis: null, result: null, page: 1, total: 0, busy: false };
const esc = (value) => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const words = (value) => String(value ?? '').replace(/([a-z])([A-Z])/g, '$1 $2');
const number = (value) => Number.isFinite(Number(value)) ? Math.round(Number(value)) : 0;
const date = (value) => value ? new Date(value).toLocaleString(undefined, {month:'short',day:'numeric',hour:'2-digit',minute:'2-digit'}) : '—';
const badge = (value) => `<span class="badge ${esc(String(value).replace(/\s/g,'').toLowerCase())}">${esc(words(value))}</span>`;
const isAuthority = () => state.user?.role === 'Authority';
const isCitizen = () => state.user?.role === 'Citizen';
const safeImage = (url) => typeof url === 'string' && /^\/(demo\/[a-z]+\.svg|uploads\/[a-f0-9]{32}\.(jpg|png|webp))$/.test(url) ? url : '';
function toast(message, error = false) { const el = $('#toast'); el.textContent = message; el.className = 'toast' + (error ? ' error' : ''); el.hidden = false; clearTimeout(toast.timer); toast.timer = setTimeout(() => el.hidden = true, error ? 8500 : 4500); }
async function api(path, options = {}) {
  const headers = {...(state.token ? {'Authorization':`Bearer ${state.token}`} : {}), ...(options.headers || {})};
  if (options.body && !(options.body instanceof FormData)) { headers['Content-Type'] = 'application/json'; options.body = JSON.stringify(options.body); }
  const response = await fetch(path, {...options, headers});
  let payload; try { payload = await response.json(); } catch { throw new Error('The server returned an unreadable response. Check the server console.'); }
  if (!response.ok || payload.success === false) {
    if (response.status === 401) throw new Error('Your demo session expired or the server restarted. Select a demo account again.');
    throw new Error(payload.error?.message || `Request failed (${response.status}).`);
  }
  return Object.hasOwn(payload, 'data') ? payload.data : payload;
}
async function work(fn) {
  if (state.busy) return;
  state.busy = true;
  const bar = document.createElement('div'); bar.className = 'busybar'; document.body.append(bar);
  const buttons = [...document.querySelectorAll('button, #role')].filter(el => !el.disabled);
  buttons.forEach(el => el.disabled = true);
  try { await fn(); } catch (error) { toast(error.message || 'Something went wrong.', true); }
  finally { buttons.forEach(el => el.disabled = false); document.querySelectorAll('button[data-busy]').forEach(el => {el.disabled=false; delete el.dataset.busy;}); bar.remove(); state.busy = false; }
}
async function login(role) {
  const account = state.demo.accounts.find(a => a.role === role);
  if (!account) throw new Error('This demo account is unavailable.');
  const auth = await api('/api/auth/login', {method:'POST',body:{email:account.email,password:account.password}});
  state.token = auth.token; state.user = auth.user; $('#role').value = auth.user.role; $('#avatar').textContent = auth.user.name.slice(0,1).toUpperCase();
  state.result = null; state.page = 1;
  for (const dialog of document.querySelectorAll('dialog[open]')) dialog.close();
  if (!isAuthority() && state.view === 'scenarios') state.view = 'overview';
  await refresh();
}
async function refresh() {
  const params = new URLSearchParams({page:state.page,pageSize:50});
  const search = $('#search')?.value?.trim(); const status = $('#status-filter')?.value;
  if (state.view === 'incidents' && search) params.set('search', search);
  if (state.view === 'incidents' && status) params.set('status', status);
  const [list,zones, dashboard] = await Promise.all([api('/api/incidents?'+params),api('/api/zones'),isAuthority()?api('/api/dashboard/summary'):Promise.resolve(null)]);
  state.items=list.items; state.total=list.total; state.zones=zones; state.dashboard=dashboard;
  if (state.view === 'scenarios') state.scenarios=await api('/api/demo/scenarios');
  render(search,status);
}
function heading(title,desc,action='') {return `<div class="page-heading"><div><span class="eyebrow">${esc(isAuthority()?'CITY INTELLIGENCE':isCitizen()?'CITIZEN WORKSPACE':'FIELD OPERATIONS')}</span><h1>${esc(title)}</h1><p>${esc(desc)}</p></div>${action}</div>`;}
function stat(label,value,foot,suffix='') {return `<div class="stat"><span class="stat-label">${esc(label)}</span><div class="stat-value">${esc(value)}${suffix?`<small>${esc(suffix)}</small>`:''}</div><span class="stat-foot">${esc(foot)}</span></div>`;}
function table(items) {
  if (!items.length) return '<div class="empty">No incidents match this view.<br>Try another filter or run a scenario in Demo studio.</div>';
  return `<div class="table-wrap"><table><thead><tr><th>INCIDENT / LOCATION</th><th>PRIORITY</th><th>STATUS</th><th>EVIDENCE</th><th>UPDATED</th></tr></thead><tbody>${items.map(i=>`<tr><td><button class="row-link" data-incident="${esc(i.id)}"><strong>${esc(i.categoryLabel)}</strong><span class="sub">${esc(i.code)} · ${esc(i.locationLabel || i.zoneName || 'Hyderabad')}</span></button></td><td><span class="score">${number(i.priorityScore)}</span> ${badge(i.priorityBand)}</td><td>${badge(i.status)}</td><td>${number(i.observationCount)} reports<span class="sub">${number(i.confirmationCount)} confirmations</span></td><td>${esc(date(i.lastUpdated))}</td></tr>`).join('')}</tbody></table></div>`;
}
function map() {
  const bounds = state.demo.city.mapBounds;
  return `<div class="map"><svg viewBox="0 0 800 320" preserveAspectRatio="none" aria-hidden="true"><rect width="800" height="320" fill="#f0f2eb"/><path d="M0 80L800 80M0 155L800 155M0 245L800 245M110 0L110 320M270 0L270 320M480 0L480 320M655 0L655 320" stroke="#fff" stroke-width="14"/><path d="M0 80L800 80M0 155L800 155M0 245L800 245M110 0L110 320M270 0L270 320M480 0L480 320M655 0L655 320" stroke="#dbe1d6" stroke-width="1"/><path d="M-20 320Q130 150 180 170T360 320" fill="none" stroke="#c1d8d2" stroke-width="31"/><rect x="490" y="165" width="150" height="66" rx="7" fill="#dce8d1"/><rect x="120" y="87" width="136" height="58" rx="7" fill="#e3e8dc"/><text x="145" y="42" fill="#939e8d" font-size="11" font-family="Arial">LAKDIKAPUL</text><text x="330" y="42" fill="#939e8d" font-size="11" font-family="Arial">ABIDS</text><text x="680" y="42" fill="#939e8d" font-size="11" font-family="Arial">KOTI</text><text x="520" y="200" fill="#8b9b81" font-size="10" font-family="Arial">CITY PARK</text></svg><span class="map-label">ILLUSTRATIVE MAP · CLICK AN INCIDENT</span>${state.items.slice(0,40).map(i=>{const x=Math.max(8,Math.min(94,8+(i.longitude-bounds.minLongitude)/(bounds.maxLongitude-bounds.minLongitude)*86));const y=Math.max(15,Math.min(87,87-(i.latitude-bounds.minLatitude)/(bounds.maxLatitude-bounds.minLatitude)*70));return `<button class="map-pin ${i.status==='VerifiedResolved'?'resolved':''}" style="left:${x}%;top:${y}%" data-incident="${esc(i.id)}" title="${esc(i.code+' · '+i.categoryLabel)}" aria-label="${esc(i.code+' '+i.categoryLabel)}"></button>`;}).join('')}<span class="map-legend">● Open incidents &nbsp; <span style="color:#5d9c76">●</span> Verified resolved</span></div>`;
}
function overview() {
  const d=state.dashboard; const health=number(d?.civicHealthScore ?? (state.zones.length?state.zones.reduce((s,z)=>s+z.healthScore,0)/state.zones.length:100));
  const reports=state.items.reduce((n,i)=>n+i.observationCount,0);
  return heading(isCitizen()?'Your city. Your voice.':state.user.role==='FieldWorker'?'Make progress visible.':'A clearer view of your city.',isCitizen()?'Report what matters. Help verify that the work is really done.':state.user.role==='FieldWorker'?'Review assigned work, track progress, and submit proof of resolution.':'Prioritize what matters. Connect citizen evidence to meaningful action.',isCitizen()?'<button class="primary" data-report>＋ Report an issue</button>':'<button class="secondary" data-refresh>↻ Refresh city</button>')+
  `<div class="stats">${stat(d?'Active incidents':state.user.role==='FieldWorker'?'Assigned incidents':'Incidents',d?.activeIncidents??state.total,'Live demo records')}${stat('Critical priority',d?.criticalIncidents??state.items.filter(i=>i.priorityBand==='Critical').length,'Context-aware prioritization')}${stat(d?'Awaiting verification':'Citizen observations',d?.awaitingVerification??reports,d?'Completion needs evidence':'Evidence in the current page')}${stat('City health',health,'Computed from zone health',' / 100')}</div><div class="grid"><section class="panel"><div class="panel-head"><div><h2>Incident landscape</h2><p>Explore observations across the demo city</p></div><span class="tag">${state.items.length} shown</span></div>${map()}</section><section class="panel"><div class="panel-head"><h2>Neighborhood health</h2><span class="tag">${state.zones.length} zones</span></div><div class="health-wrap"><div class="health-number">${health}<small> / 100</small></div><div class="health-bar"><span style="width:${Math.max(0,Math.min(100,health))}%"></span></div>${state.zones.map(z=>`<div class="zone">${esc(z.name)}<span>${number(z.healthScore)} / 100</span></div>`).join('')}<p class="section-note">Health reflects unresolved incident severity, evidence, and recurring issues.</p></div></section></div><section class="panel"><div class="panel-head"><div><h2>${state.user.role==='FieldWorker'?'Your assigned work':'Priority workspace'}</h2><p>Every report is a starting point. Verification closes the loop.</p></div><button class="secondary" data-view="incidents">View all →</button></div>${table(state.items.slice(0,7))}</section>`;
}
function incidents(search='', status='') {
  return heading('Incident workspace','Evidence, context, and progress in one place.',isCitizen()?'<button class="primary" data-report>＋ Report an issue</button>':'<button class="secondary" data-refresh>↻ Refresh</button>')+`<div class="toolbar"><input id="search" aria-label="Search incidents" placeholder="Search incident code or location…" value="${esc(search)}"><select id="status-filter" aria-label="Filter by status">${['','Reported','Validated','Assigned','Accepted','InProgress','AwaitingVerification','VerifiedResolved','RequiresReview','Reopened'].map(s=>`<option value="${s}" ${s===status?'selected':''}>${s?esc(words(s)):'All statuses'}</option>`).join('')}</select><button class="primary" data-filter>Apply filters</button></div><section class="panel">${table(state.items)}<div class="panel-head"><span class="muted">${state.total} incidents · Page ${state.page}</span><div class="actions"><button class="secondary" data-page="-1" ${state.page===1?'disabled':''}>← Previous</button><button class="secondary" data-page="1" ${state.page*50>=state.total?'disabled':''}>Next →</button></div></div></section>`;
}
function scenarios() {
 return heading('Make the story come alive.','Run the actual backend workflow with synthetic photos and deterministic demo AI.','<button class="danger" data-reset>↻ Reset demo city</button>')+`<div class="guide"><strong>Recommended jury story: 2 → 3 → 7 → 8</strong>Duplicate reports become one incident. Context raises priority near a school. Evidence verifies a repair. Ghost closures are disputed.<br>These are demo simulations—not live AI or real city reports.</div>${state.result?`<div class="scenario-result"><h3>${esc(state.result.name)}</h3><p>${esc(state.result.summary)}</p><ul>${state.result.highlights.map(h=>`<li>${esc(h)}</li>`).join('')}</ul>${state.result.incidentId?`<button class="primary" data-incident="${esc(state.result.incidentId)}">Explore ${esc(state.result.incidentCode||'incident')} →</button>`:''}</div>`:''}<div class="scenario-grid">${state.scenarios.map(s=>`<section class="panel scenario"><div class="scenario-top"><span class="scenario-number">SCENARIO ${String(s.id).padStart(2,'0')}</span><span class="tag">Live workflow</span></div><h3>${esc(s.name)}</h3><p>${esc(s.description)}</p><button class="primary" data-scenario="${s.id}">Run scenario →</button></section>`).join('')}</div>`;
}
function render(search='',status='') {
  $('#view-label').textContent={overview:'City overview',incidents:'Incident workspace',scenarios:'Demo studio'}[state.view];
  document.querySelectorAll('.nav').forEach(b=>{b.classList.toggle('active',b.dataset.view===state.view);b.hidden=b.dataset.view==='scenarios'&&!isAuthority();});
  $('#main').innerHTML=state.view==='overview'?overview():state.view==='incidents'?incidents(search,status):scenarios();
}
async function openIncident(id) {
  const detail=await api('/api/incidents/'+id); state.selected=detail;
  const s=detail.summary; const r=detail.resolution;
  let controls='';
  if (isAuthority()) {
    const workers=await api('/api/users/workers');
    if (['Reported','Validated','Reopened','RequiresReview'].includes(s.status)) controls+=`<form id="assign-form"><label>Assign a field worker<select name="workerId">${workers.map(w=>`<option value="${esc(w.id)}">${esc(w.name)} · ${w.activeAssignments} active</option>`).join('')}</select></label><button class="primary" type="submit" ${workers.length?'':'disabled'}>Assign incident</button></form>`;
  }
  if (detail.allowedNextStatuses?.length) controls+=`<div class="actions">${detail.allowedNextStatuses.filter(x=>x!=='Assigned').map(x=>`<button class="secondary" data-status="${esc(x)}">${esc(words(x))}</button>`).join('')}</div>`;
  if (detail.canConfirm) controls+='<button class="primary" data-confirm>I also see this issue</button>';
  if (detail.canVerifyResolution) controls+=`<h3 class="detail-heading">Is this issue actually fixed?</h3><div class="actions"><button class="primary" data-vote="Fixed">✓ Fixed</button><button class="danger" data-vote="StillExists">Still exists</button><button class="secondary" data-vote="Unsure">Unsure</button></div>`;
  if (state.user.role==='FieldWorker'&&s.status==='InProgress') controls+=`<form id="resolution-form"><h3 class="detail-heading">Submit proof of completion</h3><label>After photo<input type="file" name="afterImage" accept="image/jpeg,image/png,image/webp" required></label><label>Work notes<textarea name="notes" maxlength="1000"></textarea></label><button class="primary" type="submit">Submit for verification →</button></form>`;
  $('#detail').innerHTML=`<div class="dialog-heading"><div><span class="eyebrow">${esc(s.code)} · ${esc(s.zoneName||'CITY INCIDENT')}</span><h2>${esc(s.categoryLabel)}</h2><p class="muted">${esc(s.locationLabel||'Hyderabad demo city')}</p></div><button data-close="detail-dialog" class="icon-btn" aria-label="Close">×</button></div><div class="pill-row">${badge(s.status)}${badge(s.priorityBand)}${s.isRecurring?'<span class="tag">Recurring infrastructure issue</span>':''}</div>${safeImage(s.primaryImageUrl)?`<img class="detail-image" data-fallback="/demo/${esc(s.category.toLowerCase())}.svg" src="${esc(s.primaryImageUrl)}" alt="Incident evidence">`:''}<div class="detail-metrics"><div>Priority score<strong>${number(s.priorityScore)}</strong></div><div>Observations<strong>${number(s.observationCount)}</strong></div><div>Confirmations<strong>${number(s.confirmationCount)}</strong></div></div><p class="guide">${esc(detail.statusBanner)}<br>Assigned to: ${esc(s.assigneeName||'Not assigned')}</p><h3 class="detail-heading">Why this matters</h3><ul class="muted">${(detail.priority.reasons||[]).map(x=>`<li>${esc(x)}</li>`).join('')}</ul>${detail.recurrence?.isRecurring?`<div class="guide"><strong>Infrastructure memory</strong>${esc(detail.recurrence.summary)}</div>`:''}${r?`<h3 class="detail-heading">Resolution evidence</h3>${safeImage(r.afterImageUrl)?`<img class="detail-image" data-fallback="/demo/${r.verificationResult==='IssueNotDetected'?'fixed':esc(s.category.toLowerCase())}.svg" src="${esc(r.afterImageUrl)}" alt="After photo">`:''}<p class="muted">${esc(r.outcomeLabel)} · ${number(r.resolutionConfidencePercent)}% confidence<br>${esc(r.aiReason||r.neutralNote||'')}<br>Citizen checks: ${number(r.citizens.fixed)} fixed · ${number(r.citizens.stillExists)} still exists</p>`:''}${controls}<h3 class="detail-heading">Incident timeline</h3><div class="timeline">${detail.timeline.map(t=>`<div class="timeline-item">${esc(t.message)}<small>${esc(date(t.at))} · ${esc(words(t.eventType))}</small></div>`).join('')||'<p class="muted">No timeline events yet.</p>'}</div>`;
  if (!$('#detail-dialog').open) $('#detail-dialog').showModal();
}
async function mutate(path,body={}) {await api('/api/incidents/'+state.selected.summary.id+'/'+path,{method:'POST',body});toast('Incident updated.');await refresh();await openIncident(state.selected.summary.id);}
function showAnalysis(result) {
  state.analysis=result;
  $('#analysis-result').innerHTML=`<div class="analysis"><span class="eyebrow">REVIEW YOUR OBSERVATION</span><h3>${esc(result.detectedCategoryLabel)} · ${number(result.confidencePercent)}% confidence</h3><p class="muted">${esc(result.reason)}<br>Provider: ${esc(result.provider)} · Demo AI is simulated.</p>${result.warnings.map(w=>`<p class="guide">${esc(w)}</p>`).join('')}<label>Confirm the issue category<select id="category-choice">${result.alternatives.map(a=>`<option value="${esc(a.category)}" ${a.category===result.detectedCategory?'selected':''}>${esc(a.label)}</option>`).join('')}</select></label><p class="muted">Context risk: ${esc(result.context.locationRiskLabel)}. ${esc(result.context.reasons.join(' '))}</p><button class="primary" data-submit-new>Create a new incident →</button><h3 class="detail-heading">Possible existing incidents</h3><p class="muted">Join an existing report when it describes the same problem.</p><div id="matches">${matches(result.matches)}</div></div>`;
  $('#analysis-result').scrollIntoView({behavior:'smooth',block:'nearest'});
}
function matches(items) {return items.length?items.map(m=>`<div class="match"><div><strong>${esc(m.code)} · ${esc(m.categoryLabel)}</strong><span class="sub">${number(m.distanceMeters)} m away · ${number(m.sameIncidentPercent)}% match</span><p class="muted">${esc(m.reason)}</p></div><button class="secondary" data-attach="${esc(m.incidentId)}">Join report</button></div>`).join(''):'<p class="muted">No likely matches found nearby.</p>';}
async function submitObservation(id=null) {
  const result=await api('/api/observations',{method:'POST',body:{draftId:state.analysis.draftId,selectedCategory:$('#category-choice').value,description:$('#report-form [name=description]').value,action:id?'AttachToExisting':'CreateNew',incidentId:id}});
  $('#report-dialog').close();state.analysis=null;state.view='incidents';state.page=1;toast(result.message||'Observation submitted.');await refresh();await openIncident(result.incidentId);
}
document.addEventListener('click',event=>{
  const button=event.target.closest('button');if(!button||button.disabled)return;
  if(button.dataset.close){$('#'+button.dataset.close).close();return;}
  work(async()=>{
    if(button.dataset.view){state.view=button.dataset.view;state.page=1;await refresh();}
    else if(button.hasAttribute('data-refresh')) await refresh();
    else if(button.hasAttribute('data-filter')){state.page=1;await refresh();}
    else if(button.dataset.page){state.page+=Number(button.dataset.page);await refresh();}
    else if(button.dataset.incident) await openIncident(button.dataset.incident);
    else if(button.hasAttribute('data-report')){$('#report-form').reset();$('#analysis-result').innerHTML='';state.analysis=null;$('#report-dialog').showModal();}
    else if(button.dataset.scenario){state.result=await api('/api/demo/scenarios/'+button.dataset.scenario+'/run',{method:'POST'});await refresh();toast('Scenario complete. Explore the result above.');window.scrollTo({top:0,behavior:'smooth'});}
    else if(button.hasAttribute('data-reset')){if(!confirm('Reset all demo incidents and restore the seeded city?'))return;await api('/api/demo/reset',{method:'POST'});state.result=null;state.page=1;await refresh();toast('Demo city restored.');}
    else if(button.dataset.status)await mutate('status',{status:button.dataset.status,reason:'Updated during the live demo'});
    else if(button.hasAttribute('data-confirm'))await mutate('confirm');
    else if(button.dataset.vote)await mutate('verify',{vote:button.dataset.vote,latitude:state.selected.summary.latitude,longitude:state.selected.summary.longitude});
    else if(button.hasAttribute('data-submit-new'))await submitObservation();
    else if(button.dataset.attach)await submitObservation(button.dataset.attach);
  });
});
document.addEventListener('submit',event=>{
  event.preventDefault();
  work(async()=>{
    if(event.target.id==='report-form')showAnalysis(await api('/api/observations/analyze',{method:'POST',body:new FormData(event.target)}));
    if(event.target.id==='assign-form')await mutate('assign',{workerId:new FormData(event.target).get('workerId'),note:'Assigned during the live demo'});
    if(event.target.id==='resolution-form'){await api('/api/incidents/'+state.selected.summary.id+'/resolution',{method:'POST',body:new FormData(event.target)});toast('Evidence submitted for verification.');await refresh();await openIncident(state.selected.summary.id);}
  });
});
document.addEventListener('change',event=>{
  if(event.target.id==='role'){const role=event.target.value;work(async()=>{try{await login(role);}catch(error){$('#role').value=state.user?.role||'Authority';throw error;}});}
  if(event.target.id==='category-choice')work(async()=>{$('#matches').innerHTML=matches(await api('/api/observations/drafts/'+state.analysis.draftId+'/matches?category='+encodeURIComponent(event.target.value)));});
});
document.addEventListener('keydown',event=>{if(event.target.id==='search'&&event.key==='Enter'){event.preventDefault();work(async()=>{state.page=1;await refresh();});}});
work(async()=>{
  state.demo=await api('/api/demo/info');
  if(!state.demo.demoMode){$('#main').innerHTML='<div class="empty"><h2>Demo mode is not enabled.</h2>Use the Demo launch profile or follow the README to start the local MVP.</div>';return;}
  const city = state.demo.city;
  document.querySelector('.city-label').textContent = '● ' + city.name.toUpperCase() + ' DEMO CITY';
  for (const [field, value] of [['latitude', city.reportLatitude], ['longitude', city.reportLongitude], ['locationLabel', city.reportLocationLabel]]) {
    const input = document.querySelector('#report-form [name=' + field + ']'); input.defaultValue = value; input.value = value;
  }
  try{await login('Authority');}catch(error){$('#main').innerHTML='<div class="empty"><h2>Unable to load the city.</h2>Check the server console, then reload this page.</div>';throw error;}
});

// Synthetic scenario bytes are not actual photos; use labeled illustrations when decoding fails.
document.addEventListener('error', event => {
  const image = event.target;
  if (!(image instanceof HTMLImageElement) || !image.dataset.fallback) return;
  const fallback = image.dataset.fallback; delete image.dataset.fallback;
  image.src = fallback; image.alt = 'Demo illustration; synthetic evidence is not a real photo';
  const label = document.createElement('p'); label.className = 'muted'; label.textContent = 'Demo illustration · synthetic scenario evidence'; image.after(label);
}, true);

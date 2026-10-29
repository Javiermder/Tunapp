import { readFile } from 'node:fs/promises';
import { before, beforeEach, after, test } from 'node:test';
import { initializeTestEnvironment, assertFails, assertSucceeds } from '@firebase/rules-unit-testing';
import { doc, collection, getDoc, getDocs, setDoc, updateDoc, deleteDoc, writeBatch, serverTimestamp, Timestamp } from 'firebase/firestore';
const projectId = 'demo-tunapp-direct';
let env;
const now = Timestamp.fromDate(new Date('2026-10-01T00:00:00Z'));
const meta = (uid='owner') => ({ createdBy:uid, createdAt:serverTimestamp(), updatedAt:serverTimestamp() });
const plot = (uid='owner') => ({ name:'Olivar', areaM2:25000, trees:250, variety:'Picual', notes:'', ...meta(uid) });
const member = (uid,role) => ({uid,email:`${uid}@example.test`,role,createdAt:serverTimestamp(),updatedAt:serverTimestamp()});
const db = (uid='owner', verified=true) => env.authenticatedContext(uid,{email:`${uid}@example.test`,email_verified:verified}).firestore();
before(async()=> { env = await initializeTestEnvironment({projectId,firestore:{host:'127.0.0.1',port:8080,rules:(await readFile(new URL('../../firestore.rules',import.meta.url),'utf8')).replace(/^\uFEFF/,'')}}); });
beforeEach(async()=> {
  await env.clearFirestore();
  await env.withSecurityRulesDisabled(async context=> {
    const s=context.firestore();
    await setDoc(doc(s,'farms/a'),{name:'Familia',ownerUid:'owner',createdBy:'owner',createdAt:now,updatedAt:now});
    for(const [uid,role] of [['owner',2],['reader',0],['worker',1]]) {
      await setDoc(doc(s,`farms/a/members/${uid}`),{uid,email:`${uid}@example.test`,role,createdAt:now,updatedAt:now});
      await setDoc(doc(s,`users/${uid}/farms/a`),{farmId:'a',createdAt:now});
    }
    await setDoc(doc(s,'farms/a/campaigns/open'),{name:'2026/2027',start:now,end:Timestamp.fromDate(new Date('2027-09-30T00:00:00Z')),status:0,createdBy:'owner',createdAt:now,updatedAt:now});
    await setDoc(doc(s,'farms/a/campaigns/closed'),{name:'2025/2026',start:now,end:now,status:1,createdBy:'owner',createdAt:now,updatedAt:now});
    await setDoc(doc(s,'farms/a/plots/p'),{name:'Olivar',areaM2:25000,trees:250,variety:'Picual',notes:'',createdBy:'owner',createdAt:now,updatedAt:now});
    await setDoc(doc(s,'farms/a/campaignPlots/closed_p'),{campaignId:'closed',plotId:'p',areaM2:25000,trees:250,variety:'Picual',notes:'',createdBy:'owner',createdAt:now,updatedAt:now});
  });
});
after(async()=>{ await env?.cleanup(); });
const link=(campaign='open',uid='owner')=>({campaignId:campaign,plotId:'p',areaM2:24000,trees:240,variety:'Picual',notes:'',...meta(uid)});
const work=(overrides={})=>({campaignId:'open',plotId:'p',kind:0,date:now,description:'Aceituna',quantityMilli:125500,unit:'kg',cooperativeId:null,notes:'',...meta(),...overrides});
test('cooperativas y trabajos respetan permisos y campaña', async()=>{
  const d=db();
  await assertSucceeds(setDoc(doc(d,'farms/a/cooperatives/coop'),{name:'Cooperativa',notes:'',...meta()}));
  await assertSucceeds(setDoc(doc(d,'farms/a/fieldWorks/harvest'),work({cooperativeId:'coop'})));
  await assertSucceeds(setDoc(doc(d,'farms/a/fieldWorks/treatment'),work({kind:1,description:'Abonado',unit:'L'})));
  await assertSucceeds(getDocs(collection(db('reader'),'farms/a/fieldWorks')));
  await assertFails(setDoc(doc(db('reader'),'farms/a/fieldWorks/denied'),work({...meta('reader')})));
  await assertFails(setDoc(doc(db('other'),'farms/a/cooperatives/denied'),{name:'Ajena',notes:'',...meta('other')}));
  await assertFails(getDocs(collection(db('other'),'farms/a/fieldWorks')));
});
test('trabajos rechazan campaña cerrada, referencias ajenas y cantidades inválidas', async()=>{
  const d=db();
  for (const [id,change] of Object.entries({closed:{campaignId:'closed'},missing:{plotId:'other'},coop:{cooperativeId:'other'},quantity:{quantityMilli:0},unit:{unit:'L'},extra:{role:2},treatment:{kind:1,cooperativeId:'other'}}))
    await assertFails(setDoc(doc(d,`farms/a/fieldWorks/${id}`),work(change)));
  const b=writeBatch(d); b.update(doc(d,'farms/a/campaigns/open'),{status:1,updatedAt:serverTimestamp()});
  b.set(doc(d,'farms/a/fieldWorks/closing'),work()); await assertFails(b.commit());
});
test('sin sesión no se leen ni se escriben fincas',async()=> {const d=env.unauthenticatedContext().firestore();await assertFails(getDoc(doc(d,'farms/a/plots/p')));await assertFails(setDoc(doc(d,'farms/a/plots/x'),plot()));});
test('usuario ajeno no accede a la explotación',async()=>{const d=db('other');await assertFails(getDoc(doc(d,'farms/a')));await assertFails(getDocs(collection(d,'farms/a/plots')));});
test('lector consulta sus fincas y campañas',async()=>{const d=db('reader');await assertSucceeds(getDocs(collection(d,'farms/a/plots')));await assertSucceeds(getDoc(doc(d,'farms/a/campaigns/open')));});
test('lector no escribe ni eleva permisos',async()=>{const d=db('reader');await assertFails(setDoc(doc(d,'farms/a/plots/x'),plot('reader')));await assertFails(updateDoc(doc(d,'farms/a/members/reader'),{role:2,updatedAt:serverTimestamp()}));});
test('no se enumeran explotaciones ni correos de otros miembros',async()=>{await assertFails(getDocs(collection(db(),'farms')));await assertFails(getDocs(collection(db('reader'),'farms/a/members')));await assertFails(getDoc(doc(db('reader'),'farms/a/members/owner')));});
test('operario crea una finca permanente',async()=>{await assertSucceeds(setDoc(doc(db('worker'),'farms/a/plots/new'),plot('worker')));});
test('reglas validan superficie, olivos y campos extra',async()=>{const d=db();await assertFails(setDoc(doc(d,'farms/a/plots/x'),{...plot(),areaM2:-1}));await assertFails(setDoc(doc(d,'farms/a/plots/x'),{...plot(),trees:-1}));await assertFails(setDoc(doc(d,'farms/a/plots/x'),{...plot(),role:2}));});
test('finca y campaña deben pertenecer a la misma explotación',async()=>{await assertFails(setDoc(doc(db(),'farms/a/campaignPlots/missing_p'),link('missing')));await assertFails(setDoc(doc(db(),'farms/a/campaignPlots/open_missing'),{...link(),plotId:'missing'}));});
test('vincular finca conserva datos anuales independientes',async()=>{await assertSucceeds(setDoc(doc(db(),'farms/a/campaignPlots/open_p'),link()));});
test('campaña cerrada bloquea nuevas vinculaciones y ediciones',async()=>{await assertFails(setDoc(doc(db('worker'),'farms/a/campaignPlots/closed_p2'),{...link('closed','worker'),plotId:'p2'}));await assertFails(updateDoc(doc(db(),'farms/a/campaignPlots/closed_p'),{trees:1,updatedAt:serverTimestamp()}));});
test('operario no cierra ni reabre campañas',async()=>{await assertFails(updateDoc(doc(db('worker'),'farms/a/campaigns/open'),{status:1,updatedAt:serverTimestamp()}));await assertFails(updateDoc(doc(db('worker'),'farms/a/campaigns/closed'),{status:0,updatedAt:serverTimestamp()}));});
test('administrador cierra y reabre campañas',async()=>{await assertSucceeds(updateDoc(doc(db(),'farms/a/campaigns/open'),{status:1,updatedAt:serverTimestamp()}));await assertSucceeds(updateDoc(doc(db(),'farms/a/campaigns/open'),{status:0,updatedAt:serverTimestamp()}));});
test('no se modifica una campaña cerrándola en el mismo lote',async()=>{const d=db();const b=writeBatch(d);b.update(doc(d,'farms/a/campaigns/open'),{status:1,updatedAt:serverTimestamp()});b.set(doc(d,'farms/a/campaignPlots/open_p'),link());await assertFails(b.commit());});
test('crear explotación exige propietario, membresía e índice atómicos',async()=>{const d=db('new');await assertFails(setDoc(doc(d,'farms/new'),{name:'Nueva',ownerUid:'new',...meta('new')}));const b=writeBatch(d);b.set(doc(d,'farms/new'),{name:'Nueva',ownerUid:'new',...meta('new')});b.set(doc(d,'farms/new/members/new'),member('new',2));b.set(doc(d,'users/new/farms/new'),{farmId:'new',createdAt:serverTimestamp()});await assertSucceeds(b.commit());});
test('no se puede apropiar una explotación existente ni forjar su índice',async()=>{const d=db('other');await assertFails(setDoc(doc(d,'users/other/farms/a'),{farmId:'a',createdAt:serverTimestamp()}));const b=writeBatch(d);b.set(doc(d,'farms/a/members/other'),member('other',2));b.set(doc(d,'users/other/farms/a'),{farmId:'a',createdAt:serverTimestamp()});await assertFails(b.commit());});
async function invitation() { const d=db();await setDoc(doc(d,'invitations/new@example.test/farms/a'),{farmId:'a',farmName:'Familia',email:'new@example.test',role:0,createdBy:'owner',createdAt:serverTimestamp()}); }
function acceptance(verified=true,role=0) {const d=db('new',verified);const b=writeBatch(d);b.set(doc(d,'farms/a/members/new'),member('new',role));b.set(doc(d,'users/new/farms/a'),{farmId:'a',createdAt:serverTimestamp()});b.delete(doc(d,'invitations/new@example.test/farms/a'));return b;}
test('solo administrador invita usuarios',async()=>{const data={farmId:'a',farmName:'Familia',email:'new@example.test',role:0,createdBy:'reader',createdAt:serverTimestamp()};await assertFails(setDoc(doc(db('reader'),'invitations/new@example.test/farms/a'),data));await invitation();});
test('solo el destinatario verificado puede leer y aceptar invitación',async()=>{await invitation();await assertFails(getDoc(doc(db('other'),'invitations/new@example.test/farms/a')));await assertFails(acceptance(false).commit());await assertSucceeds(acceptance().commit());});
test('aceptar invitación no permite ampliar el permiso concedido',async()=>{await invitation();await assertFails(acceptance(true,2).commit());});
test('aceptar exige consumir la invitación para impedir reutilización tras revocar',async()=>{await invitation();const d=db('new');const b=writeBatch(d);b.set(doc(d,'farms/a/members/new'),member('new',0));b.set(doc(d,'users/new/farms/a'),{farmId:'a',createdAt:serverTimestamp()});await assertFails(b.commit());});
test('el propietario no puede ser eliminado ni degradado',async()=>{const d=db();await assertFails(updateDoc(doc(d,'farms/a/members/owner'),{role:0,updatedAt:serverTimestamp()}));const b=writeBatch(d);b.delete(doc(d,'farms/a/members/owner'));b.delete(doc(d,'users/owner/farms/a'));await assertFails(b.commit());});
test('revocar miembro exige eliminar índice y bloquea futuras lecturas',async()=>{const d=db();await assertFails(deleteDoc(doc(d,'farms/a/members/reader')));const b=writeBatch(d);b.delete(doc(d,'farms/a/members/reader'));b.delete(doc(d,'users/reader/farms/a'));await assertSucceeds(b.commit());await assertFails(getDoc(doc(db('reader'),'farms/a/plots/p')));});
test('no se cambia propietario ni procedencia del registro',async()=>{const d=db();await assertFails(updateDoc(doc(d,'farms/a'),{ownerUid:'other',updatedAt:serverTimestamp()}));await assertFails(updateDoc(doc(d,'farms/a/plots/p'),{createdBy:'worker',updatedAt:serverTimestamp()}));});

test('optional campaign dates and area remain valid with permissions', async()=> {
  const d=db();
  for (const [id,start,end] of [['none',null,null],['start',now,null],['end',null,now]])
    await assertSucceeds(setDoc(doc(d,`farms/a/campaigns/${id}`),{name:id,start,end,status:0,...meta()}));
  await assertSucceeds(setDoc(doc(d,'farms/a/plots/optional'),{...plot(),areaM2:null}));
  await assertSucceeds(setDoc(doc(d,'farms/a/campaignPlots/open_p'),{...link(),areaM2:null}));
  await assertFails(setDoc(doc(db('reader'),'farms/a/plots/denied'),{...plot('reader'),areaM2:null}));
  await assertFails(setDoc(doc(d,'farms/a/campaigns/reversed'),{name:'Invalid',start:Timestamp.fromDate(new Date('2027-01-01')),end:now,status:0,...meta()}));
});

test('calidad admite arbol y suelo conservando registros anteriores', async()=>{
  const d=db();
  await assertSucceeds(setDoc(doc(d,'farms/a/fieldWorks/tree'),work({quality:'Árbol'})));
  await assertSucceeds(setDoc(doc(d,'farms/a/fieldWorks/ground'),work({quality:'Suelo'})));
  await assertSucceeds(setDoc(doc(d,'farms/a/fieldWorks/legacy'),work()));
  for (const [id,change] of Object.entries({bad:{quality:'Otra'},numeric:{quality:0},nullQuality:{quality:null},treatmentQuality:{kind:1,quality:'Suelo'}}))
    await assertFails(setDoc(doc(d,`farms/a/fieldWorks/${id}`),work(change)));
});

test('precios por calidad y costes respetan validacion y permisos', async()=>{
 const d=db();
 await setDoc(doc(d,'farms/a/cooperatives/coop'),{name:'Coop',notes:'',...meta()});
 const price=(overrides={})=>({campaignId:'open',cooperativeId:'coop',quality:'Suelo',priceTenThousandths:7525,...meta(),...overrides});
 const target=doc(d,'farms/a/cooperativePrices/open_coop_ground');
 await assertSucceeds(setDoc(target,price()));
 await assertSucceeds(updateDoc(target,{priceTenThousandths:0,updatedAt:serverTimestamp()}));
 await assertSucceeds(getDocs(collection(db('reader'),'farms/a/cooperativePrices')));
 await assertFails(getDocs(collection(db('other'),'farms/a/cooperativePrices')));
 await assertFails(setDoc(doc(db('reader'),'farms/a/cooperativePrices/open_coop_ground'),price({...meta('reader')})));
 for (const change of [{priceTenThousandths:-1},{priceTenThousandths:0.1},{cooperativeId:'missing'},{quality:'Otra'},{campaignId:'closed'}])
   await assertFails(setDoc(target,price(change)));
 await assertFails(setDoc(doc(d,'farms/a/cooperativePrices/incorrect'),price()));
 await assertSucceeds(setDoc(doc(d,'farms/a/fieldWorks/cost'),work({kind:1,costCents:1250})));
 await assertSucceeds(setDoc(doc(d,'farms/a/fieldWorks/free'),work({kind:1,costCents:0})));
 for (const [id,change] of Object.entries({negative:{kind:1,costCents:-1},fraction:{kind:1,costCents:1.5},harvest:{costCents:100}}))
   await assertFails(setDoc(doc(d,`farms/a/fieldWorks/${id}`),work(change)));
 await updateDoc(doc(d,'farms/a/campaigns/open'),{status:1,updatedAt:serverTimestamp()});
 await assertFails(updateDoc(target,{priceTenThousandths:100,updatedAt:serverTimestamp()}));
});

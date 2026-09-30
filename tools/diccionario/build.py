"""Arma el corpus de chats de venta: Evolution (nuestras líneas) + sales-hub (leads con estado).
Salida: chats.jsonl (un chat por línea, mensajes ordenados y deduplicados) + stats en stdout."""
import paths; paths.use_data()
import csv, gzip, json, re, sys
from collections import defaultdict
from datetime import datetime, timezone

csv.field_size_limit(sys.maxsize)
STATUS = {0:'New',1:'Assigned',2:'Queued',3:'Sent',4:'Replied',5:'Interested',6:'DemoScheduled',7:'Closed',8:'Lost',9:'Blocked',10:'NoWhatsApp'}
SALES_MARK = re.compile(r'gym ?hero|turnos ?pro|unistock|playcrew|archicloud|bunker|efcloud|prueba gratis|d[ií]as gratis|te creo la cuenta|sistema de gesti|tu (gimnasio|gym|negocio|centro|local|estudio|obra|complejo|consultorio)|tus (alumnos|socios|clientes|pacientes)|nuestra propuesta', re.I)
NOTIF = re.compile(r'^\W{0,3}\*[A-ZÁÉÍÓÚÑ ]{4,}( - |\*)')
IN_MARK = re.compile(r'gym ?hero|turnos ?pro|unistock|playcrew|archicloud|efcloud|morosidad|activar .*(gym|gimnasio|sistema)|quiero (m[aá]s )?info|dejar el excel|prueba gratis|(mi|el) (gimnasio|gym)', re.I)

def key_of(phone):
    d = re.sub(r'\D', '', phone or '')
    return d[-10:] if len(d) >= 8 else None

# ── leads ──
leads, lead_by_key = {}, {}
with gzip.open('hub_leads.csv.gz', 'rt') as f:
    for r in csv.DictReader(f):
        k = key_of(r['whatsapp_phone'] or r['raw_phone'])
        r['_key'] = k
        leads[r['id']] = r
        if k:
            prev = lead_by_key.get(k)
            # si hay duplicados, nos quedamos con el de estado más avanzado
            if prev is None or int(r['status']) in (5,6,7) or int(prev['status']) < int(r['status']) < 8:
                lead_by_key[k] = r

chats = defaultdict(dict)   # key -> {dedup_key: msg}
names = {}
insts = defaultdict(set)

def add(k, from_me, ts, text, mid, inst, mtype):
    text = (text or '').strip()
    if not text:
        text = f'[{mtype}]' if mtype and mtype not in ('conversation','extendedTextMessage') else ''
    if not text or mtype in ('protocolMessage','reactionMessage','pollUpdateMessage','editedMessage'):
        return
    dk = mid or f'{from_me}|{ts//10}|{text[:40]}'
    # el mismo mensaje entra por 2 líneas de la misma cuenta y también por sales-hub
    dk2 = f'{from_me}|{ts//30}|{text[:60]}'
    c = chats[k]
    if dk in c or dk2 in c: return
    m = {'me': from_me, 'ts': ts, 't': text, 'i': inst}
    c[dk] = m; c[dk2] = m
    insts[k].add(inst)

with gzip.open('evo.csv.gz', 'rt') as f:
    for r in csv.DictReader(f):
        jid = r['jid'] or ''
        phone = r['jid_alt'] if jid.endswith('@lid') and r['jid_alt'] else jid
        k = key_of(phone.split('@')[0]) if not phone.endswith('@lid') else 'lid:' + jid
        if not k: continue
        try: ts = int(float(r['ts']))
        except: continue
        add(k, r['from_me'] == 't', ts, r['text'], r['msg_id'], r['instance'], r['mtype'])
        if r['from_me'] != 't' and r['push_name'] and r['push_name'] != '.':
            names[k] = r['push_name']

with gzip.open('hub_msgs.csv.gz', 'rt') as f:
    for r in csv.DictReader(f):
        l = leads.get(r['lead_id'])
        if not l or not l['_key']: continue
        ts = int(datetime.strptime(r['timestamp'][:19], '%Y-%m-%d %H:%M:%S').replace(tzinfo=timezone.utc).timestamp())
        add(l['_key'], r['direction'] == '0', ts, r['text'], r['whatsapp_message_id'] or None, r['evolution_instance'] or 'hub', 'conversation')

out, kept, dropped = open('chats.jsonl', 'w'), 0, 0
for k, c in chats.items():
    msgs = sorted({id(m): m for m in c.values()}.values(), key=lambda m: m['ts'])
    lead = lead_by_key.get(k) if not k.startswith('lid:') else None
    ours = ' '.join(m['t'] for m in msgs if m['me'])
    theirs = ' '.join(m['t'] for m in msgs if not m['me'])
    if not lead and not SALES_MARK.search(ours) and not IN_MARK.search(theirs):
        dropped += 1; continue   # chat personal de un vendedor
    notif = sum(bool(NOTIF.match(m['t'])) for m in msgs)
    if notif >= 5 and notif >= 0.3 * len(msgs):
        dropped += 1; continue   # canal de avisos del tracker / admin, no es un lead
    kept += 1
    out.write(json.dumps({
        'k': k, 'name': (lead or {}).get('name') or names.get(k),
        'product': (lead or {}).get('product_key'), 'source': (lead or {}).get('source'),
        'status': STATUS.get(int(lead['status'])) if lead else None,
        'demo_at': (lead or {}).get('demo_scheduled_at') or None,
        'lines': sorted(insts[k]), 'msgs': msgs}, ensure_ascii=False) + '\n')
print(f'chats de venta: {kept}  descartados (personales/sin marca): {dropped}')

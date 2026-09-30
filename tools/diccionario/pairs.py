"""Pares entrada→salida: cada 'turno' del lead (mensajes seguidos) → nuestra respuesta siguiente
(mensajes seguidos) → si el lead volvió a escribir en 7 días. Salida: pairs.jsonl"""
import paths; paths.use_data()
import json, re
from norm import norm
SALES_OUR = re.compile(r'gym ?hero|turnos ?pro|unistock|playcrew|archicloud|bunker|prueba gratis|d[ií]as gratis|te creo la cuenta|tu (gimnasio|gym|negocio|centro|estudio|obra|complejo)|tus (alumnos|socios|clientes)|c[oó]mo (llev[aá]s|cobr[aá]s|tom[aá]s) ', re.I)
SALES_IN = re.compile(r'gym ?hero|turnos ?pro|unistock|playcrew|archicloud|morosidad|activar .*(gym|gimnasio|sistema|negocio)|quiero (m[aá]s )?info|dejar el excel|prueba gratis|precio|cu[aá]nto (sale|cuesta)', re.I)
CUSTOMER = re.compile(r'comprobante|ya (te )?(pagu|abon|transfer)|me venci[oó]|renov|\bactivad[oa]\b|bienvenid[oa]s|pago recibido', re.I)
HOT_SOURCES = {'400','401','402','403','404'}
kept_chats = 0
DAY=86400
out=open('pairs.jsonl','w'); n=0
for l in open('chats.jsonl'):
    c=json.loads(l); ms=c['msgs']
    head=ms[:6]   # una charla de venta ARRANCA como venta (opener, anuncio o formulario)
    ours=' '.join(m['t'] for m in head if m['me']); theirs=' '.join(m['t'] for m in head if not m['me'])
    if not (SALES_OUR.search(ours) or SALES_IN.search(theirs) or c.get('source') in HOT_SOURCES): continue
    # Desde que es cliente, lo que sigue es soporte (otro bot): se corta ahí.
    cut = next((i for i,m in enumerate(ms) if CUSTOMER.search(m['t'])), None)
    if cut is not None: ms = ms[:cut]
    if not any(not m['me'] for m in ms): continue
    kept_chats += 1
    # agrupar en turnos
    turns=[]
    for m in ms:
        if turns and turns[-1]['me']==m['me'] and m['ts']-turns[-1]['end']<3600:
            turns[-1]['t'].append(m['t']); turns[-1]['end']=m['ts']
        else:
            turns.append({'me':m['me'],'t':[m['t']],'start':m['ts'],'end':m['ts']})
    for i,t in enumerate(turns):
        if t['me']: continue
        nxt=turns[i+1] if i+1<len(turns) else None
        reply=nxt if nxt and nxt['me'] else None
        after=turns[i+2] if reply and i+2<len(turns) else None
        cont = bool(after and not after['me'] and after['start']-reply['end']<7*DAY)
        idx=sum(1 for x in turns[:i] if not x['me'])
        prev=turns[i-1] if i>0 and turns[i-1]['me'] else None   # nro de turno del lead en la charla
        out.write(json.dumps({'k':c['k'],'product':c['product'],'status':c['status'],'turn':idx,
            'prev':' / '.join(prev['t'][-2:]) if prev else None,
            'in':' / '.join(t['t']),'in_n':norm(' '.join(t['t'])),
            'out':' / '.join(reply['t'][:4]) if reply else None,
            'delay_min':round((reply['start']-t['end'])/60) if reply else None,
            'continued':cont},ensure_ascii=False)+'\n'); n+=1
print('charlas de venta:', kept_chats, '| pares:', n)

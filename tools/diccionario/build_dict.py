"""Genera diccionario.json: tipo de mensaje -> patrones, acción, respuesta sugerida, métricas medidas."""
import paths; paths.use_data()
import json, statistics as st
from intents import INTENTS, classify
P=[json.loads(l) for l in open('pairs.jsonl')]
for p in P: p['intent']=classify(p['in'], p.get('prev'))
N=len(P)
# Respuesta sugerida por tipo. {vendedor} {producto} {precio} {link} {h1} {h2} se completan en runtime.
# Estilo: minúscula, sin ¿ ni emojis, corto (docs/bases-de-venta-2026-09.md §2).
REPLY = {
 'autorespuesta': None,
 'multimedia': None,
 'email': '{listo|genial|perfecto}, {ya te cree la cuenta|te deje la cuenta armada}. {entra de aca|te dejo el acceso}, es directo: {link}',
 'email_incompleto': '{me lo pasas completo|me pasas el mail completo}? {con el @ y lo que sigue|con el arroba}',
 'anuncio': '{buenas|hola|buen dia}! soy {vendedor} de {producto}. {te hago un par de preguntas rapidas para ver como te ayudo|te hago dos preguntas asi te ayudo mejor}',
 'rechazo': '{dale|perfecto|ok}, {sin drama|no hay problema}. {si mas adelante te sirve, escribime por aca|cualquier cosa me escribis}',
 'ya_tiene': '{buenisimo|genial}. {y que es lo que mas te molesta del que usas hoy|hay algo que no te convenza del que usas}? si es algo que resolvemos te muestro solo eso, si no no te hago perder tiempo',
 'equivocado': '{perdon|disculpa} {la molestia|por el mensaje}, que andes bien',
 'no_decisor': '{dale, gracias|ah perfecto, gracias}. {me pasas el contacto de quien decide|me pasarias el numero del dueño} asi le escribo directo?',
 'desconfianza': 'soy {vendedor} de {producto}. te escribi porque dejaste tus datos en el anuncio. {si no te interesa lo dejamos aca|si no es para vos no te escribo mas}',
 'pago': '{genial|buenisimo}. el alias es {alias}, me mandas el comprobante por aca y te la dejo activa {en el momento|al toque}',
 'precio': 'son {precio} por mes, sin limite de alumnos. {con que no pierdas una cuota por mes ya se paga solo|se paga solo con una cuota que no se te escape}. tenes 7 dias gratis, {te dejo la cuenta armada|te la armo}?',
 'de_donde': '{somos de buenos aires|somos de aca, de buenos aires}, {argentina|capital}. todo en pesos',
 'pide_llamada': '{dale|obvio}, te llamo. {te queda bien|te sirve} hoy {h1} o mañana {h2}?',
 'no_audio': '{dale|perdon}, te lo escribo {cortito|por aca}',
 'horario': '{dale|joya|perfecto}, {sin drama|tranqui}. a que hora te queda comodo que te llame?',
 'pide_material': '{aca tenes|te paso} un video corto de como funciona: {video}. {cualquier duda me decis|si te surge algo me escribis}',
 'prueba': 'tenes 7 dias gratis para probar todo. {pasame tu mail y te dejo la cuenta armada ahora|si me pasas tu mail te la armo al toque}',
 'mas_adelante': '{dale|perfecto|buenisimo}. para cuando mas o menos? te escribo unos dias antes y te lo dejo armado',
 'lo_veo': None,
 'soporte': None,
 'interes': None,
 'como_funciona': None,
 'dato_calificacion': '{ok|dale|perfecto|genial}, {anotado|ya lo tengo|listo}',
 'saludo': None,
 'ack': None,
}
# Respuesta propia por app (pisa a REPLY): una linea por app, cada una con su guion.
REPLY_BY_PRODUCT = {
 'desconfianza': {
  'gymhero': 'soy {vendedor} de gymhero, un sistema para cobrar cuotas y controlar el acceso en gimnasios. te escribi porque dejaste tus datos en el anuncio. {si no te interesa lo dejamos aca|si no es para vos no te escribo mas}',
  'turnospro': 'soy {vendedor} de turnospro, un sistema de turnos online con recordatorios y seña. te escribi porque dejaste tus datos en el anuncio. {si no te interesa lo dejamos aca|si no es para vos no te escribo mas}',
 },
}
NOTE = {
 'autorespuesta': 'no responder: es el contestador de otro negocio; esperar al humano. hoy le contestamos al 75%',
 'multimedia': 'audio: transcribir y reclasificar el texto. imagen/documento: pasar a humano',
 'email': 'crear la cuenta y avisar; seguimiento humano a las 24 h ("entraste?")',
 'anuncio': 'opener corto + primera pregunta del guion. el mensaje largo con lista de funciones retiene 4 veces menos',
 'rechazo': 'cerrar y no volver a escribir',
 'ya_tiene': 'objeción: una sola pregunta sobre qué le molesta',
 'equivocado': 'cerrar y excluir de toda cadencia',
 'desconfianza': 'presentación en una línea con el motivo real del contacto',
 'pago': 'URGENTE a humano: <5 min sigue 89%, >1 h 27%',
 'precio': 'precio leído de la API del producto, nunca escrito a mano; no pedir el mail antes',
 'de_donde': 'respuesta fija',
 'pide_llamada': 'pasar a humano / agenda con 2 horarios reales',
 'no_audio': 'marcar el lead como "sin audios" y pasar a texto',
 'horario': 'confirmar el horario y agendar la llamada',
 'pide_material': 'mandar el video del producto',
 'prueba': 'crear cuenta de prueba',
 'mas_adelante': 'preguntar fecha y programar el recontacto',
 'lo_veo': 'no responder al toque: seguimiento programado a las 24-48 h',
 'soporte': 'reenviar link de acceso si es login; si no, humano (bot de soporte)',
 'interes': 'avanzar al siguiente paso del guion (siguiente pregunta o crear cuenta)',
 'como_funciona': 'respuesta de la FAQ del producto (o IA con el conocimiento del producto si no hay FAQ que coincida)',
 'dato_calificacion': 'guardar el dato y hacer la siguiente pregunta del guion',
 'no_decisor': 'no decide (empleado, trabaja en otro gym): pedir el contacto del dueño y crear el lead',
 'email_incompleto': 'se detecta por contexto: le pedimos el mail y mando algo sin @; pedirlo completo',
 'saludo': 'saludar y seguir con el paso del guion en el mismo mensaje',
 'ack': 'si el guion tiene próximo paso, seguir; si no, no responder',
}
out=[]
for k,name,pat,action in INTENTS:
    ps=[p for p in P if p['intent']==k]; ans=[p for p in ps if p['out']]
    fast=[p for p in ans if (p['delay_min'] or 0)<5]; slow=[p for p in ans if (p['delay_min'] or 0)>=60]
    ex=[]
    for p in ps:
        t=p['in'].split(' / ')[0].strip()
        if '@' in t or len(t)>120 or t in ex: continue
        ex.append(t)
        if len(ex)>=6: break
    out.append({'key':k,'name':name,'pattern':pat,'action':action,'reply':REPLY.get(k),'reply_by_product':REPLY_BY_PRODUCT.get(k,{}),'note':NOTE.get(k),
        'max_words':25 if k=='dato_calificacion' else 35 if k=='horario' else None,
        'metrics':{'turns':len(ps),'share_pct':round(100*len(ps)/N,1),'answered_pct':round(100*len(ans)/max(1,len(ps))),
                   'continued_pct':round(100*sum(p['continued'] for p in ans)/max(1,len(ans))),
                   'continued_fast_pct':round(100*sum(p['continued'] for p in fast)/max(1,len(fast))) if fast else None,
                   'continued_slow_pct':round(100*sum(p['continued'] for p in slow)/max(1,len(slow))) if slow else None},
        'examples':ex})
# Tipo que solo se detecta por contexto (regla en el clasificador), con fila para verlo y editar la respuesta.
ps=[p for p in P if p['intent']=='email_incompleto']
out.append({'key':'email_incompleto','name':'Mail incompleto','pattern':'(?!x)x','action':'pedir_mail','reply':REPLY['email_incompleto'],
    'note':NOTE['email_incompleto'],'max_words':None,'metrics':{'turns':len(ps)},'examples':['llanos.yohana']})
other=sum(p['intent'] in ('otro','vacio') for p in P)
json.dump({'version':1,'built_at':'2026-09-30','corpus':{'sales_chats':len({p['k'] for p in P}),'lead_turns':N,
    'coverage_pct':round(100*(N-other)/N,1)},'context_rules':[
      {'if_prev_matches':r'como se llama|nombre (del|de tu|de la)|decime (solo )?el nombre','and_max_words':6,'then':'dato_calificacion'},
      {'if_prev_matches':r'\?|como (llevas|cobras|tomas|manejas)|cuantos|que (usas|sistema|rubro)','and_max_words':4,'then':'dato_calificacion'}],
    'normalization':'minúscula, sin tildes ni emojis, letras repetidas colapsadas, números=N, mail=EMAIL, link=URL, $monto=PRECIO (ver norm.py)',
    'intents':out}, open('diccionario.json','w'), ensure_ascii=False, indent=1)
# Copia para el backend (recurso embebido que siembra reply_intents): respuestas sin tildes y sin mails de ejemplo.
import paths as _p, os as _os
T = str.maketrans('áéíóúüÁÉÍÓÚÜ', 'aeiouuAEIOUU')
d = json.load(open('diccionario.json'))
for i in d['intents']:
    if i.get('reply'): i['reply'] = i['reply'].translate(T)
    i['reply_by_product'] = {p: r.translate(T) for p, r in (i.get('reply_by_product') or {}).items()}
    i['examples'] = [e for e in i['examples'] if '@' not in e]
json.dump(d, open('diccionario.json', 'w'), ensure_ascii=False, indent=1)
json.dump(d, open(_os.path.join(_p.REPO, 'backend/src/SalesHub.Infrastructure/Resources/intent-dictionary.json'), 'w'), ensure_ascii=False, indent=1)
print('ok', len(out), 'tipos; cobertura', round(100*(N-other)/N,1), '— diccionario embebido actualizado')

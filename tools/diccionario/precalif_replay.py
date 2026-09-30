"""Replay del bot de pre-calificación (2 preguntas → pase a Mateo) sobre las charlas reales que
arrancaron con un anuncio. Aproxima: usa los mensajes reales del lead en orden como respuestas a
nuestras preguntas. Salida: cómo terminaría cada charla."""
import json, re
from collections import Counter
import paths; paths.use_data()
from intents import classify
from confianza import is_confident

HOT = {'precio','pago','pide_llamada','pide_material','prueba','como_funciona','interes','desconfianza',
       'ya_tiene','no_decisor','mas_adelante','lo_veo','soporte','horario','de_donde','no_audio','rechazo'}
DOUBT = re.compile(r'(\?|no s[eé]\b|no estoy segur|no entiend|(duda|consulta|pregunta)s?\b|y si\b|se puede|puedo\b|es seguro|no me convence|desconf[ií])', re.I)
KEYW = re.compile(r'\b(precio|costo|cuesta|cu[aá]nto sale|cu[aá]nto vale|valor|tarifa|presupuesto|info|informaci[oó]n|qu[eé] es|c[oó]mo funciona|para qu[eé] sirve|qu[eé] hace|demo|prueba|gratis|trial|probar|funcion(a|es)|features|caracter[ií]sticas|qu[eé] incluye)\b', re.I)
out = Counter(); examples = {}
for l in open('chats.jsonl'):
    c = json.loads(l)
    ins = [m['t'] for m in c['msgs'] if not m['me'] and not m['t'].startswith('[')]
    if not ins: continue
    if classify(ins[0]) != 'anuncio': continue          # solo charlas que arrancan por anuncio
    answers = ins[1:]
    q = ['cómo se llama tu gimnasio o centro deportivo?', 'cómo llevás hoy los cobros y las cuotas? excel, papel, algún sistema?']
    result = None
    for i in range(2):
        if i >= len(answers): result = f'se cayó antes de responder la pregunta {i+1}'; break
        a = answers[i]
        k, conf = is_confident(a, q[i], 8)
        if DOUBT.search(a) or KEYW.search(a.lower()) or (conf and k in HOT):
            result = 'pase a Mateo por señal/pregunta' if not (conf and k == 'rechazo') else 'rechazo'
            break
    if result is None: result = 'pase a Mateo tras 2 preguntas'
    out[result] += 1
    examples.setdefault(result, []).append(' / '.join(ins[:3])[:140])
n = sum(out.values())
print(f'charlas que arrancan por anuncio: {n}')
for k, v in out.most_common(): print(f'  {k}: {v} ({round(100*v/n)}%)')
for k in out:
    print(f'\n-- {k}:'); [print('   ', e) for e in examples[k][:4]]

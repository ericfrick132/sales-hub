"""
Mide la precisión si el bot responde SOLO cuando está seguro (y el resto va a humano/IA).
"Seguro" = el mensaje coincide con un único tipo, es corto, y si trae una pregunta el tipo es una pregunta.

    python3 confianza.py                 # sobre a_etiquetar.jsonl (muestra nueva etiquetada)
    python3 confianza.py gold.jsonl      # sobre el set de oro
"""
import sys, json
from collections import defaultdict
import paths; paths.use_data()
from intents import classify, COMPILED
from norm import norm

QUESTION_TYPES = {'precio', 'como_funciona', 'prueba', 'de_donde', 'desconfianza', 'pago', 'pide_llamada', 'pide_material'}

def matches(text):
    t = norm(text); return {k for k, _, rx, _ in COMPILED if rx.search(t)}

def is_confident(text, prev, maxw=12):
    k = classify(text, prev)
    if k in ('otro', 'vacio'): return k, False
    t = norm(text)
    if len(t.split()) > maxw: return k, False
    if matches(text) - {k}: return k, False                       # coincide con más de un tipo
    if '?' in (text or '') and k not in QUESTION_TYPES: return k, False  # trae una pregunta que no es su tipo
    return k, True

if __name__ == '__main__':
    src = sys.argv[1] if len(sys.argv) > 1 else 'a_etiquetar.jsonl'
    R = [json.loads(l) for l in open(src)]
    R = [r for r in R if r.get('expected')]
    for maxw in (8, 12, 20):
        C = []
        for r in R:
            k, ok = is_confident(r['text'], r.get('prev'), maxw)
            if ok: C.append((k, r))
        good = sum(k in r['expected'] for k, r in C)
        print(f'hasta {maxw} palabras: responde solo {len(C)}/{len(R)} ({round(100*len(C)/len(R))}%) · precisión {round(100*good/max(1,len(C)),1)}% · errores {len(C)-good}')
    C = [(k, r) for r in R for k, ok in [is_confident(r['text'], r.get('prev'), 12)] if ok]
    print('\nerrores dentro de lo que respondería (12 palabras):')
    for k, r in C:
        if k not in r['expected']: print('  ', k, 'esperado', r['expected'], '|', r['text'][:100].replace('\n', ' / '))
    by = defaultdict(list)
    for k, r in C: by[k].append(k in r['expected'])
    print('\nprecisión por tipo dentro de lo que respondería:')
    for k, v in sorted(by.items(), key=lambda x: -len(x[1])): print(f'  {k:18} {sum(v)}/{len(v)}')

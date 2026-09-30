"""
Muestra y etiquetado para el set de oro (la parte humana del ciclo de backtest).

    python3 label.py sample 50     # saca 50 turnos al azar que NO están en el gold -> a_etiquetar.jsonl
                                   # (con el tipo que les pone hoy el diccionario: medir ANTES de tocar nada)
    python3 label.py score         # precisión real sobre a_etiquetar.jsonl ya etiquetado (campo "expected")
    python3 label.py merge         # suma lo etiquetado a gold.jsonl

El orden importa: muestrear, etiquetar, medir (esa es la precisión honesta) y recién ahí
sumar al gold y ajustar patrones. Si se ajusta mirando la muestra antes de medirla, la
precisión sale inflada.
"""
import paths; paths.use_data()
import json, random, sys, os
from datetime import datetime
from intents import classify
cmd = sys.argv[1] if len(sys.argv) > 1 else 'sample'

if cmd == 'sample':
    n = int(sys.argv[2]) if len(sys.argv) > 2 else 50
    gold = {json.loads(l)['text'] for l in open('gold.jsonl')} if os.path.exists('gold.jsonl') else set()
    P = [json.loads(l) for l in open('pairs.jsonl')]
    pool = [p for p in P if p['in'] not in gold and not p['in'].startswith('[')]
    random.seed(datetime.now().timestamp())
    tag = datetime.now().strftime('%Y%m%d-%H%M')
    with open('a_etiquetar.jsonl', 'w') as f:
        for i, p in enumerate(random.sample(pool, n), 1):
            f.write(json.dumps({'id': f'{tag}-{i}', 'text': p['in'], 'prev': p.get('prev'),
                                'predicted': classify(p['in'], p.get('prev')), 'expected': None,
                                'source': f'muestra {tag}'}, ensure_ascii=False) + '\n')
    print(f'{n} turnos en a_etiquetar.jsonl — completar "expected" (lista de tipos aceptables; "otro" = IA/humano)')

elif cmd == 'score':
    R = [json.loads(l) for l in open('a_etiquetar.jsonl')]
    R = [r for r in R if r['expected']]
    for r in R:  # 'vacio' y 'otro' son lo mismo: van a IA/humano
        if r['predicted'] == 'vacio': r['predicted'] = 'otro'
        if 'vacio' in r['expected'] and 'otro' not in r['expected']: r['expected'] = r['expected'] + ['otro']
    ok = sum(r['predicted'] in r['expected'] for r in R)
    err = [r for r in R if r['predicted'] not in r['expected'] and r['predicted'] != 'otro']
    esc = [r for r in R if r['predicted'] == 'otro' and 'otro' not in r['expected']]
    typed = [r for r in R if r['predicted'] != 'otro']
    print(f'etiquetados {len(R)} · ok {ok} · errores {len(err)} · escapes {len(esc)} · '
          f'precisión cuando pone tipo {round(100 * (len(typed) - len(err)) / max(1, len(typed)), 1)}%')
    for r in err: print(f"  ✗ {r['predicted']} (esperado {'/'.join(r['expected'])}): {r['text'][:100]}")
    for r in esc: print(f"  · escape (esperado {'/'.join(r['expected'])}): {r['text'][:100]}")

elif cmd == 'merge':
    R = [json.loads(l) for l in open('a_etiquetar.jsonl') if json.loads(l)['expected']]
    with open('gold.jsonl', 'a') as f:
        for r in R:
            f.write(json.dumps({k: r[k] for k in ('id', 'text', 'prev', 'expected', 'source')}, ensure_ascii=False) + '\n')
    print(f'{len(R)} sumados a gold.jsonl')

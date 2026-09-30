"""
Backtest del diccionario de tipos de mensaje contra TODOS los chats de venta.

    python3 backtest.py            # corre, compara con la corrida anterior, escribe el reporte
    python3 backtest.py --save     # además la guarda como nueva línea de base (si no hay regresión)
    python3 backtest.py --refresh  # antes vuelve a bajar los chats de prod (build.py + pairs.py)

Qué mide:
  1. Cobertura: % de turnos del lead con tipo (sin IA), total / por producto / por mes / por tipo.
  2. Precisión contra gold.jsonl (etiquetado a mano): aciertos, errores (tipo equivocado) y
     escapes (quedó "otro" y tenía tipo). Un error es peor que un escape: con respuesta
     automática, un error manda algo mal; un escape solo va a la IA o a un humano.
  3. Regresión contra la corrida anterior (runs/): si sube un error del gold o baja la
     precisión, la corrida FALLA (exit 1) y no se guarda.
  4. Retroalimentación: agrupa lo que quedó sin tipo por frases frecuentes y propone qué
     mirar (propuestas.md). Lo que se apruebe va a intents.py, se vuelve a correr y queda.

Salidas: reporte.md, propuestas.md, runs/<fecha>.json
"""
import paths; paths.use_data()
import json, os, re, subprocess, sys, statistics as st
from collections import Counter, defaultdict
from datetime import datetime, timezone
from intents import classify, INTENTS
from norm import norm

RUNS = 'runs'
os.makedirs(RUNS, exist_ok=True)
NAMES = {k: n for k, n, _, _ in INTENTS}
NAMES.update({'otro': 'Sin tipo (IA/humano)', 'vacio': 'Vacío', 'email_incompleto': 'Mail incompleto'})

if '--refresh' in sys.argv:
    subprocess.run(['bash', os.path.join(paths.TOOLS, 'export.sh')], check=True)
    for script in ('build.py', 'pairs.py'):
        subprocess.run([sys.executable, os.path.join(paths.TOOLS, script)], check=True)

# ── 1. Corpus completo ────────────────────────────────────────────────────
P = [json.loads(l) for l in open('pairs.jsonl')]
for p in P:
    p['intent'] = classify(p['in'], p.get('prev'))
N = len(P)
cnt = Counter(p['intent'] for p in P)
covered = N - cnt['otro'] - cnt['vacio']

def cov(ps):
    n = len(ps)
    return round(100 * sum(p['intent'] not in ('otro', 'vacio') for p in ps) / n, 1) if n else None

chat_month = {}
for l in open('chats.jsonl'):
    c = json.loads(l)
    chat_month[c['k']] = datetime.fromtimestamp(c['msgs'][-1]['ts'], timezone.utc).strftime('%Y-%m')
by_product = defaultdict(list); by_month = defaultdict(list)
for p in P:
    by_product[p.get('product') or 'sin producto'].append(p)
    by_month[chat_month.get(p['k'], '?')].append(p)

per_type = {}
for k, ps in defaultdict(list, {k: [p for p in P if p['intent'] == k] for k in cnt}).items():
    ans = [p for p in ps if p['out']]
    per_type[k] = {
        'n': len(ps), 'pct': round(100 * len(ps) / N, 1),
        'answered_pct': round(100 * len(ans) / len(ps)) if ps else None,
        'continued_pct': round(100 * sum(p['continued'] for p in ans) / len(ans)) if ans else None,
    }

# ── 2. Gold ───────────────────────────────────────────────────────────────
G = [json.loads(l) for l in open('gold.jsonl')] if os.path.exists('gold.jsonl') else []
gold = []
for g in G:
    got = classify(g['text'], g.get('prev'))
    exp = g['expected']
    if got in exp: verdict = 'ok'
    elif got == 'otro': verdict = 'escape'
    else: verdict = 'error'
    gold.append({'id': g['id'], 'got': got, 'expected': exp, 'verdict': verdict, 'text': g['text'][:140]})
gc = Counter(x['verdict'] for x in gold)
classified = sum(1 for x in gold if x['got'] != 'otro')
precision = round(100 * sum(1 for x in gold if x['verdict'] == 'ok' and x['got'] != 'otro') / classified, 1) if classified else None

# ── 3. Regresión ──────────────────────────────────────────────────────────
prev_runs = sorted(f for f in os.listdir(RUNS) if f.endswith('.json') and not f.startswith('labels_'))
base = json.load(open(os.path.join(RUNS, prev_runs[-1]))) if prev_runs else None
regressions = []
if base:
    base_gold = {x['id']: x for x in base.get('gold', [])}
    for x in gold:
        b = base_gold.get(x['id'])
        if b and b['verdict'] == 'ok' and x['verdict'] != 'ok':
            regressions.append(f"{x['id']}: antes {b['got']}, ahora {x['got']} (esperado {'/'.join(x['expected'])}) — {x['text'][:80]}")
    # Precisión comparada sobre los MISMOS casos de la línea de base (el gold crece entre corridas).
    same = [x for x in gold if x['id'] in base_gold]
    def prec(xs, key):
        typed = [x for x in xs if x[key] != 'otro']
        return round(100 * sum(1 for x in typed if x[key] in x['expected']) / len(typed), 1) if typed else None
    now_same = prec(same, 'got')
    before_same = prec([dict(base_gold[x['id']], expected=x['expected']) for x in same], 'got')
    if now_same is not None and before_same is not None and now_same < before_same:
        regressions.append(f"precisión sobre los mismos {len(same)} casos bajó: {before_same}% → {now_same}%")
flips = []
if base and os.path.exists(os.path.join(RUNS, 'labels_' + prev_runs[-1])):
    old = json.load(open(os.path.join(RUNS, 'labels_' + prev_runs[-1])))
    flips = Counter(f'{a} → {b}' for a, b in zip(old, [p['intent'] for p in P]) if a != b) if len(old) == N else Counter()

# ── 4. Retroalimentación: frases frecuentes entre lo que quedó sin tipo ───
other = [p for p in P if p['intent'] == 'otro']
def grams(t, k):
    w = t.split(); return {' '.join(w[i:i + k]) for i in range(len(w) - k + 1)}
STOP = {'que', 'de', 'la', 'el', 'en', 'y', 'a', 'los', 'las', 'me', 'te', 'se', 'lo', 'no', 'si', 'es', 'un', 'una', 'por', 'con', 'para', 'mi', 'tu', 'N'}
cand = Counter()
for p in other:
    for k in (2, 3):
        for g in grams(p['in_n'], k):
            if not all(w in STOP for w in g.split()):
                cand[g] += 1
# Solo frases que casi no aparecen en lo ya clasificado (si no, no distinguen nada).
in_covered = Counter()
for p in P:
    if p['intent'] not in ('otro', 'vacio'):
        for k in (2, 3):
            for g in grams(p['in_n'], k): in_covered[g] += 1
proposals = []
seen = set()
for g, n in cand.most_common(400):
    if n < 6 or in_covered[g] > n: continue
    if any(g in s or s in g for s in seen): continue
    seen.add(g)
    ex = [p for p in other if g in p['in_n']]
    replies = Counter(p['out'].split(' / ')[0][:70] for p in ex if p['out'])
    proposals.append({'phrase': g, 'n': n, 'examples': [p['in'][:130].replace('\n', ' ') for p in ex[:5]],
                      'prev': Counter((p.get('prev') or '')[-60:] for p in ex).most_common(2),
                      'top_reply': replies.most_common(1)})
    if len(proposals) >= 30: break

# ── Salidas ───────────────────────────────────────────────────────────────
stamp = datetime.now().strftime('%Y%m%d-%H%M%S')
run = {'at': stamp, 'turns': N, 'coverage': round(100 * covered / N, 1), 'precision': precision,
       'gold': gold, 'gold_counts': dict(gc), 'per_type': per_type,
       'by_product': {k: cov(v) for k, v in by_product.items()}}

R = [f'# Backtest del diccionario — {stamp}\n',
     f'- Turnos del lead: **{N}** · cobertura sin IA: **{run["coverage"]}%**' + (f' (antes {base["coverage"]}%)' if base else ''),
     f'- Gold ({len(gold)} etiquetados a mano): ok {gc["ok"]} · **errores {gc["error"]}** · escapes {gc["escape"]} · precisión cuando pone tipo: **{precision}%**' + (f' (antes {base["precision"]}%)' if base else ''),
     '', '## Regresiones', *(['- ' + r for r in regressions] or ['- ninguna']),
     '', '## Errores del gold (tipo equivocado)', *(['- `' + x['id'] + f"` → {x['got']} (esperado {'/'.join(x['expected'])}): {x['text']}" for x in gold if x['verdict'] == 'error'] or ['- ninguno']),
     '', '## Escapes del gold (quedó sin tipo)', *(['- `' + x['id'] + f"` (esperado {'/'.join(x['expected'])}): {x['text']}" for x in gold if x['verdict'] == 'escape'] or ['- ninguno']),
     '', '## Cobertura por producto', *[f'- {k}: {v}% ({len(by_product[k])} turnos)' for k, v in sorted(run['by_product'].items(), key=lambda x: -len(by_product[x[0]]))],
     '', '## Cobertura por mes (última actividad del chat)', *[f'- {k}: {cov(v)}% ({len(v)})' for k, v in sorted(by_month.items())],
     '', '## Por tipo', '| tipo | turnos | % | contestamos | siguió |', '|---|---|---|---|---|',
     *[f"| {NAMES.get(k, k)} | {v['n']} | {v['pct']}% | {v['answered_pct']}% | {v['continued_pct']}% |" for k, v in sorted(per_type.items(), key=lambda x: -x[1]['n'])]]
if flips:
    R += ['', '## Cambios de tipo vs la corrida anterior', *[f'- {v} {k}' for k, v in flips.most_common(20)]]
open('reporte.md', 'w').write('\n'.join(R) + '\n')

PR = ['# Propuestas: frases frecuentes entre lo que quedó sin tipo\n',
      'Para cada frase: cuántas veces aparece, ejemplos, nuestro mensaje anterior más común y qué le contestamos.',
      'Si una frase define un tipo nuevo o le falta a uno existente, se agrega en intents.py y se vuelve a correr.\n']
for pr in proposals:
    PR += [f"## «{pr['phrase']}» — {pr['n']} veces",
           *[f'- {e}' for e in pr['examples']],
           f"- antes le habíamos dicho: {pr['prev']}",
           f"- le contestamos: {pr['top_reply']}", '']
open('propuestas.md', 'w').write('\n'.join(PR))

print('\n'.join(R[:3]))
print(f'regresiones: {len(regressions)}')
for r in regressions: print('  ✗', r)
if regressions:
    print('FALLA: no se guarda como línea de base.'); sys.exit(1)
# Paridad: el clasificador de C# (el de producción) tiene que dar lo mismo que intents.py.
if '--parity' in sys.argv or '--save' in sys.argv:
    subprocess.run([sys.executable, os.path.join(paths.TOOLS, 'build_dict.py')], check=True)
    items = [{'in': p['in'], 'prev': p.get('prev'), 'py': p['intent']} for p in P] + \
            [{'in': g['text'], 'prev': g.get('prev'), 'py': classify(g['text'], g.get('prev'))} for g in G]
    json.dump(items, open('parity_input.json', 'w'), ensure_ascii=False)
    out = subprocess.run(['dotnet', 'run', '-c', 'Release', '--project', os.path.join(paths.TOOLS, 'parity'), '--',
                          os.path.abspath('parity_input.json')], capture_output=True, text=True)
    last = [l for l in out.stdout.splitlines() if l.startswith('iguales') or l.startswith('  ')]
    print('paridad C#:', *(last or [out.stderr[-500:]]), sep='\n  ')
    if not last or '(100.00%)' not in last[0]:
        print('FALLA: C# y el análisis no coinciden (actualizar IntentClassifier.cs o el diccionario embebido).'); sys.exit(1)

if '--save' in sys.argv:
    json.dump(run, open(os.path.join(RUNS, stamp + '.json'), 'w'), ensure_ascii=False, indent=1)
    json.dump([p['intent'] for p in P], open(os.path.join(RUNS, 'labels_' + stamp + '.json'), 'w'))
    print(f'guardada: runs/{stamp}.json')

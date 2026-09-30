"""
¿Cuándo escribir? Tasa de respuesta según hora (Argentina), día de la semana y día del mes en que
le escribimos al lead, sobre todas las charlas de venta. Un "intento" = mensaje nuestro después de
12+ h de silencio en la charla (primer contacto o seguimiento), contando solo el primero de la ráfaga.
Respuesta = el lead escribe dentro de las 24 h (y dentro de 1 h, para ver la respuesta en caliente).
"""
import json
from collections import defaultdict
from datetime import datetime, timezone, timedelta
import paths; paths.use_data()
AR = timezone(timedelta(hours=-3))
DIAS = ['lun', 'mar', 'mie', 'jue', 'vie', 'sab', 'dom']

tries = []
inbound_hours = defaultdict(int); inbound_days = defaultdict(int)
for l in open('chats.jsonl'):
    c = json.loads(l); ms = c['msgs']
    for i, m in enumerate(ms):
        t = datetime.fromtimestamp(m['ts'], AR)
        if not m['me']:
            inbound_hours[t.hour] += 1; inbound_days[t.weekday()] += 1
            continue
        prev_ts = ms[i - 1]['ts'] if i > 0 else None
        if prev_ts is not None and m['ts'] - prev_ts < 12 * 3600: continue
        first = not any(not x['me'] for x in ms[:i])          # nunca nos había escrito
        rep = next((x['ts'] for x in ms[i + 1:] if not x['me']), None)
        tries.append({'t': t, 'first': first,
                      'r24': rep is not None and rep - m['ts'] <= 86400,
                      'r1': rep is not None and rep - m['ts'] <= 3600})

def table(title, keyf, order, label=str):
    g = defaultdict(list)
    for x in tries: g[keyf(x)].append(x)
    print(f'\n## {title}  (n = intentos · contestó en 24 h · en 1 h)')
    for k in order:
        v = g.get(k, [])
        if len(v) < 30: continue
        print(f'  {label(k):>10}  n={len(v):5}  24h {100*sum(x["r24"] for x in v)/len(v):5.1f}%  1h {100*sum(x["r1"] for x in v)/len(v):5.1f}%')

print(f'intentos: {len(tries)} (primer contacto {sum(x["first"] for x in tries)}, seguimientos {sum(not x["first"] for x in tries)})')
tot = len(tries); print(f'promedio: 24h {100*sum(x["r24"] for x in tries)/tot:.1f}% · 1h {100*sum(x["r1"] for x in tries)/tot:.1f}%')
table('Hora (Argentina)', lambda x: x['t'].hour, range(24), lambda h: f'{h:02d} h')
table('Franja', lambda x: '06-09' if 6 <= x['t'].hour < 9 else '09-12' if x['t'].hour < 12 else '12-15' if x['t'].hour < 15 else '15-18' if x['t'].hour < 18 else '18-21' if x['t'].hour < 21 else '21-24' if x['t'].hour >= 21 else '00-06',
      ['06-09', '09-12', '12-15', '15-18', '18-21', '21-24', '00-06'])
table('Día de la semana', lambda x: x['t'].weekday(), range(7), lambda d: DIAS[d])
table('Día del mes', lambda x: '01-05' if x['t'].day <= 5 else '06-10' if x['t'].day <= 10 else '11-15' if x['t'].day <= 15 else '16-20' if x['t'].day <= 20 else '21-25' if x['t'].day <= 25 else '26-31',
      ['01-05', '06-10', '11-15', '16-20', '21-25', '26-31'])
table('Mes', lambda x: x['t'].strftime('%Y-%m'), sorted({x['t'].strftime('%Y-%m') for x in tries}))
# Primer contacto vs seguimiento por franja
for first in (True, False):
    sub = [x for x in tries if x['first'] == first]
    g = defaultdict(list)
    for x in sub: g['mañana 9-12' if 9 <= x['t'].hour < 12 else 'mediodía 12-15' if 12 <= x['t'].hour < 15 else 'tarde 15-19' if 15 <= x['t'].hour < 19 else 'noche 19-23' if 19 <= x['t'].hour < 23 else 'otro'].append(x)
    print(f'\n## {"Primer contacto" if first else "Seguimiento"} por franja')
    for k, v in sorted(g.items(), key=lambda kv: -len(kv[1])):
        if len(v) >= 30: print(f'  {k:>15}  n={len(v):5}  24h {100*sum(x["r24"] for x in v)/len(v):5.1f}%')
print('\n## Cuándo escriben los leads por su cuenta (mensajes entrantes)')
tot_in = sum(inbound_hours.values())
print('  hora: ' + ' '.join(f'{h:02d}:{100*inbound_hours[h]/tot_in:.0f}%' for h in range(24)))
tot_d = sum(inbound_days.values())
print('  día:  ' + ' '.join(f'{DIAS[d]} {100*inbound_days[d]/tot_d:.0f}%' for d in range(7)))

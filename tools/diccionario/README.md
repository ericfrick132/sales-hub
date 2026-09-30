# Diccionario de respuestas sin IA: backtest y retroalimentación

Clasifica cada mensaje del lead por palabras (sin IA) y se mide contra **todos** los chats de venta
reales. Los patrones viven en `intents.py`; producción usa la misma lógica en C#
(`IntentClassifier.cs` + `Resources/intent-dictionary.json`) y el backtest verifica que den igual.

Los datos (chats reales de leads) quedan en `docs/conversaciones/v2/`, **fuera de git**.

## Opiniones desde la app

En **/simulacion** se ven las conversaciones reales con lo que habría hecho el bot en cada mensaje
del lead. Cada decisión se marca **bien / mal** (con el tipo y la acción correctos y lo que habría
respondido la persona). `export.sh` baja esas opiniones (`feedback.csv.gz`) y `backtest.py` las usa
como casos etiquetados del set de oro; las correcciones de acción y las respuestas sugeridas salen en
`reporte.md`.

## El ciclo

```bash
cd tools/diccionario

# 1. Muestra nueva que el diccionario NO vio, con el tipo que le pone HOY
python3 label.py sample 50          # -> a_etiquetar.jsonl
# 2. Etiquetar a mano: en cada línea, "expected" = lista de tipos aceptables ("otro" = IA/humano)
# 3. Medir ANTES de tocar nada: esta es la precisión real
python3 label.py score
# 4. Sumarla al set de oro
python3 label.py merge
# 5. Ajustar patrones en intents.py (mirar propuestas.md y los errores del reporte)
# 6. Backtest: datos frescos de prod, freno de regresiones, paridad con C#; guarda la línea de base
python3 backtest.py --refresh --save
# 7. Commit + deploy; en /diccionario, "Restaurar diccionario del análisis" y listo
```

`backtest.py` **falla** (exit 1) y no guarda si:
- un caso del set de oro que estaba bien pasa a estar mal, o
- baja la precisión sobre los mismos casos de la corrida anterior, o
- C# y `intents.py` no clasifican igual.

## Qué escribe (en la carpeta de datos)

| Archivo | Qué es |
|---|---|
| `reporte.md` | cobertura (total, producto, mes, tipo), errores y escapes del set de oro, regresiones, cambios de tipo |
| `propuestas.md` | frases frecuentes entre lo que quedó sin tipo, con ejemplos y qué le contestamos: de acá salen los patrones nuevos |
| `gold.jsonl` | set de oro etiquetado a mano (crece en cada vuelta) |
| `runs/` | historial de corridas (línea de base para comparar) |

## Métricas

- **Cobertura**: % de turnos del lead con tipo (lo demás va a la IA o a un humano).
- **Error**: tipo equivocado. Es lo grave: con respuesta automática, manda algo mal.
- **Escape**: quedó sin tipo teniendo uno. Es leve: va a la IA o a un humano.
- **Precisión**: cuando pone tipo, cuántas veces acierta. La honesta es la de `label.py score`
  sobre la muestra nueva (antes de ajustar); la del set de oro sale inflada porque con esos
  casos se ajustó.

## Archivos

- `norm.py`: normalización del texto (igual a `IntentText.cs`).
- `intents.py`: patrones y reglas de contexto (igual a `IntentClassifier.cs`).
- `export.sh` → `build.py` → `pairs.py`: baja los chats de prod, arma el corpus y los pares entrada → respuesta → si siguió.
- `build_dict.py`: genera el diccionario (patrones + respuesta sugerida + métricas) y el recurso embebido del backend.
- `parity/`: consola .NET que corre el clasificador de producción sobre el corpus.

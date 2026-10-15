# Presupuesto de obra en Excel: plantilla gratis y cómo armarlo paso a paso

Un presupuesto de obra bien armado es lo que separa una obra que cierra números de una que se come el margen sin que nadie se dé cuenta. Si todavía lo hacés en una planilla suelta, con rubros que se repiten y totales que no cuadran, esta guía te da dos cosas: una **plantilla de presupuesto de obra en Excel** lista para usar, con fórmulas, resumen por rubro y certificación de avance, y el método para completarla sin errores.

**[Descargar la plantilla de presupuesto de obra (Excel)](https://archicloud.tech/plantillas/presupuesto-de-obra.xlsx)** — gratis, sin registro. Viene con un ejemplo completo de una casa de 120 m² para que veas cómo se carga.

## Qué es un presupuesto de obra y qué tiene que tener

El presupuesto de obra es el documento que traduce un proyecto en plata: cuánto cuesta cada tarea, cuánto suma todo y qué precio final se le presenta al cliente. En Argentina, y en general en toda Latinoamérica, se arma con esta estructura:

1. **Rubros.** Los grandes capítulos de la obra: trabajos preliminares, movimiento de suelos, estructura de hormigón, mampostería, cubierta, instalaciones, revoques, pisos, carpinterías, pintura, limpieza final.
2. **Ítems.** Cada tarea concreta dentro de un rubro, con su unidad de medida: m², m³, metro lineal, unidad, boca o global.
3. **Cómputo métrico.** La cantidad de cada ítem, medida sobre el plano. Es la parte que más tiempo lleva y la que más errores esconde.
4. **Precios unitarios.** Cuánto cuesta una unidad de cada ítem, separando **materiales** y **mano de obra**. Separarlos no es un detalle: te permite ver de dónde viene el costo y renegociar lo que corresponde.
5. **Costo directo.** La suma de todos los ítems: cantidad por precio unitario.
6. **Gastos generales, beneficio e IVA.** Sobre el costo directo se aplican los gastos generales de la empresa (dirección, seguros, administración), el beneficio esperado y, al final, el IVA. Ese es el **precio final** que ve el cliente.
7. **Incidencia.** Qué porcentaje del total representa cada rubro. Sirve para controlar: si la estructura suele pesar entre 20 y 30 por ciento y en tu presupuesto pesa 8, algo está mal computado.

Un presupuesto sin estas siete partes es una lista de precios, no un presupuesto.

## Cómo está armada la plantilla

La plantilla tiene cuatro hojas conectadas entre sí:

- **Datos.** Nombre de la obra, cliente, fecha, moneda, superficie cubierta y los tres porcentajes que después se aplican solos: gastos generales, beneficio e IVA. Cambiás un porcentaje acá y se recalcula todo.
- **Presupuesto.** Catorce rubros con 36 ítems de ejemplo para una casa de 120 m², cada uno con unidad, cantidad, precio unitario de materiales y de mano de obra. Las columnas de subtotal e incidencia se calculan solas. Los rubros muestran su subtotal en la fila de título.
- **Resumen.** El costo directo por rubro con su incidencia, y abajo el cálculo del precio final: gastos generales, beneficio, subtotal sin IVA, IVA y precio con IVA, más el precio por m² cubierto, que es el número que todos te van a preguntar.
- **Certificación.** Cada ítem con su monto presupuestado y seis columnas de porcentaje de avance acumulado, una por mes. La planilla calcula el certificado de cada mes (la diferencia con el mes anterior), el certificado a origen y el saldo que falta certificar. Es la base para facturar por avance sin discusiones.

Todos los precios del ejemplo son ilustrativos: sirven para entender la mecánica, no como referencia de mercado. Reemplazalos por tus valores.

## Paso a paso para armar tu presupuesto

### 1. Cargá los datos generales

En la hoja Datos completá la obra, el cliente, la superficie y los porcentajes. Si no tenés un criterio propio, un punto de partida habitual es gastos generales entre 10 y 15 por ciento y beneficio entre 10 y 20 por ciento, según el tamaño de la empresa y el riesgo de la obra. El IVA depende de cómo facturás.

### 2. Hacé el cómputo métrico sobre el plano

Medí cada ítem en su unidad: metros cuadrados de muro, metros cúbicos de hormigón, bocas eléctricas, metros lineales de zócalo. Anotá de dónde sale cada cantidad, aunque sea en una columna auxiliar, porque cuando el cliente pregunte por qué hay 210 m² de mampostería vas a querer poder mostrarlo.

### 3. Pedí precios y separá materiales de mano de obra

Cargá el precio unitario de materiales y el de mano de obra en columnas distintas. Si un contratista te pasa un precio "todo incluido", pedile que lo abra: no podés controlar lo que no ves. La columna de precio unitario total se arma sola.

### 4. Revisá las incidencias

Antes de mirar el total, mirá la columna de incidencia por rubro en la hoja Resumen. Si un rubro pesa mucho más o mucho menos de lo esperable para ese tipo de obra, volvé al cómputo. Es el control más rápido y el que más errores atrapa.

### 5. Definí el precio final y su validez

La hoja Resumen te da el precio con IVA y el precio por m². Ponele fecha y validez (30 días es lo habitual con inflación alta) y aclarás qué no incluye: proyecto, dirección, honorarios, tasas municipales, conexiones de servicios. Lo que no está escrito, el cliente lo va a dar por incluido.

### 6. Usá el presupuesto para certificar

Cuando la obra arranca, el presupuesto deja de ser un papel y pasa a ser la base de control. En la hoja Certificación cargás el porcentaje de avance de cada ítem al cierre de cada mes, y la planilla te dice cuánto certificar ese mes y cuánto llevás a origen. Con eso facturás por avance real, no por lo que parece.

## Errores comunes en un presupuesto de obra

- **Mezclar unidades.** Un ítem en m² y el siguiente del mismo rubro en global, sin aclarar qué incluye el global. Después nadie sabe qué se presupuestó.
- **No separar materiales y mano de obra.** Cuando sube el precio de un material, no sabés cuánto te afecta.
- **Olvidar los rubros chicos.** Zinguería, limpieza final, imprevistos. Individualmente no pesan; juntos son un 5 por ciento que sale de tu margen.
- **Aplicar el beneficio antes de los gastos generales.** El orden importa: primero gastos generales sobre el costo directo, después beneficio sobre el subtotal, al final el IVA.
- **No poner validez.** Un presupuesto de hace tres meses en Argentina es otro presupuesto.
- **Presupuestar y no controlar.** El error más caro: armar el presupuesto para ganar la obra y nunca compararlo con lo que se gasta. Ahí es donde se pierde el margen.

## Cuándo el Excel te queda chico

La plantilla resuelve una obra. El problema aparece cuando tenés dos o tres a la vez, con contratistas distintos, gastos que entran todos los días y un cliente que pregunta cómo va la plata. Ahí el Excel exige que alguien lo mantenga a mano, y lo que no se carga no se controla.

[ArchiCloud](/) está armado alrededor de este mismo método: el presupuesto se carga por rubro con categorías editables, cada gasto entra por foto desde el celular, en pesos o en dólares con la cotización del día guardada, y se compara en vivo contra el presupuesto de su rubro. Los avances por etapa alimentan la certificación, los contratistas tienen sus seguros y ART al día en el mismo lugar, y el cliente ve el estado de su obra en un portal propio, sin llamarte. Podés leer más en [cómo controlar los gastos de una obra](/blog/como-controlar-los-gastos-de-una-obra/), en [software de gestión de obras para arquitectos](/software-de-gestion-de-obras-para-arquitectos/) y en [software para constructoras](/software-para-constructoras/).

**[Descargar la plantilla de presupuesto de obra (Excel)](https://archicloud.tech/plantillas/presupuesto-de-obra.xlsx)** y, cuando te quede chica, **[probá ArchiCloud 14 días gratis](/register)**, sin tarjeta.

"""Diccionario de tipos de mensaje del lead. Patrones sobre texto normalizado (norm.py: minúscula,
sin tildes ni emojis, números = N, mail = EMAIL, link = URL, precio = PRECIO). El orden importa:
gana el primero que coincide."""
import re
from norm import norm

ACK = r'(grs|grax|graciass|ok|oka|okk|oki|okey|okay|oks|okok|dale|si|sisi|no|nono|tranqui|asi es|puede ser|por favor|si por favor|vamos|yendo|joya|genial|perfecto|listo|bueno|barbaro|joya|gracias|muchas gracias|mil gracias|buenisimo|excelente|voy|de nada|ja|jaja|jajaja|jajajaja|jeje|buen|bien|claro|obvio|sip|ah|aa|ahh|mm|mmm|okis|vale|super|re bien|todo bien|igualmente|a vos|abrazo|saludos|besos|dalee|ook)'

# (clave, nombre, patrón, acción por defecto)
INTENTS = [
 ('autorespuesta', 'Contestador automático de otro negocio',
  r'soy (el|la) asistente (virtual )?de|^(?!.*(pasanos|mandanos|pasame|mandame|enviame|te habla|habla \w+ del|no (estaria|estamos|estoy) necesitando|por el momento no|lo vamos charlando))(?:.*)(gracias por (comunicarte|tu mensaje|ponerte en contacto|escribirnos|contactarnos|escribir)|asistente virtual|a la brevedad|lo antes posible|horarios? de atencion|lunes a viernes|(en que|como) (te |le )?podemos ayudar|haznos saber|dejanos tu consulta|te he conectado|no podemos responder|hemos pasado la conversacion|responderemos|revisara tu mensaje|mensaje automatico)',
  'no_responder'),
 ('multimedia', 'Solo audio / imagen / sticker', r'^((audio|image|sticker|video|document|ptv)message\s*)+$', 'transcribir_o_humano'),
 ('email', 'Pasó su mail', r'\bEMAIL\b', 'avanzar_guion'),
 ('anuncio', 'Texto precargado del anuncio',
  r'gustaria activar|quiero (info|informacion|mas informacion|mas info)|quiero contratar|quiero bajar la morosidad|quiero cobrar las cuotas|quiero el seguimiento de obra|dejar el excel|quiero ordenar|quiero organizar',
  'arrancar_guion'),
 ('rechazo', 'No le interesa / que no le escriban',
  r'no (me )?interesa|^no,? gracias|lo dejamos para (otro momento|mas adelante)|estamos cubiertos|mil gracias igual|por el culo|a la mierda|pelotud|\bforro\b|la concha|no ?me mand(es|en)|no ?me escrib|denunci|^no te agradezco|^chau$|no chau|no voy a (querer|contratar|seguir)|imposible pagar|no (puedo|podemos) (ocuparme|pagar)|me estoy enfocando en otro|no por ahora|por ahora no|no necesito|no estoy interesad|no quiero|dejame de|deja de escribir|no me escribas|borrame|dame de baja|no molest',
  'cerrar'),
 ('ya_tiene', 'Ya tiene otro sistema',
  r'ya (tengo|tenemos|uso|usamos|contamos con|trabajo con|trabajamos con|contrate|contratamos) (un |una |el |la |otro |otra )?(sistema|app|aplicacion|programa|software|plataforma|servicio|con)|ya (contrate|contratamos|consegui)|ya cuento con|contamos con (un|una) (sistema|app|programa|software)|estamos sistematizados|sistema q(ue)? ya tengo|(?<!no )(estoy|estamos) usando(?! (ningun|nada|nigun))|actualmente (uso|usamos|estoy con|trabajo con)|(trabajamos|trabajo) con una app|usamos una app|estoy con otra app|tengo otra app|tengo un (sistema|programa|software)|tenemos un (sistema|programa|software)|estamos (usando|con un)|consegui otro|uso otro',
  'objecion'),
 ('equivocado', 'Número equivocado / no es del rubro / busca trabajo',
  r'numero equivocado|te equivocaste|te confundiste|equivocado de|no tengo (mas )?(el )?(gym|gimnasio|negocio)|cerre el|lo vendi|no es un (gym|gimnasio)|no soy (el|la) (duen|encargad)|busco trabajo|buscando trabajo|soy (pintor|yesero|albanil|plomero|electricista|herrero)|ofrezco mis (trabajos|servicios)|estoy disponible para',
  'cerrar'),
 ('desconfianza', '¿Quién sos? / sos un bot',
  r'quien (sos|habla|te (dio|paso)|me escribe)|por ?que (tema )?me (escrib|habl)|que es esto|no se quien|quien es$|de donde (me|sacaste|tenes|sos)|como (conseguiste|tenes) mi|no te (tengo|conozco)|te conozco|sos (un )?bot|es un bot|sos real|sos una persona|\bspam\b',
  'presentarse'),
 ('precio', 'Pregunta el precio',
  r'precio|presupuesto|despues de (los|esos|la) (N dias|prueba)|\babonas\b|se abona|hay que pagar|que vale|cuanto (sale|cuesta|es|seria|cobran|vale|queda|me sale|saldria|costaria)|tengo que pagar|que valor|el valor|valores|costo|tarifa|\bPRECIO\b|cuanto por mes|tiene (algun )?costo|se paga',
  'responder_precio'),
 ('pago', 'Quiere pagar / cómo pago',
  r'como (pago|abono|hago el pago|te pago|contrato)|quiero el (de|plan de) N (meses|mes)|renovar|voy a (pagar|abonar|transferir)|para (la )?transferencia|quiero abonar|hacer el pago|necesito pagar|puedo pagar|\btarjeta\b|pasame (el |tu )?(alias|cbu|link de pago)|\balias\b|\bcbu\b|transferi|comprobante|quiero (pagar|abonar)|donde (pago|abono)|con tarjeta|mercado ?pago',
  'cerrar_venta'),
 ('de_donde', 'De dónde son / qué país',
  r'de (que|donde) (pais|son|sos|provincia|ciudad)|son de (argentina|cordoba|buenos aires|capital|aca)|de que pais|donde estan ubicados|son argentinos',
  'respuesta_fija'),
 ('pide_llamada', 'Pide llamada / demo / hablar con alguien',
  r'(?<!se )llamame|me (llamas|podes llamar|podrias llamar)|llamada|llamar(me|te)?\b|llama (ya|ahora)|^llamame|videollamada|video llamada|(hacer|tener|coordinar|armar|agendar|hagamos|tengamos) (una )?reunion|\bdemo\b|\bmeet\b|\bzoom\b|hablar con (alguien|una persona|un asesor|vos)|me podes llamar|te llamo',
  'agendar_llamada'),
 ('no_audio', 'No puede escuchar audios',
  r'no (puedo |podemos |logro )?(escuchar|reproducir|abrir)(los)? ?(audios?|el audio)?$|no logro escuchar|no escucho audios|audios no|escribime|por escrito|no se escucha',
  'pasar_a_texto'),
 ('no_decisor', 'No es el dueño / no decide',
  r'(trabajo|estoy trabajando) (en|para) (otro|un|una|el|la)|soy (empleado|empleada|recepcionista|la secretaria|el secretario)|no soy (el|la) (duen|encargad|que decide)|(el|la) (duen|duena|dueno) es|le paso (tu|el) contacto|tendrias que hablar con',
  'pedir_contacto_decisor'),
 ('horario', 'Horario / disponibilidad',
  r'\b(manana|hoy|pasado|lunes|martes|miercoles|jueves|viernes|sabado|domingo)\b.*\b(puedo|podria|puede|te va|me va|a la (manana|tarde|noche)|a las|N ?hs|temprano|tarde)\b|a la (manana|tarde|noche) (puedo|podria|te)|\bN ?hs\b|\bN N ?hs\b|a las N|despues de las N|en un rato|dame un rato|estoy (justo )?con (turnos|clientes|clase|alumnos)|estoy (en el banco|trabajando(?! (en|para|de|con))|manejando|ocupad|con un cliente|con (una )?clase|dando clase)|ahora no puedo|me desocupo|mas tarde (te|hablamos)|a que hora',
  'agendar_llamada'),
 ('pide_material', 'Pide video / tutorial / material',
  r'(pasa|pasame|pasas|manda|mandame|mandas|tenes|tendrias|tienen|hay|envia|enviame|mande|ver el).{0,30}(video|tutorial|manual|instructivo|folleto|presentacion|catalogo)|^(video|el video|mandame el video)|^(dale|si|bueno|ok) ?(pasame|mandame|pasa|manda)( (la )?(info|todo))?$',
  'enviar_material'),
 ('prueba', 'Cómo lo pruebo',
  r'como (hago|haria|hacer|pra|para) (para )?prob|la puedo probar|lo puedo probar|quiero probar|como pruebo|probarlo|probar(lo)? (desde|en)|prueba gratis|periodo de prueba|dias (de prueba|gratis)',
  'crear_cuenta'),
 ('mas_adelante', 'Más adelante / todavía no abrió',
  r'mas adelante|todavia no (abri|arranque|empece|tengo|estoy)|en N (meses|semanas)|estoy por abrir|voy a abrir|abro en|abrimos en|inaugur|el mes que viene|la semana que viene|a fin de mes|ahora no|en otro momento|por el momento no|cuando (abra|tenga|arranque)|el ano que viene|despues de las fiestas',
  'agendar_recontacto'),
 ('lo_veo', 'Lo veo / lo pienso / lo consulto',
  r'lo (veo|miro|reviso|analizo|pienso|charlo|consulto|evaluo|chusmeo)|lo voy a (ver|mirar|pensar|revisar|analizar|probar)|te aviso|voy a (ver|mirar|chusmear|probar|revisar)|lo vemos|dejame (ver|pensar|chequear)|con mi (socio|socia|marido|mujer|esposa|esposo|pareja|jefe)|lo hablo',
  'seguimiento_programado'),
 ('soporte', 'Problema técnico / no puede entrar',
  r'no me (deja|anda|funciona|carga|llega|llego|abre|aparece|toma|reconoce)|no (pude|puedo|pudo|podemos) (entrar|ingresar|acceder)|me sale (esto|con deuda|que|otra vez|abonar)|no hay caso|no me permite|no estan cargad|aparece que|me dice que|le dice que|(pasame|mandame|reenviame) el (link|enlace|acceso)|no (m )?(deja|carga|llega)|se queda (ahi|cargando)|no carga|problemas? con la (app|pagina|cuenta)|me solucionen|no figura|no aparece|no puedo (entrar|ingresar|acceder|registrar|cargar)|\berror\b|contrasena|\bclave\b|\bcodigo\b|no (me )?llego|se trabo|no anda|como (hago|se hace) para|como (cargo|agrego|configuro|pongo)|no encuentro',
  'soporte'),
 ('interes', 'Muestra interés',
  r'me interesa|quiero verlo|mande nomas|mandame|me encanta|me gusta|me sirve|esta (muy )?(bueno|buena|interesante)|que bueno|re interesante|suena bien|esta genial',
  'avanzar_guion'),
 ('como_funciona', 'Cómo funciona / qué incluye',
  r'^(y )?(como(?! (estas|va|andas|estan|anda|te va|les va))|se puede|puede|hay forma|que pasa)\b|puede ser con|se puede|como hago|puedo (hacer|poner|usar|cargar)|como funciona|como es (el|la)|^que (me )?ofrecen|tengo que (cargar|mandar|abonar|pedir|ingresar|pagar)|es (una )?app o (es )?web|en espanol|es mensual|una sola vez|que (hace|incluye|tiene|ofrece|trae)|de que se trata|(mas|mayor) (info|informacion|detalle)|explica|como seria|tiene (app|aplicacion)|sirve para|se puede|funciona con|hay (app|aplicacion)|como se (usa|maneja)',
  'faq'),
 ('dato_calificacion', 'Responde una pregunta del guion',
  r'\bpapel\b|\bexcel\b|\bword\b|cuaderno|nunca tuve|aprox|aproximad|\bmanual\b|se llama|mi (gym|gimnasio|estudio|local|negocio) es|planilla|a mano|agenda|no uso nada|no usamos nada|desde cero|de cero|\bN obras?\b|alrededor de N|al rededor de N|unos N|mas o menos N|\bN (a|o) N\b|\bN (socios|alumnos|clientes|personas|miembros|chicos)\b|^N( aprox| mas o menos| masomenos)?$|pase libre|\bclases\b|funcional|crossfit|pilates|musculacion|estetica|peluqueria|barberia|consultorio|no tengo (socios|alumnos)|ningun sistema|por whatsapp|efectivo|transferencia',
  'avanzar_guion'),
 ('saludo', 'Saludo', r'^(hola|holis|buenas|buen dia|buenos dias|buenas tardes|buenas noches|que tal|como estas|como va|como andas)( (hola|buenas|buen dia|buenos dias|buenas tardes|que tal|como estas|como va|como andas|como estan|todo bien|bien|y vos|eric|mateo|si|dale|gracias)){0,5}$', 'saludar_y_seguir'),
 ('ack', 'Confirmación / gracias', rf'^({ACK})( {ACK}){{0,4}}( (audio|image|video|sticker)message)*$', 'no_responder_o_seguir'),
]
COMPILED=[(k,n,re.compile(p),a) for k,n,p,a in INTENTS]

# Contexto: si nuestro último mensaje hizo una pregunta del guion, una respuesta corta es la respuesta.
RAW_HANDLE = re.compile(r'^(?=.*[a-zA-Z])(?=.*[._\d])[\w.]{5,}$')
Q_MAIL = re.compile(r'(pasame|pasas|me pasas|dejame|decime) (tu |el )?mail')
Q_HOW_PAY = re.compile(r'como (llevas|cobras|manejas|registras|administran|llevan)|excel papel')
Q_COUNT = re.compile(r'cuant(os|as) (alumnos|socios|clientes|obras|canchas|profesionales|personas)')
Q_NAME = re.compile(r'como se llama|nombre (del|de tu|de la)|decime (solo )?el nombre')
Q_ANY  = re.compile(r'\?|como (llevas|cobras|tomas|manejas)|cuantos|que (usas|sistema|rubro)')

def classify(text, prev=None):
    t = norm(text)
    if not t: return 'vacio'
    words = len(t.split())
    # Si le preguntamos cómo cobra y contesta con su método, es la respuesta aunque agregue algo más.
    if prev and Q_HOW_PAY.search(norm(prev)) and words <= 40 and re.search(r'\b(excel|papel|cuaderno|planilla|manual|a mano)\b', t):
        return 'dato_calificacion'
    for k,_,rx,_ in COMPILED:
        if k == 'horario' and words > 15: continue
        if k == 'dato_calificacion' and words > 25: continue
        if rx.search(t): return k
    if prev:
        p = norm(prev)
        if Q_MAIL.search(p) and RAW_HANDLE.match((text or '').strip()): return 'email_incompleto'
        if Q_NAME.search(p) and len(t.split()) <= 6: return 'dato_calificacion'
        if Q_COUNT.search(p) and re.search(r'\bN\b', t) and len(t.split()) <= 15 and '?' not in (text or ''): return 'dato_calificacion'
        if Q_ANY.search(p) and len(t.split()) <= 4: return 'dato_calificacion'
    return 'otro'

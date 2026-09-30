"""Los scripts viven en tools/diccionario (en git); los datos, en docs/conversaciones/v2 (fuera de
git: son chats reales de leads). Cada script hace chdir a DATA al arrancar."""
import os
TOOLS = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(TOOLS, '..', '..'))
DATA = os.environ.get('DICCIONARIO_DATA', os.path.join(REPO, 'docs', 'conversaciones', 'v2'))
os.makedirs(DATA, exist_ok=True)
def use_data():
    os.chdir(DATA)

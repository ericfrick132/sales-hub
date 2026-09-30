import re, unicodedata
EMO = re.compile('[\U0001F000-\U0001FAFF☀-➿️‍]')
def norm(t):
    t = EMO.sub(' ', t or '').lower()
    t = ''.join(c for c in unicodedata.normalize('NFD', t) if unicodedata.category(c) != 'Mn')
    t = re.sub(r'https?://\S+', ' URL ', t)
    t = re.sub(r'\S+@\S+\.\S+', ' EMAIL ', t)
    t = re.sub(r'\$\s?\d[\d.,]*', ' PRECIO ', t)
    t = re.sub(r'\d+', ' N ', t)
    t = re.sub(r'[^a-zñ\sA-Z]', ' ', t)
    t = re.sub(r'(.)\1{2,}', r'\1', t)          # holaaa -> hola, siii -> si
    t = re.sub(r'([aeious])\1+\b', r'\1', t)     # okaa -> oka, sii -> si, graciass -> gracias
    return re.sub(r'\s+', ' ', t).strip()

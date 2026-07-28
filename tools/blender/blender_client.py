#!/usr/bin/env python3
"""Client per l'estensione MCP ufficiale di Blender (Blender Lab, id "mcp").

L'estensione espone un server TCP su 127.0.0.1:9876 che parla JSON delimitato
da null byte. Questo client invia uno script Python da eseguire dentro Blender
e stampa la risposta.

Contratto del codice inviato (imposto dall'estensione):
  - lo script DEVE assegnare una variabile `result` di tipo dict serializzabile;
  - stdout/stderr dello script vengono catturati e restituiti nella risposta.

Uso:
    python tools/blender/blender_client.py script.py [arg ...]
    echo "import bpy; result = {'v': bpy.app.version_string}" | python tools/blender/blender_client.py -

Gli argomenti extra arrivano allo script come lista `ARGV`.
"""

import json
import os
import socket
import sys

HOST = "127.0.0.1"
PORT = 9876
TIMEOUT = 600.0  # Alcune operazioni (subdivision, weight, export) sono lente.

# Radice del progetto, ricavata da dove sta QUESTO file (tools/blender/ -> su di due).
#
# Gli script vengono spediti a Blender come sorgente, non come file: dentro Blender non
# esiste __file__ e non c'e' modo di dedurre dove stia il progetto. Prima il percorso era
# scritto a mano in ogni script ("c:/repositories/lagoon"), quindi la pipeline funzionava
# su una sola macchina e su un solo sistema operativo. Ora lo inietta il client.
PROJECT_DIR = os.path.dirname(os.path.dirname(os.path.dirname(
    os.path.abspath(__file__)))).replace("\\", "/")


def preamble(project_dir, argv):
    """Righe iniettate in testa a ogni script spedito a Blender."""
    return (
        "import os as _os, sys as _sys\n"
        "PROJECT_DIR = {project!r}\n"
        "_os.environ['LAGOON_PROJECT_DIR'] = PROJECT_DIR\n"
        "_tools = PROJECT_DIR + '/tools/blender'\n"
        "if _tools not in _sys.path:\n"
        "    _sys.path.append(_tools)\n"
        "ARGV = {argv!r}\n"
    ).format(project=project_dir, argv=list(argv))


def execute(code, host=HOST, port=PORT, timeout=TIMEOUT):
    """Esegue `code` dentro Blender e restituisce il dict di risposta."""
    request = json.dumps({"type": "execute", "code": code, "strict_json": True})
    with socket.create_connection((host, port), timeout=timeout) as sock:
        sock.settimeout(timeout)
        sock.sendall(request.encode("utf-8") + b"\0")

        chunks = []
        while True:
            data = sock.recv(65536)
            if not data:
                raise ConnectionError("Connessione chiusa da Blender senza risposta completa")
            chunks.append(data)
            if b"\0" in data:
                break

    payload = b"".join(chunks).split(b"\0", 1)[0]
    return json.loads(payload.decode("utf-8"))


def main(argv):
    if len(argv) < 2:
        print(__doc__, file=sys.stderr)
        return 2

    if argv[1] == "-":
        code = sys.stdin.read()
    else:
        with open(argv[1], "r", encoding="utf-8") as handle:
            code = handle.read()

    # Radice del progetto, sys.path dei moduli condivisi e argomenti extra: tutto iniettato
    # in testa, cosi' gli script remoti non contengono un solo percorso assoluto.
    code = preamble(PROJECT_DIR, argv[2:]) + code

    try:
        response = execute(code)
    except OSError as exc:
        print("Impossibile contattare Blender su {}:{} -> {}".format(HOST, PORT, exc), file=sys.stderr)
        print("Verifica che Blender sia aperto e che l'estensione MCP sia attiva.", file=sys.stderr)
        return 3

    if response.get("stdout"):
        print("--- stdout ---")
        print(response["stdout"].rstrip())
    if response.get("stderr"):
        print("--- stderr ---", file=sys.stderr)
        print(response["stderr"].rstrip(), file=sys.stderr)

    if response.get("status") != "ok":
        print("--- ERRORE ---", file=sys.stderr)
        print(response.get("message", response), file=sys.stderr)
        return 1

    print("--- result ---")
    print(json.dumps(response.get("result", {}), indent=2, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))

#!/usr/bin/env python3
"""Bridge MCP (stdio) verso l'estensione ufficiale Blender Lab "mcp".

Perche' esiste: l'estensione ufficiale espone solo un socket TCP, non un server
MCP. I pacchetti PyPI omonimi (blender-mcp, blender-mcp-server) parlano il
protocollo di un ALTRO addon (ahujasid/blender-mcp) e non sono compatibili:
mancano di `strict_json` e usano un incapsulamento diverso. Questo bridge parla
il protocollo verificato dell'estensione ufficiale.

Registrazione (gia' fatta in .mcp.json alla radice del repo):

    uv run --with mcp python tools/blender/mcp_bridge.py

Richiede Blender aperto con l'estensione MCP attiva su 127.0.0.1:9876.
"""

import json

from mcp.server.fastmcp import FastMCP

from blender_client import execute

mcp = FastMCP("blender")


@mcp.tool()
def blender_execute(code: str) -> str:
    """Esegue codice Python dentro Blender e restituisce il risultato.

    Il codice DEVE assegnare una variabile `result` di tipo dict
    JSON-serializzabile, per esempio:

        import bpy
        result = {"objects": [o.name for o in bpy.data.objects]}

    stdout e stderr dello script vengono catturati e restituiti insieme al
    risultato. In caso di eccezione viene restituito il traceback completo.
    """
    try:
        response = execute(code)
    except OSError as exc:
        return ("ERRORE: Blender non raggiungibile su 127.0.0.1:9876 ({}). "
                "Verifica che Blender sia aperto e l'estensione MCP attiva.".format(exc))

    parts = []
    if response.get("stdout"):
        parts.append("stdout:\n" + response["stdout"].rstrip())
    if response.get("stderr"):
        parts.append("stderr:\n" + response["stderr"].rstrip())
    if response.get("status") == "ok":
        parts.append("result:\n" + json.dumps(response.get("result", {}), indent=2, ensure_ascii=False))
    else:
        parts.append("ERRORE:\n" + str(response.get("message", response)))
    return "\n\n".join(parts)


if __name__ == "__main__":
    mcp.run()

# Flow protocol v1

Este es el contrato común para `FlowHub`, `Flow.Windows` y `Flow.Android`.
Los clientes son local-first: guardan la operación en su SQLite local y la
reintentan mediante `POST /v1/sync/push`. El hub no recibe audio de dictados
normales; recibe eventos de texto y metadatos. Las reuniones son la excepción:
su evento sincroniza la transcripción estructurada y, cuando existe un archivo
local, el cliente lo sube después mediante el endpoint binario de audio.

## Endpoint

La sincronización no exige Tailscale. Se recomienda una URL HTTPS del FlowHub
configurada explícitamente en cada cliente. Como transporte privado opcional se
puede usar el nombre MagicDNS publicado por Tailscale Serve o, si Serve no está
habilitado, una IP Tailscale `100.64.0.0/10` con el puerto `8790`; el tráfico
sigue viajando cifrado dentro de Tailscale y el firewall del homelab solo
permite ese rango por el adaptador Tailscale. La URL es configuración del
cliente, no una IP incrustada en el código.

La ruta preferida queda así:

```text
https://homelab.<tailnet>.ts.net
  -> Tailscale Serve
  -> http://127.0.0.1:8790
```

El fallback temporal queda así:

```text
http://<ip-tailscale-del-homelab>:8790
  -> FlowHub escuchando en el homelab
  -> firewall limitado al adaptador y rango Tailscale
```

El cliente no debe codificar la IP LAN. Serve sigue siendo preferible porque
aporta HTTPS en la propia aplicación; el fallback HTTP solo es válido sobre
la interfaz Tailscale y no debe reutilizarse para una IP LAN.

## Sincronización

```json
{
  "deviceId": "msi-pablo",
  "operations": [
    {
      "eventId": "019...",
      "entity": "dictations",
      "entityId": "019...",
      "operation": "create",
      "payload": { "text": "...", "language": "es" }
    }
  ]
}
```

El `eventId` es una clave de idempotencia. Si el cliente reintenta la misma
operación, el hub no la duplica, pero la incluye en
`acknowledgedEventIds` para que el cliente pueda retirar exactamente esa
operación de su outbox. `serverSeq` es monotónico y se usa para
`GET /v1/sync/pull?after=<seq>`; no se usa `updated_at` como orden global.
Los dispositivos se actualizan sin crear eventos repetidos si no cambia su
metadato, y las reuniones usan `meetingId` como clave idempotente.

### Entidades sincronizables

Los clientes aplican `create` y `upsert` como una escritura idempotente local,
y `delete` como una eliminación por `entityId`. La última operación recibida
según `serverSeq` gana para una misma entidad.

| Entidad | Contenido sincronizado | No incluye |
|---|---|---|
| `dictations` | texto final, transcripción original, duración, aplicación, modelos y favorito | audio WAV |
| `dictionary` | término, sustitución, categoría y fecha | — |
| `snippets` | disparador, expansión, categoría y fecha | — |
| `settings` | solo claves `correction_*` y `style_*` | API keys, token del Hub, URL, micrófono, tema y dispositivo |
| `meetings` | metadatos, transcripción, resumen, acuerdos, tareas y segmentos con `speaker`, `startMs`, `endMs` y `text` | audio se transporta aparte, nunca dentro del evento JSON |

Los datos existentes en el cliente Windows se exportan una sola vez al
outbox mediante un snapshot idempotente. Las modificaciones posteriores se
encolan en el momento de guardar, marcar favorito, borrar o cambiar una
preferencia. Así, un cliente que estuvo sin red converge al volver a tener
acceso, sin que una respuesta parcial vacíe su cola local.

## Eventos en tiempo real

`GET /v1/events/ws` acepta WebSocket y emite los mismos eventos confirmados
por REST. La pérdida temporal del socket no pierde datos: el cliente vuelve a
`pull` desde su último `serverSeq`.

## Reuniones

`POST /v1/meetings` crea el registro y, por defecto, exporta una nota Markdown
y un `.txt` a `C:\Knowledge\Vault\Meetings\YYYY\MM\`. El audio se mantiene
fuera de la base de datos. Tras confirmar el evento de reunión, el cliente
sube el archivo con:

```text
POST /v1/meetings/<meetingId>/audio?filename=meeting.wav
X-Flow-SHA256: <sha256 del archivo>
Content-Type: application/octet-stream
```

El hub valida la reunión y la huella, conserva el archivo en su almacén de
audio y lo sirve con `GET /v1/meetings/<meetingId>/audio`, incluyendo rangos
HTTP para que el reproductor pueda saltar a un timestamp. Los clientes
mantienen su copia local para reproducir sin red; si una subida falla, la
reunión permanece pendiente y se reintenta sin duplicar el evento.

La interfaz de reuniones de Windows y Android presenta la transcripción por
segmentos, permite saltar desde cada segmento al instante correspondiente y
exporta Markdown o texto plano. Antes del resumen, el modelo aplica una pasada
de corrección contextual sobre todos los segmentos y conserva sus IDs,
hablantes y timestamps; la salida de esa pasada puede llegar hasta el máximo
de salida del modelo, independiente del límite de 2.048 tokens usado para el
resumen. El etiquetado actual es `Persona 1` como identificador provisional:
Groq Whisper proporciona segmentos y timestamps, pero no diarización de
hablantes.

## Autenticación

En el homelab se debe definir `FLOW_HUB_APP_TOKEN` fuera del repositorio. Si se
define, todas las rutas `/v1/*` requieren `Authorization: Bearer <token>`;
`/healthz` queda disponible para el supervisor local. Flow.Windows busca el
token en el destino `Flow/FlowHubAppToken` de Windows Credential Manager y
Flow.Android lo guarda en Android Keystore. En Android 14 o superior, el
acceso rápido usa una Activity translúcida efímera para que el arranque del
servicio de micrófono conserve el permiso «while in use» sin reemplazar la
aplicación que estaba en primer plano.

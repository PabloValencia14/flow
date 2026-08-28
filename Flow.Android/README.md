# Flow.Android

## Compilación reproducible

El repositorio incluye el Wrapper de Gradle fijado a Gradle 9.5.1. Con JDK 17
y el SDK de Android configurado, la APK de depuración se genera con:

```powershell
.\gradlew.bat test assembleDebug lintDebug --console=plain
```

El resultado queda en `app/build/outputs/apk/debug/app-debug.apk`. El `test`
actual no tiene fuentes de pruebas unitarias todavía; `lintDebug` sí valida el
código y el manifiesto antes de instalar.

La APK incluye el motor y una interfaz Compose funcional. La lógica sigue
separada de la presentación en `FlowEngine`: cualquier pantalla puede observar
`FlowEngineListener` y llamar a `start()`, `finish()`, `cancel()` o
`syncPending()` sin conocer los detalles de audio, red o SQLite.

El motor captura el micrófono predeterminado de Android con `AudioRecord` en
PCM16 mono a 16 kHz, emite niveles de audio para las barras y genera un WAV
válido. Después llama a Groq Whisper (`whisper-large-v3`), aplica una segunda
pasada contextual con `openai/gpt-oss-20b` y, si la corrección falla, conserva
la transcripción literal. El resultado y la transcripción original se guardan
en `sync_outbox` antes de intentar sincronizar.

La sincronización HTTP implementa `POST /v1/sync/push` y
`GET /v1/sync/pull?after=...`. Cada push elimina solo los `eventId` que
FlowHub devuelve en `acknowledgedEventIds`; una respuesta ambigua conserva la
operación local. `FlowSyncWorker` repite el push/pull cada 15 minutos cuando
hay red, la aplicación intenta sincronizar al abrirse y las reuniones lanzan
además un trabajo inmediato. Tras confirmar la reunión, el audio se sube a
`POST /v1/meetings/<id>/audio` con su SHA-256; un fallo deja la reunión en
cola para reintentar. Si FlowHub no está configurado o no responde, la
operación permanece en SQLite para reintentarla.
La clave de Groq y el token opcional de FlowHub se almacenan cifrados mediante
Android Keystore. La interfaz debe ofrecer una forma de introducirlos mediante
`FlowEngine.setGroqApiKey()` y `setFlowHubToken()`.

El editor Android aplica la misma política de reescritura contextual: no se
limita a corregir palabras, sino que reconstruye la sintaxis oral, une ideas y
corrige expresiones poco naturales manteniendo los hechos y la intención.
También resuelve rectificaciones y reinicios de pensamiento, elimina pausas y
repeticiones de tartamudeo, corrige términos solo cuando el contexto es
inequívoco y da formato a listas o párrafos cuando la intención lo indica. El
estilo sincronizado se convierte en instrucciones concretas para el modelo
(cercano, profesional, formal, conciso o técnico). Cuando el servicio de
accesibilidad está activo, Flow reconoce WhatsApp, Gmail y ChatGPT por la
aplicación en primer plano y, en navegadores, por el nombre del servicio en el
título de la ventana. WhatsApp usa el estilo personal, Gmail el de correo y
ChatGPT el neutro. Solo se conserva la etiqueta de destino normalizada; no se
guarda ni se envía el contenido de la ventana. El limpiador local
impide que una pausa termine como puntos suspensivos aunque el modelo no esté
disponible. La versión Android actual se mantiene alineada con la política de
Windows; el diccionario y los estilos configurables siguen siendo funciones
de gestión local de Windows hasta añadir su almacenamiento compartido en
FlowHub.

Las reuniones usan `MeetingRecordService` y
`MeetingSegmentRecorder`: graban en segmentos WAV de cinco minutos, los
transcriben con `verbose_json` y timestamps, corrigen la transcripción completa
por segmento sin cambiar IDs ni timestamps, los unen en un `meeting.wav`,
generan un resumen estructurado y publican el resultado mediante el outbox
común. La vista «Reuniones» permite importar audio de hasta 25 MB,
previsualizar los segmentos, reproducir el archivo saltando al timestamp
pulsado y exportar Markdown o `.txt`. La copia de audio se conserva localmente
para la reproducción y la sincronización.

Cada segmento se muestra como `Persona 1` de forma provisional. La API de
Groq usada para la transcripción proporciona timestamps, pero no separación
real de voces; por tanto, esta versión no inventa `Persona 2` o `Persona 3`.

`FlowOverlayService` ofrece la burbuja flotante sobre otras aplicaciones cuando
el usuario concede el permiso de superposición; la notificación persistente y
el acceso rápido de Android siguen funcionando si ese permiso no se concede.
La burbuja mantiene un único renglón compacto, usa medidas adaptadas a la
densidad de la pantalla, controles circulares con respuesta al pulsar,
iconos vectoriales, punto de estado animado y barras que reflejan el nivel del
micrófono. Al terminar la captura cambia a un estado «Procesando…» y después
se cierra con una animación breve.

Tailscale es opcional y no se incrusta en la APK ni se activa por defecto. La
URL de FlowHub se configura explícitamente en Ajustes y no se codifica ninguna
IP. Se acepta HTTPS para cualquier despliegue seguro y, como fallback
controlado, HTTP solo si el host es una IP Tailscale `100.64.0.0/10`; nunca se
acepta la IP LAN en el aprovisionamiento. El contrato común está en
`../protocol.md`.

La interfaz actual incluye Dictado, Reuniones, Historial y Ajustes, con tema
claro/oscuro/sistema, controles accesibles de 48 dp y barras de nivel del
micrófono. La activación global se puede hacer desde el Quick Settings Tile.
Para insertar el resultado en la aplicación que estaba en primer plano se
incluye `FlowTextAccessibilityService`, que debe activarse manualmente en
Ajustes → Accesibilidad → Flow.

La base local también aplica desde FlowHub el diccionario, los snippets y las
preferencias `correction_*` y `style_*` sincronizadas desde otro dispositivo.
El motor Android las usa en la siguiente captura: el diccionario se entrega al
corrector y los snippets se expanden después de limpiar el texto. Las claves,
la URL, el identificador del dispositivo y los ajustes propios de Android no
se propagan.

El tile comprueba los permisos antes de iniciar el foreground service. Con el
dispositivo desbloqueado usa una `QuickStartActivity` translúcida y efímera:
no abre `MainActivity`, no entra en Recientes y devuelve el foco a la
aplicación que estaba abierta. Esa ventana visible es necesaria en Android
14+ para que el servicio de micrófono conserve su permiso «while in use»;
después envía la orden a `FlowOverlayService` y la burbuja aparece por encima
de la aplicación. Con la pantalla bloqueada se conserva `unlockAndRun`. Si
falta el permiso de superposición, el tile abre la pantalla correspondiente;
si la capa rechaza el arranque, Flow registra el error sin lanzar una
excepción a SystemUI. Tras actualizar la APK, si HyperOS conserva un tile
antiguo, hay que quitar «Flow» de Ajustes rápidos y volver a añadirlo.

## Inicio rápido desde cualquier aplicación

La ruta recomendada es el tile de Ajustes rápidos, no Gboard: Android no
permite que Flow sustituya o añada un botón al micrófono del teclado de Google.
Desde Ajustes de Flow se puede solicitar la incorporación de «Flow · Grabar»
al panel donde están el brillo y el volumen. Antes del primer uso hay que
conceder el permiso de micrófono y el permiso de «mostrar sobre otras
aplicaciones». Al pulsar el tile, Flow muestra durante un instante una
ventana transparente para cumplir la restricción de Android, inicia el
servicio de micrófono y muestra la burbuja flotante con sus barras de nivel
sin cerrar ni sustituir la aplicación que estaba en primer plano; si falta el
permiso de superposición, el tile abre directamente la pantalla para
concederlo.

En el entorno de pruebas, `provision-from-windows.ps1` importa la credencial
`Flow/GroqApiKey` desde el Administrador de credenciales de Windows. La clave
se transmite por la entrada estándar de ADB, se consume una sola vez desde un
archivo privado temporal de la app y termina cifrada en Android Keystore. No
se incluye en el código ni en los recursos de la APK.

Los dos scripts de aprovisionamiento aceptan `-DeviceSerial` para seleccionar
de forma explícita el dispositivo cuando hay más de uno conectado por ADB; el
valor predeterminado conserva la Xiaomi Pad 7 de pruebas.

## Inserción en la aplicación activa

El servicio de accesibilidad solo se utiliza al terminar un dictado iniciado
explícitamente por el usuario. Flow localiza el campo editable que conserva el
foco en la aplicación activa y primero intenta insertar con `ACTION_PASTE`, por
lo que el texto ya escrito y la posición del cursor se conservan. Si el editor
no expone esa acción, usa `ACTION_SET_TEXT` reconstruyendo el valor alrededor
del cursor. No actúa sobre campos de contraseña. Si no hay un campo editable,
si la aplicación rechaza la acción o el servicio no está activado, el resultado
se copia al portapapeles y Flow muestra el motivo en el estado de la burbuja.

La autorización de accesibilidad es necesaria porque Android no ofrece una API
normal para que una aplicación escriba directamente en el campo de otra. El
servicio está limitado al nodo con foco y no mantiene una lectura continua del
contenido de las aplicaciones.

La entrada de la burbuja utiliza el patrón de `Panel reveal` de
[Transitions.dev](https://transitions.dev): desplazamiento vertical desde
arriba, opacidad progresiva y la curva de salida suave equivalente a
`cubic-bezier(0.22, 1, 0.36, 1)`. La salida usa el mismo recorrido en sentido
contrario y respeta la escala de animación del sistema.

`provision-flowhub-from-windows.ps1` hace el mismo proceso para
`Flow/FlowHubAppToken` y recibe la URL de FlowHub como parámetro. La URL y el
token se consumen desde el staging privado de la aplicación; el token no se
imprime ni se incluye en la APK.

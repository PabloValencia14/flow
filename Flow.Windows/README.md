# Flow.Windows

Este esqueleto WPF se convertirá en el cliente que sustituya a Wispr Flow.
Wispr Flow 1.6.606 se mantiene temporalmente como referencia de prueba; no se
instalarán capturadores ni atajos duplicados hasta que el modo de comparación
esté preparado.

Flow.Windows tendrá su propio estado local y hablará con FlowHub contra
`../protocol.md`. No leerá ni modificará `%APPDATA%\Wispr Flow\flow.sqlite`.

La interfaz usa una paleta monocroma sobria en los temas oscuro y claro:
negro, blanco y grises neutros. Los degradados y colores de estado se han
eliminado; la grabación y el procesamiento se distinguen mediante contraste,
animación y texto.

## Prueba temporal

El proyecto se publica como aplicación Windows (`WinExe`), por lo que el
ejecutable no abre una consola asociada. Para una instalación de usuario se
debe ejecutar `install.ps1`: publica en `%LOCALAPPDATA%\Programs\Flow`, crea
el acceso directo del menú Inicio y registra el arranque automático.

`install.bat` es únicamente el lanzador del instalador y puede mostrar una
terminal mientras instala; esa terminal no forma parte de Flow. Después de
instalar, se debe abrir Flow desde el acceso directo del menú Inicio o desde
`Flow.Windows.exe` en `%LOCALAPPDATA%\Programs\Flow`.

Flow se mantiene residente sin mostrar el panel ni un botón en la barra de
tareas mientras está cerrado. El proceso, el atajo global, la barra flotante y
el icono del área de notificación siguen activos. El clic izquierdo en el icono
de notificación o la opción `Abrir Flow` muestran el panel; mientras está
abierto Windows puede mostrar su botón de tarea. Los botones de minimizar y
cerrar del panel lo devuelven al segundo plano y eliminan ese botón sin detener
el proceso. El menú contextual del icono permite abrir Flow o salir; Windows
puede colocar el icono dentro del desplegable `^` de iconos ocultos según la
configuración del usuario.

Para desarrollo, compila y ejecuta `Flow.Windows.exe` desde la carpeta
`bin\Debug\net9.0-windows`.
Mientras Wispr siga instalado, Flow usa su propio atajo `Ctrl+Win`; no reclama
los atajos de Wispr. Mantener `Ctrl+Win` activa el modo pulsar-para-hablar:
empieza al presionar y termina al soltar. Dos pulsaciones cortas seguidas dejan
el dictado activo; otras dos pulsaciones lo terminan y pegan el texto. También
se puede probar con los botones de la ventana.

Durante la captura aparece una burbuja flotante centrada en la parte superior
de la pantalla. Es una ventana transparente al foco y a los clics: no roba el
foco del editor. Entra con una animación breve de escala/opacidad, usa blanco
y grises neutros para los distintos estados y desaparece al terminar o si
Groq devuelve un error. Las barras centrales reaccionan al nivel de señal de
cada bloque del micrófono para indicar que la grabadora está recibiendo audio.

La captura usa el dispositivo de entrada WASAPI que se haya seleccionado en
Ajustes y recuerda su identificador en la base local. Si ese dispositivo ya no
está conectado, Flow usa temporalmente el predeterminado de Windows sin borrar
la selección guardada, de modo que puede recuperarla cuando vuelva a aparecer.
Convierte el formato flotante habitual del micrófono a PCM de 16 bits antes de
crear el WAV. Si Windows no entrega muestras, Flow no envía un WAV vacío a
Groq y muestra el diagnóstico local en lugar de producir un `400` remoto.
La parada espera a que WASAPI entregue el último bloque (`RecordingStopped`)
antes de cerrar el WAV. La credencial de Groq se decodifica como UTF-16LE
cuando así la devuelve Windows Credential Manager y se sanea solo en memoria.
El cliente recuerda la última ventana externa en primer plano y la restaura
antes de pegar, por lo que los botones no dejan el texto dentro de Flow.

Después de Whisper, Flow aplica una segunda pasada de reescritura contextual con
`openai/gpt-oss-20b`. No se limita a corregir palabras: reconstruye la sintaxis
oral, une ideas, corrige expresiones poco naturales y elimina arranques
abandonados, manteniendo todos los hechos, nombres, cifras, negaciones,
condiciones, matices y peticiones. También resuelve autocorrecciones habladas
como «martes, no, miércoles» y no usa puntos suspensivos para pausas o dudas.
El texto fuente se entrega como datos delimitados y la respuesta se valida antes
de pegarla: si GPT-OSS devuelve una explicación, saludo, respuesta
conversacional, razonamiento o JSON, Flow la descarta y usa la limpieza local
del texto de Whisper. Así una pausa no termina pegándose como `...` ni una
respuesta del modelo aparece en el editor.

La reescritura contextual recibe también el nombre del proceso de destino y el
diccionario personal local. Según la aplicación, conserva sintaxis técnica,
usa un tono profesional para correo, conciso para mensajería de trabajo o
natural para mensajes personales. Los perfiles no son solo etiquetas: cada uno
indica al modelo cómo reformular la frase sin cambiar su significado. En Ajustes
se puede activar o desactivar por separado la eliminación de muletillas, la
eliminación de repeticiones, la resolución de autocorrecciones y el formato de
párrafos/listas. Los snippets definidos en Flow se expanden después de la corrección, con
coincidencia de palabra completa, para no enviar su contenido privado al
modelo.

Por ejemplo, un dictado como «Bueno, yo lo que quería comentarte es que, a ver,
el informe lo terminamos mañana, bueno, el jueves» puede quedar como «El
informe lo terminamos el jueves». En un perfil formal o profesional, el mismo
criterio se aplica además al tono y a la estructura; en el perfil cercano se
mantiene la naturalidad sin conservar muletillas ni errores.

La vista «Estilos» permite guardar un perfil independiente para mensajería de
trabajo, correo, código/prompts y mensajes personales. Cada perfil ofrece
modo automático, profesional, formal, conciso, técnico o cercano según lo
que tenga sentido para ese destino. La selección se guarda de forma
transaccional en `app_settings` local y, al terminar un dictado, se combina con el proceso que estaba en
primer plano antes de grabar. Si se elige «Automático», se usan los valores
predeterminados: profesional para trabajo, formal para correo, técnico para
código y cercano para mensajes personales. En Gmail o Slack abiertos dentro
de un navegador, Windows solo expone el proceso del navegador; para esos
casos se usa el perfil general salvo que la aplicación anfitriona sea
detectable.

El tema visual, el micrófono y los efectos de sonido son preferencias propias
del equipo y se restauran antes de que el atajo global pueda iniciar una
grabación. Las opciones de corrección y estilo se guardan junto con su evento
de sincronización en la misma transacción; así no pueden quedar guardadas en
la interfaz pero ausentes de la cola si Flow pierde la conexión. Los snippets
se conservan en SQLite y sus valores predeterminados solo se inicializan una
vez: borrar todos los snippets no los vuelve a crear al reiniciar.

Para la primera prueba se puede definir `FLOW_GROQ_API_KEY` solo en la sesión
del proceso. El modo previsto usa Windows Credential Manager con el destino
`Flow/GroqApiKey`; la clave no entra en SQLite, logs ni el repositorio.

El Hub es opcional para dictar: si no está configurado o no responde, el texto
se inserta y queda en la cola local. La URL se guarda en la clave local
`flowhub_server_url` y puede ser el endpoint Tailscale/Serve o, mientras Serve
no esté habilitado, una IP Tailscale `100.64.0.0/10` con el puerto `8790`.
No se debe usar `192.168.255.200:8790` ni guardar el token en SQLite. Flow
Windows usa el destino `Flow/FlowHubAppToken` de Windows Credential Manager.
Cada ciclo registra el dispositivo, sube la outbox y retira solo los eventos
confirmados; después aplica los eventos nuevos desde el último `serverSeq`.
Se sincronizan historial, favoritos y borrados, diccionario, snippets y
preferencias de corrección/estilo. Las credenciales y la configuración propia
del equipo permanecen locales.

## Reuniones

La vista «Reuniones» permite importar un audio de hasta 25 MB, transcribirlo
con segmentos y timestamps, reproducirlo con una línea temporal clicable y
exportar Markdown o texto plano. Las grabaciones creadas en Flow se conservan
localmente y se sincronizan con FlowHub en dos fases: primero el evento JSON de
la reunión y después el audio binario con SHA-256. Si una fase falla, la cola
local se conserva para reintentar sin duplicar la reunión.

La etiqueta de hablante que se muestra ahora es `Persona 1` provisional. El
servicio de Speech-to-Text empleado entrega timestamps de segmentos, pero no
diarización, por lo que aún no se asignan automáticamente `Persona 2` o
`Persona 3`. En reuniones se ejecuta una pasada contextual independiente que
devuelve la transcripción completa corregida, un elemento por segmento, y
conserva sus IDs, hablantes y timestamps. El resumen mantiene su propia salida
acotada y no limita la longitud de la transcripción corregida.

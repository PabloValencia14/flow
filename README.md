# Flow

## Dictado por voz, reuniones y sincronización entre dispositivos

Flow es una herramienta de productividad por voz para Windows y Android. Convierte el habla en texto listo para pegar, mejora la transcripción respetando el contexto y permite organizar reuniones y dictados desde una arquitectura local-first.

El proyecto está pensado para quienes quieren controlar sus datos y, al mismo tiempo, aprovechar modelos de reconocimiento y corrección de voz. El audio y el historial se mantienen en el dispositivo; las peticiones a Groq solo se realizan cuando se inicia una transcripción o una corrección.

> Estado: proyecto en desarrollo activo. Las funciones principales de dictado, corrección contextual, reuniones, sincronización y acceso rápido están implementadas, aunque algunas integraciones todavía requieren validación en cada dispositivo.

## Funcionalidades

### Dictado inteligente

- Captura el micrófono predeterminado o el dispositivo seleccionado por el usuario.
- Transcribe en español con Groq Whisper.
- Corrige errores fonéticos cuando el contexto lo permite.
- Reescribe el lenguaje oral o informal como texto natural, con el estilo elegido.
- Resuelve autocorrecciones habladas, reinicios de pensamiento, repeticiones y muletillas.
- Evita que las pausas se conviertan en puntos suspensivos.
- Adapta el formato al destino: mensajes, correo, código o texto técnico.
- Detecta el destino por la ventana y la página activa: WhatsApp cercano, Gmail formal y ChatGPT neutro.
- Expande snippets después de corregir el texto.

En Windows, Flow puede iniciarse en segundo plano y activarse con `Ctrl+Win`. La burbuja de grabación muestra el estado y el nivel del micrófono sin robar el foco de la aplicación activa.

### Reuniones y clases

- Importación de audio de hasta 25 MB por archivo.
- Transcripción con segmentos y timestamps.
- Corrección contextual de la transcripción completa.
- Reproducción sincronizada con cada segmento.
- Exportación a Markdown o texto plano (`.txt`).
- Sincronización opcional del texto y del audio con FlowHub.

La versión actual conserva `Persona 1` como identificador provisional. Groq proporciona timestamps, pero no realiza diarización real de voces; por tanto, la asignación automática de `Persona 2`, `Persona 3`, etc. todavía no está incluida.

### Windows y Android

- **Windows:** aplicación residente, atajo global, burbuja de grabación, inserción directa en la aplicación activa, estilos, snippets y procesamiento local de preferencias.
- **Android:** Quick Settings Tile, burbuja sobre otras aplicaciones, grabación desde el micrófono predeterminado, servicio de accesibilidad para insertar texto y vista de reuniones.

Consulta la documentación específica de [Flow.Windows](Flow.Windows/README.md) y [Flow.Android](Flow.Android/README.md) para conocer permisos, atajos y comportamiento detallado.

## Instalación rápida en Windows

El repositorio incluye un instalador autocontenido para Windows x64. No hace falta instalar .NET en el ordenador de destino.

```powershell
git clone https://github.com/PabloValencia14/flow.git
cd flow
.\install-flow.bat
```

El instalador:

1. Instala Flow en `%LOCALAPPDATA%\Programs\Flow`.
2. Crea el acceso directo del menú Inicio.
3. Configura el arranque residente en segundo plano.
4. Solicita la clave de Groq.

La clave se guarda en el Administrador de credenciales de Windows con el destino `Flow/GroqApiKey`. No se almacena en SQLite, en archivos de texto, en los argumentos del proceso ni en el repositorio.

También se puede descargar directamente el [instalador autocontenido](installer/release/Flow-Windows-Installer.zip).

## Sincronización entre dispositivos

Flow puede funcionar sin servidor: el dictado y las reuniones se guardan localmente y se reintentan cuando sea necesario. Para compartir datos entre Windows y Android se puede desplegar el servidor opcional [FlowHub](FlowHub/) y configurar su URL en cada cliente.

La sincronización puede incluir dictados, reuniones, historial, favoritos, diccionario, snippets y preferencias de corrección y estilo. Las claves, el micrófono, el tema y otros ajustes propios del dispositivo permanecen locales.

Tailscale no es un requisito ni se activa durante la instalación. Puede utilizarse de forma explícita como transporte privado para acceder a FlowHub, pero Flow no instala el cliente, inicia sesión, cambia rutas ni configura Serve por defecto.

## Desarrollo

### Windows

Se necesita el SDK de .NET 9:

```powershell
dotnet restore .\Flow.Windows\Flow.Windows.csproj
dotnet run --project .\Flow.Windows\Flow.Windows.csproj
```

Para regenerar el instalador autocontenido:

```powershell
.\installer\Build-FlowInstaller.ps1 -Clean
```

El resultado se genera en `installer/release`. El ZIP incluido en el repositorio permite instalar Flow en otro equipo sin disponer del SDK.

### Android

Abre `Flow.Android` en Android Studio con un SDK Android configurado. Antes de usar el acceso rápido, Android requiere conceder el permiso de micrófono y, para la burbuja, el permiso de mostrar sobre otras aplicaciones. La inserción automática en la aplicación activa requiere habilitar el servicio de accesibilidad de Flow.

## Estructura del repositorio

```text
Flow.Windows/    Cliente WPF para Windows
Flow.Android/    Aplicación Android y servicios de captura
FlowHub/         Servidor opcional de sincronización
installer/       Instalador autocontenido y paquete distribuible
protocol.md      Contrato común de sincronización
acceptance.md    Criterios de validación del proyecto
```

## Privacidad y límites conocidos

- Se necesita una clave de Groq para transcribir y corregir audio.
- La cuenta gratuita de Groq limita las subidas de audio a 25 MB por petición en esta versión.
- La corrección contextual depende del modelo disponible en Groq; si falla, Flow conserva la transcripción literal y aplica la limpieza local.
- FlowHub es opcional y no recibe audio de dictados breves; el audio de reuniones solo se sincroniza cuando el usuario configura esa función.
- Las reuniones todavía no disponen de separación automática real de hablantes.

## Documentación

- [Guía de Windows](Flow.Windows/README.md)
- [Guía de Android](Flow.Android/README.md)
- [Contrato de sincronización](protocol.md)
- [Criterios de aceptación](acceptance.md)
- [Instalador reproducible](installer/README.md)

# Integración con Wispr Flow instalado

## Hechos comprobados en el MSI

- Aplicación: `Wispr Flow 1.6.606`.
- Ejecutable: `%LOCALAPPDATA%\WisprFlow\app-1.6.606\Wispr Flow.exe`.
- Arranque automático: acceso directo en la carpeta Startup del usuario.
- Estado local observado: `%APPDATA%\Wispr Flow\flow.sqlite` y sus ficheros
  WAL/SHM, además de `config.json`.

La base y la configuración pertenecen a Wispr Flow. No se deben sincronizar
por Syncthing, copiar mientras el programa está abierto ni editar para forzar
una integración: son datos vivos de una aplicación de terceros y pueden
contener historial, preferencias y material sensible.

## Papel temporal

Wispr Flow no es el producto final de esta carpeta. Se conserva únicamente
como referencia funcional mientras se construye Flow.Windows. No se debe
desinstalar, cerrar de forma disruptiva ni modificar su configuración durante
la implementación salvo que el usuario lo pida expresamente.

La retirada queda bloqueada hasta que Flow.Windows haya demostrado, en este
MSI, captura, transcripción, inserción, reintento offline y sincronización
con el Hub, además de una comparación manual satisfactoria con Wispr Flow.

## Arquitectura durante la transición

```text
Wispr Flow (MSI)
  ├─ captura y transcripción de dictado
  ├─ formato, backtrack, diccionario y pegado
  └─ su propia cuenta/almacenamiento
          │
          │ integración soportada pendiente de una API o exportación
          ▼
FlowHub por Tailscale Serve
  ├─ dispositivos y estado de sincronización
  ├─ reuniones importadas explícitamente
  ├─ eventos y WebSocket
  └─ exportación Markdown a C:\Knowledge\Vault\Meetings
```

Esto cambia una afirmación del diseño inicial: mientras Wispr Flow sea el
motor, no es correcto decir que todo el historial de dictado vive solamente
en los dispositivos y en `C:\Knowledge`. Su destino depende de la política de
privacidad y de Cloud Sync configurada en Wispr Flow.

## Límites de integración actuales

La documentación pública de Wispr Flow indica que el diccionario no tiene
exportación integrada y que la API-based export fue retirada. La API de
desarrolladores existe solo para organizaciones con acceso aprobado, por lo
que no se debe construir el despliegue suponiendo que estará disponible.

Por ello, la primera integración segura es una frontera explícita:

1. Wispr Flow continúa siendo el único capturador de dictado del MSI durante
   la fase de comparación.
2. FlowHub no intenta extraer `flow.sqlite`, `config.json` ni sus logs.
3. Las reuniones o notas que se quieran conservar en Knowledge se incorporan
   mediante un formato/exportación soportado por Wispr Flow cuando esté
   disponible, o mediante una importación manual comprobable.
4. El diccionario maestro no se duplica hasta disponer de una exportación
   soportada. Se puede preparar un CSV/JSON para importarlo en Wispr Flow,
   pero no reclamar sincronización bidireccional automática.

## Lo que no se hará mientras sea referencia

- No se inyectará una DLL ni se modificará `app.asar`.
- No se leerá la base SQLite en caliente.
- No se desactivará el arranque automático de Wispr Flow.
- No se instalarán atajos globales de Flow.Windows hasta poder desactivarlos
  durante la comparación.
- No se desinstalará Wispr Flow antes del cierre de la matriz de aceptación.
- No se enviará audio del MSI al FlowHub solo para recrear una función que ya
  presta Wispr Flow.

## Cliente sustituto

Flow.Windows hablará con FlowHub contra `../protocol.md` y tendrá su propia
captura, transcripción, formato, entrega y SQLite local. No leerá ni
modificará `%APPDATA%\Wispr Flow\flow.sqlite`.

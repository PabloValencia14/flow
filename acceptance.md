# Criterio de retirada de Wispr Flow

Wispr Flow es una referencia de prueba, no una dependencia del resultado.
No se desinstala hasta que todos estos puntos estén verificados en el MSI:

- dictado en un editor de texto, navegador y terminal;
- captura de micrófono y transcripción Groq con latencia aceptable;
- inserción sin destruir el portapapeles existente;
- backtrack, puntuación y diccionario mínimo;
- funcionamiento sin FlowHub y cola local de operaciones;
- sincronización posterior contra FlowHub sin duplicados;
- arranque y apagado controlados, sin dejar dos atajos o dos capturadores;
- comparación manual de al menos diez frases reales contra Wispr Flow;
- reuniones y cliente Android cubiertos o explícitamente aplazados por el
  usuario.

Solo después se hará una copia de seguridad de la configuración necesaria,
se desinstalará Wispr Flow con su desinstalador oficial y se comprobará que no
queda ningún proceso ni acceso directo de arranque. Esa acción será el último
paso, no parte de la instalación inicial.

## Última validación técnica

El 28 de agosto de 2026 se verificó que Windows compila en Release sin errores,
que Flow.Windows permanece en segundo plano y que el acceso directo de Inicio
apunta al ejecutable y al icono instalados. En Android, la APK 1.0.1 compila
con `test`, `assembleDebug` y `lintDebug`, se instala conservando los datos y
arranca sin excepciones fatales en la Xiaomi Pad 7 de pruebas. La ruta de red
de Android a FlowHub responde por Tailscale en el puerto configurado.

La prueba de inserción real queda pendiente de desbloquear la tableta y
activar manualmente «Flow» en Accesibilidad; Android no permite conceder ese
permiso como parte de una instalación silenciosa. El segundo dispositivo
conectado informa API 24 y no es compatible con el `minSdk` actual (29).

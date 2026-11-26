---
version: 1
slug: "flow-windows-mainwindow-xaml"
primary_target: "Flow.Windows/MainWindow.xaml"
related_targets: ["Flow.Windows/FlowStyles.xaml","Flow.Windows/RecordingIndicatorWindow.xaml"]
---

# Flow para Windows

## Scope and mode

Superficie nativa WPF de escritorio. Modo operate. Prioridad: iniciar y controlar dictado global, revisar el historial y configurar proveedores locales.

## Direction contract

THESIS: Flow debe leerse como una herramienta de dictado activa, con una retícula modular que pone el estado y el texto por delante de la decoración. Sustituye las tarjetas genéricas y la escala de grises por módulos cuadrados; la burbuja es la excepción expresamente pedida por el usuario.

OWN-WORLD: Arte bitmap y ASCII de la cultura de la informática doméstica: celdas cuadradas, color distribuido con intención y marcas tipográficas breves. Fondo verde profundo, superficies verdosas, lima para la acción, coral para grabación, azul para sincronización y ámbar para avisos. Segoe UI para leer; Consolas para índices y duración. La burbuja de grabación se inspira en Voice Island: cápsula adaptada al tema claro u oscuro, dos hebras de partículas cuadradas en tonos verdes, resplandor localizado y palabras que aparecen debajo.

STORY: Dictados muestra dispositivo, atajo, filtros, búsqueda e historial. El usuario ve cuándo captura audio, cuándo procesa localmente y cuándo termina. El indicador conserva el foco de la aplicación activa; sus palabras progresivas quedan visibles y sus acciones para cancelar o finalizar siguen disponibles. La cápsula se abre con una transición elástica; el visualizador nativo de partículas se mueve continuamente y gana amplitud e intensidad con el volumen. Al procesar se aplana y al terminar se oculta; cada palabra nueva cambia su opacidad sin volver a animar las anteriores.

FIRST VIEWPORT: Barra de ventana arriba, navegación lateral con nombres e índices ASCII, contenido de Dictados a la derecha. Micrófono, Ctrl+Win y Dictar permanecen en la cabecera; filtros y búsqueda preceden la lista. La burbuja conserva cápsula en tema claro u oscuro, cuadrado Cancelar, dos hebras de puntos verdes que responden al volumen, duración, texto vivo debajo y control Finalizar.

FORM: Dirección asignada 3 de 7, derivada de la petición del usuario: informática doméstica en ASCII y píxeles. La retícula y la navegación conservan las esquinas cuadradas; Voice Island es la referencia explícita de la cápsula de grabación, incluida su línea fina de puntos luminosos. El seed de concepto es seed:aa1f4f409874b279.

FINISH: unreviewed and undocumented is unfinished; this build ends with the finish review, the verdict, DESIGN.md, and every shipping raster carrying its provenance

# Revisión de Flow y propuesta de rediseño

Fecha: 30 de octubre de 2026. Prioridad confirmada por el usuario: Windows.

## Conclusión

Flow necesita concentrar su interfaz en dictar, recuperar texto y trabajar con
reuniones. La pantalla de inicio actual dedica demasiado espacio a presentar
el producto. El cambio importante es de jerarquía e interacción, manteniendo
el cliente nativo WPF y su identidad monocroma. No hace falta cambiar de
framework ni añadir dependencias para conseguirlo.

Esta propuesta es un borrador de producto, no un diseño visual aprobado ni una
afirmación de que el rediseño completo esté implementado.

## Evidencia y alcance

- Repositorio: `PabloValencia14/flow`, base `5227df1`, rama principal `main`.
- Inspección visual de la aplicación Windows instalada: Inicio, Actividad y
  Reuniones. Revisión de XAML, acceso a SQLite, sincronización Windows/FlowHub
  y estructura de Android/Compose.
- La copia de desarrollo tenía 40 archivos modificados y 12 rutas nuevas sin
  confirmar. Varios cambios de interfaz, audio y Android pertenecen a ese
  trabajo previo y no están en `origin/main`. No se han incluido en esta rama.
- La revisión de la interfaz se refiere al estado instalado y al código local
  observado; las correcciones de esta rama parten de la base publicada.
- Método: revisión nativa con Impeccable, sin detector web: WPF y Compose no
  son HTML/CSS. No se asigna una puntuación global de accesibilidad o rendimiento
  que implicaría comprobar más estados y dispositivos.
- Se intentó acceder a `https://designer.xyz`, pero la herramienta devolvió que
  el sitio no era accesible. No se han utilizado referencias visuales de allí.
- Las capturas de la aplicación instalada contienen historial personal y no se
  incorporan al repositorio ni a este documento.

## Problemas prioritarios

### P1 — Métricas de demostración presentadas como datos reales

`Flow.Windows/LocalOutbox.cs`, método `GetStatisticsAsync`, imponía al menos
269 palabras, 210 palabras/min y 18,5 minutos de ahorro; añadía duración
artificial y devolvía siempre un día de racha. `MainWindow.xaml` incluía un
gráfico semanal con alturas constantes y cifras iniciales ficticias.

Impacto: incluso una instalación sin historial mostraba actividad inexistente.
**Corregido en esta rama:** totales reales, ceros sin datos, racha de días locales
consecutivos y eliminación del gráfico fijo. El ahorro queda identificado como
estimación frente a una hipótesis de escritura a 40 palabras/min. Se conserva
la racha de ayer durante el día actual; un día completo sin actividad la rompe.

### P1 — La interfaz espera a la red después de acciones locales

En `FlowClient.FinishDictationAsync`, el texto se inserta y guarda antes de
`await SyncPendingAsync()`, pero el método no devuelve el resultado hasta que
acaba ese ciclo. `MainWindow.FinishDictationAsync` espera ese retorno para cerrar
el estado de procesamiento y actualizar el historial. Favoritos y borrados
también esperan a FlowHub antes de reconstruir la lista.

Impacto: una conexión lenta puede hacer que una acción ya completada parezca
pendiente. Es una dependencia comprobada en el código; no se midió su latencia
con un servidor desconectado durante esta revisión.

Propuesta: confirmar primero la escritura local y actualizar la vista; procesar
la cola con un único coordinador de sincronización que gestione errores y
cancelación. No basta con lanzar tareas sin observar sus fallos. **Pendiente.**

### P1 — Cambios de datos y eventos de sincronización no siempre son atómicos

`LocalOutbox.DeleteDictationAsync`, `ToggleFavoriteAsync`, `ClearAllHistoryAsync`
y el flujo de guardado/encolado del dictado hacen operaciones separadas.
Un cierre entre la modificación y el encolado puede dejar dispositivos con
estados distintos. Las preferencias ya usan transacciones: es un patrón que
se puede reutilizar.

Propuesta: guardar cada cambio y su evento de outbox en la misma transacción;
probar interrupciones y repetición de eventos con bases desechables. **Pendiente;
no se han hecho pruebas destructivas sobre datos personales.**

### P1 — Texto secundario con contraste insuficiente

Los tokens originales daban estas relaciones calculadas desde sus valores sRGB:
`#767676` sobre `#EEEEEE`: 3,91:1; sobre `#F7F7F7`: 4,24:1;
`#7A7A7A` sobre `#181818`: 4,14:1; sobre `#242424`: 3,62:1.
Son inferiores al objetivo de 4,5:1 para texto normal.

**Corregido en esta rama:** texto atenuado `#666666` en claro y `#929292` en
oscuro, actualizando ThemeManager y los valores iniciales de FlowStyles.
Esto corrige esos tokens, sin certificar toda la accesibilidad de la app.

### P2 — Búsqueda y listas con trabajo repetido

`MainWindow.NotesSearchBox_TextChanged` recarga con cada pulsación. Notas,
reuniones, segmentos, diccionario y snippets se construyen con `Children.Add`
y se vacían/recrean. El historial mostrado está limitado a 100 elementos;
no se ha supuesto una lista infinita.

**Corregido parcialmente:** índice sobre `dictations_history(created_at DESC)`
para el orden de recientes, sin cambiar el límite ni borrar datos.
Propuesta posterior: espera breve al escribir, descarte de resultados obsoletos,
colecciones enlazadas y virtualización con reciclaje donde el volumen lo justifique.
`Microsoft.Data.Sqlite` ejecuta sus métodos async de forma síncrona: añadir
`await` no mueve por sí solo la consulta fuera del hilo de interfaz.

### P2 — Estado duplicado y ambiguo

En el inicio observado, el estado de FlowHub se repite en la barra superior
y junto al título. El segundo bloque compite con el encabezado por el ancho.
`MainWindow.UpdateStatus` usa los mismos mensajes para ambos indicadores,
incluidos errores de micrófono y avisos de preferencias.

Propuesta: estado de captura junto al control de dictado; estado de sincronización
en un lugar permanente y discreto. Mostrar estados independientes: «Grabando»,
«Procesando», «Guardado en este equipo», «Cambios pendientes» y «Sincronizado».
«Guardado» no debe significar que el audio ya esté subido. **Pendiente.**

### P2 — Actualización parcial tras recibir cambios remotos

El temporizador de `MainWindow` recarga notas, snippets, preferencias y métricas
cuando hay eventos, pero omite reuniones y diccionario; el inicio también omite
el diccionario después del pull. La navegación recarga esas vistas, por lo que
no se concluye que los datos se hayan perdido.

Propuesta: notificar las entidades cambiadas y actualizar la vista afectada;
compartir el mismo tratamiento entre inicio, temporizador y sincronización manual.
**Pendiente.**

## Propuesta de interfaz Windows

Modo de uso Impeccable: **Operate**. El usuario llega para completar una tarea;
la densidad útil, la lectura y la respuesta inmediata tienen prioridad.

### Dictados como pantalla principal

```text
Flow                       Micrófono preparado       Sincronización
──────────────────────────────────────────────────────────────────
Dictados       Dictados                [Dictar]  Ctrl + Win
Reuniones      [Buscar dictados…]       Todos · Hoy · Favoritos
Personalizar   ────────────────────────────────────────────────────
               Lista de dictados      Detalle del seleccionado
               Fecha · destino        Texto final seleccionable
               Primera línea          [Copiar] [Favorito] [Más]
               …                      Original / Texto corregido

Ajustes        Guardado en este equipo · estado de entrega
```

El esquema expresa jerarquía e interacción, no colores ni medidas definitivas.

- Sustituir la gran presentación «Habla. Flow se ocupa del resto» por una
  franja compacta de acción. Mantener esa explicación como ayuda de primera
  ejecución, sin ocupar el espacio de trabajo habitual.
- Mostrar más historial en la primera ventana. El detalle permite leer y
  seleccionar texto largo sin expandir cada fila.
- Dar a la búsqueda una etiqueta visible. Mantener «Todos», «Hoy» y «Favoritos».
- Ofrecer «Original / Corregido» con los campos ya guardados, sin otra llamada
  al modelo y sin fingir que el original es una grabación reproducible.
- Desplazar «Vaciar historial» al menú de acciones; conservar confirmación y
  explicar que el borrado se sincroniza. La recuperación/Deshacer requiere un
  diseño de retención previo, no una promesa de interfaz sin soporte.
- Reunir diccionario, estilos y snippets como subapartados de Personalizar.
  Actividad queda como vista secundaria de uso, sin competir con dictar.

### Reuniones

- Mantener la estructura lista/detalle; ya es apropiada para esta tarea.
- Una acción principal contextual: iniciar, detener o procesar; «Importar audio»
  como alternativa. Evitar una fila de cuatro botones compitiendo con el título.
- Títulos de respaldo cuando una reunión no tenga nombre; duración y fecha
  legibles, selección evidente y estados de audio/transcripción diferenciados.
- Reproductor persistente y transcripción seleccionable con salto por timestamp.
  Resumen, acuerdos y tareas solo cuando existan datos para esas secciones.
- En ancho reducido, alternar lista y detalle con «Volver»; no estrechar dos
  columnas hasta hacerlas ilegibles. Validar también con teclado y texto ampliado.

### Ajustes y accesibilidad

- Secciones Audio, Dictado, Apariencia y Sincronización. Mantener el control
  existente para probar el micrófono sin enviar audio.
- Etiquetas persistentes, nombres accesibles para botones solo con icono,
  foco visible y orden de tabulación estable. El árbol de accesibilidad no
  se obtuvo durante la inspección; falta verificar con lector de pantalla.
- Token nuevo en un control que oculte la entrada; el TextBox actual solo
  sustituye el valor guardado por puntos, pero deja visible lo que se escriba.
- Verificar mínimo de ventana, 125/150/200 % de escala, temas claro/oscuro,
  nombres largos de micrófono, historial vacío y errores recuperables.

## Android: siguiente fase

La revisión fue de código, sin instalación ni prueba física de Android.
Hay Material 3, temas semánticos y LazyColumn: conservarlos.
La barra inferior permanece en anchos grandes; proponer rail y lista/detalle
para tablet. El sondeo de estado cada 150 ms merece sustituirse por observación
de estado ligada al ciclo de vida, después de medir consumo y recomposiciones.
Compartir el significado de los estados con Windows, respetando los controles
y permisos propios de Android.

## GitHub, instalación y sincronización de datos

Son tres circuitos distintos:

1. **Código:** commits y ramas en GitHub. Esta revisión queda en una rama
   independiente para poder revisar el cambio sin publicar trabajo local previo.
2. **Binarios:** instalar una nueva versión requiere compilar/publicar el
   ejecutable o APK. Un push no actualiza la aplicación instalada.
3. **Datos personales:** dictados, reuniones y preferencias viajan por FlowHub;
   no deben pasar por GitHub, ni incorporarse a tests o capturas públicas.

Propuesta de entrega: commits pequeños por problema; CI para Windows (incluido
en esta rama); añadir después compilación/pruebas Android y contrato de FlowHub.
Publicar instaladores mediante Releases con versión y hash vinculados al commit.
El ZIP del instalador está versionado en Git: migrarlo a Releases requiere
actualizar las instrucciones y el instalador; no se ha retirado en esta revisión.

Antes de extender el acceso a FlowHub, comprobar su configuración de autenticación:
`FlowHub/Program.cs` solo exige token cuando `FLOW_HUB_APP_TOKEN` está definido.
El valor predeterminado escucha en loopback. Esto es una condición del código,
no evidencia de que el servidor real esté expuesto o sin protección.

## Validación de esta entrega

- Compilación Release de WPF y diez comprobaciones sobre bases temporales:
  vacío, métricas reales, rachas, duración nula, estimación, día local,
  plan de consulta, conservación de filas y límite de resultados.
- Se comprobó que el test de historial vacío falla con LocalOutbox original.
- Microbenchmark, 50.000 filas sintéticas, 20 lecturas por variante:
  sin índice 29,2–37,2 ms/consulta; con índice 0,08–0,16 ms/consulta.
  Medición local, sin umbral temporal obligatorio en CI. No mide el arranque,
  el desplazamiento de listas ni el tiempo de Groq/FlowHub.
- El índice es aditivo e idempotente; su creación conserva los 50.000 registros.
  Supone espacio adicional y un pequeño coste de mantenimiento al escribir.
- La aplicación instalada, su base y sus credenciales no se han actualizado.
- Render aislado del panel de Actividad mediante WPF, en claro y oscuro y a
  820/660 píxeles de ancho de contenido. Se inspeccionaron las cuatro imágenes:
  cifras cero, texto legible y nota de estimación sin recortes. No equivale a
  una prueba de navegación de la aplicación nueva ni de escalado del sistema.

## Orden recomendado

1. Integrar las correcciones verificadas de esta rama y conciliar por separado
   el trabajo local pendiente con main.
2. `$impeccable harden`: atomicidad de la outbox, confirmación local independiente
   de red y estado de sincronización coherente.
3. `$impeccable layout` y `$impeccable distill`: Inicio orientado a dictados,
   navegación agrupada y menos presentación permanente.
4. `$impeccable optimize`: medir arranque/búsqueda/desplazamiento y abordar
   virtualización, consultas en hilo de trabajo y actualizaciones incrementales.
5. `$impeccable adapt`: ventanas reducidas, escalado Windows y tablet Android.
6. `$impeccable polish`: comprobar estados, texto, foco y consistencia final.

La opción pequeña es rehacer únicamente Inicio y sus estados, conservando las
demás pantallas. El rediseño completo abarca además Reuniones y Ajustes.

## Referencias primarias

- [Virtualización y reciclaje de controles WPF](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/optimizing-performance-controls).
- [Limitaciones asíncronas de Microsoft.Data.Sqlite](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async).
- [Fechas, UTC y modificador localtime de SQLite](https://www.sqlite.org/lang_datefunc.html).

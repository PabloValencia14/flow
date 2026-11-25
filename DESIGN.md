---
name: Flow para Windows
description: Sistema visual de la interfaz WPF de dictado de Windows
colors:
  bg-main-light: "#F0F4EB"
  bg-main-dark: "#121916"
  bg-sidebar-light: "#DDE9DB"
  bg-sidebar-dark: "#1B2720"
  bg-card-light: "#F9FBF6"
  bg-card-dark: "#202E25"
  bg-card-hover-light: "#E5EFDE"
  bg-card-hover-dark: "#2B3B30"
  bg-input-light: "#FBFCF8"
  bg-input-dark: "#171F1A"
  border-light: "#C1D2BE"
  border-dark: "#3C5041"
  border-hover-light: "#779678"
  border-hover-dark: "#627A65"
  text-primary-light: "#1E3024"
  text-primary-dark: "#F1F4E8"
  text-secondary-light: "#495E4D"
  text-secondary-dark: "#C1CABB"
  text-muted-light: "#687D6C"
  text-muted-dark: "#91A091"
  accent-light: "#2A7348"
  accent-dark: "#B9E957"
  accent-soft-light: "#B1D6B3"
  accent-soft-dark: "#596F38"
  sync-light: "#3565B8"
  sync-dark: "#80AAFF"
  recording-light: "#C4443A"
  recording-dark: "#FF7464"
  success-light: "#227A49"
  success-dark: "#78D99D"
  warning-light: "#9F620F"
  warning-dark: "#F0BE59"
  on-accent-light: "#FFFFFF"
  on-accent-dark: "#18210F"
  island-bg: "#101012"
  island-border: "#2B2B2F"
  island-ink: "#F2F2F4"
  island-text: "#EDEDEE"
  island-pending: "#8C8C90"
typography:
  title:
    fontSize: "22px"
    fontWeight: 600
  body:
    fontSize: "13px"
  supporting:
    fontSize: "12.5px"
  label:
    fontSize: "12px"
    fontWeight: 500
  mono-index:
    fontFamily: "Consolas"
    fontSize: "10px"
  mono-timer:
    fontFamily: "Consolas"
    fontSize: "11px"
  transcript:
    fontSize: "17px"
    lineHeight: "23px"
rounded:
  square: "0px"
  island: "28px"
spacing:
  gap-6: "6px"
  gap-8: "8px"
  gap-10: "10px"
  gap-12: "12px"
  gap-16: "16px"
  gap-20: "20px"
components:
  button-primary:
    backgroundColor: "{colors.accent-dark}"
    textColor: "{colors.on-accent-dark}"
    rounded: "{rounded.square}"
    padding: "9px 16px"
  button-secondary:
    backgroundColor: "{colors.bg-card-dark}"
    textColor: "{colors.text-primary-dark}"
    rounded: "{rounded.square}"
    padding: "7px 12px"
  button-secondary-hover:
    backgroundColor: "{colors.bg-card-hover-dark}"
  navigation-selected:
    backgroundColor: "{colors.accent-dark}"
    textColor: "{colors.on-accent-dark}"
    rounded: "{rounded.square}"
    padding: "8px 10px"
  filter:
    backgroundColor: "{colors.bg-card-dark}"
    textColor: "{colors.text-secondary-dark}"
    rounded: "{rounded.square}"
    padding: "5px 12px"
  card:
    backgroundColor: "{colors.bg-card-dark}"
    textColor: "{colors.text-primary-dark}"
    rounded: "{rounded.square}"
    padding: "{spacing.gap-16}"
  input:
    backgroundColor: "{colors.bg-input-dark}"
    textColor: "{colors.text-primary-dark}"
    rounded: "{rounded.square}"
  island:
    backgroundColor: "{colors.island-bg}"
    textColor: "{colors.island-text}"
    rounded: "{rounded.island}"
    padding: "9px 12px"
    width: "344px"
---

# Design System: Flow para Windows

## Overview

**Creative North Star: "Retícula de dictado de Windows y cápsula de grabación"**

Flow para Windows organiza el dictado, el historial y los ajustes en módulos rectangulares, con superficies verdes y bordes visibles. El texto describe acciones y estados; los índices ASCII ayudan a localizar funciones sin sustituir sus nombres.

El dictado global es la prioridad de esta superficie. La cápsula negra de grabación permanece sobre la aplicación activa sin activarla: muestra dos hebras luminosas de partículas en lima y verde que responden al volumen, duración y transcripción debajo cuando está disponible. Su silueta redondeada es una excepción explícita a la retícula cuadrada.

**Key Characteristics:**
- Retícula rectangular con bordes finos y selección de color sólido.
- Segoe UI para lectura de interfaz; Consolas para índices y duración. Partículas de luz dibujadas por WPF.
- Dos temas operativos; cápsula negra independiente del tema.
- Copy funcional en castellano, sin lemas.

El alcance documentado es la interfaz WPF de Windows. Fuentes: Flow.Windows/FlowStyles.xaml, ThemeManager.cs, MainWindow.xaml, RecordingIndicatorWindow.xaml, su code-behind y VoiceSignal.cs; MainWindow.xaml.cs conecta nivel de audio, texto y ciclo de dictado. PRODUCT.md y el contrato de superficie aportan las decisiones confirmadas. La captura del usuario es una referencia de forma y señal, no una fuente de medidas del programa. La secuencia de revisión en .impeccable/review/recording-motion.mp4 se capturó del componente WPF real con niveles de audio y textos simulados.

## Colors

El tema oscuro combina verdes profundos y lima; el claro usa verdes pálidos y un acento verde más oscuro para mantener la lectura. Los valores exactos están en el frontmatter; los sufijos dark y light representan las dos ramas de ThemeManager.UpdateBrushes.

### Primary

- **accent-dark / accent-light:** acción primaria, navegación y filtros seleccionados. Corresponden a BrushAccentPurple y al recurso sólido BrushWisprGradient; sus nombres históricos no indican púrpura ni degradado.
- **accent-soft-dark / accent-soft-light:** recurso de apoyo BrushAccentPurpleDark; no implica una segunda familia de marca.
- **on-accent-dark / on-accent-light:** texto y marcas sobre accent.

### Secondary

- **sync-dark / sync-light:** azul operativo del recurso BrushAccentIndigo.
- **recording-dark / recording-light:** coral del estado de grabación.
- **success-dark / success-light:** verde de finalización.
- **warning-dark / warning-light:** ámbar para avisos y procesamiento de la cápsula.

### Neutral

- **bg-main-dark / bg-main-light:** fondo principal.
- **bg-sidebar-dark / bg-sidebar-light:** navegación y barra superior.
- **bg-card-dark / bg-card-light:** módulos y controles secundarios.
- **bg-card-hover-dark / bg-card-hover-light:** interacción de controles y fondo de campo enfocado.
- **bg-input-dark / bg-input-light:** campos en reposo.
- **border-dark / border-light:** delimitación y separadores.
- **border-hover-dark / border-hover-light:** borde interactivo y pulgar del scroll.
- **text-primary-dark / text-primary-light:** texto principal.
- **text-secondary-dark / text-secondary-light:** explicaciones y navegación en reposo.
- **text-muted-dark / text-muted-light:** metadatos.
- **island-bg / island-border:** negro y borde fijo de la cápsula; ThemeManager no los reemplaza.
- **island-ink:** controles de la cápsula.
- **island-text / island-pending:** texto estable y última palabra pendiente; pending también se usa en duración y procesamiento.
- **accent-dark / success-dark:** los tonos lima y verde de las dos hebras del visualizador; permanecen fijos sobre el fondo negro de la cápsula en ambos temas.

**The Operational Color Rule.** Reserva accent para acción y selección; recording, sync, success y warning distinguen estados operativos, con texto que explicita su significado.

## Typography

**Body Font:** Segoe UI como fuente de interfaz predeterminada de WPF en Windows; el código inspeccionado no fija una familia global.
**Label/Mono Font:** Consolas, declarada explícitamente para índices, identificación de plataforma y duración.

La jerarquía es compacta y funcional. No existe una escala display de marca ni una razón matemática de escalado declarada. Los tamaños del frontmatter expresan valores XAML en unidades independientes del dispositivo (DIP), representadas como px nominales para interoperabilidad; no son medidas de píxeles físicos.

### Hierarchy

- **Title:** title en Dictados (SemiBold); los otros títulos de sección usan la misma medida con Bold. Los títulos de módulos usan 14–14.5 DIP y SemiBold; el detalle de reunión usa 18 DIP.
- **Body:** body para campos y explicaciones; supporting para apoyo en ajustes y botones secundarios.
- **Label:** label en filtros; navegación a 13 DIP con Medium y SemiBold al seleccionar.
- **Mono:** mono-index para índices; mono-timer para duración. Los grupos laterales observados usan Consolas 9.5 DIP Bold; se documentan como evidencia local y no como patrón de encabezado para nuevas pantallas.
- **Signal:** partículas geométricas dibujadas por VoiceSignal; no dependen de una familia tipográfica.
- **Transcript:** transcript para palabras de la cápsula, con wrap y última palabra en island-pending.

**The Text Role Rule.** Mantén los nombres y el texto legible en la tipografía de interfaz; limita Consolas a índices, marcas ASCII y duración.

## Layout

La ventana principal tiene tamaño inicial 1112 × 752 DIP y mínimo 912 × 632 DIP; no se implementan breakpoints de móvil. El marco tiene margen 12, borde 1 y una fila de título de 52. La columna lateral mide 230; el contenido ocupa el resto. Dictados usa márgenes 28 horizontales y 24 verticales; otras vistas usan 30 y 22. No se convierte esta diferencia local en una escala de espaciado nueva.

Las filas Auto contienen cabeceras y herramientas; las listas y ajustes usan la altura restante con scroll vertical. La búsqueda se estira entre filtros y acción secundaria. En reuniones, la lista mide 250 y el separador 16. Los formularios combinan columnas proporcionales y separadores de 10, 12 o 16. El ritmo reutilizado de separación figura en spacing; los módulos WisprCard declaran padding 16.

La cápsula mide 344 de ancho dentro de una ventana transparente de 384 × 184; su mínimo de altura inicial es 52. Al mostrar texto, el mínimo de la cápsula pasa a 80, manteniendo la misma anchura y el borde inferior. La ventana se posiciona en el centro inferior del área de trabajo, a 12 DIP del borde; la cápsula conserva un margen inferior de 18. El texto tiene margen 8,7,8,0 y viewport de máximo 82 de alto con scroll vertical automático y horizontal desactivado. El code-behind conserva los últimos 900 caracteres con prefijo de elipsis y desplaza el scroll al final.

## Elevation & Depth

Los módulos internos son planos: fondos tonales y bordes de 1 separan áreas. Hay sombra exterior real en la ventana y en la cápsula; la señal tiene un resplandor localizado. No hay sombras de tarjetas ni degradado de acción.

### Shadow Vocabulary

- **Marco principal:** DropShadowEffect negro, BlurRadius 18, ShadowDepth 3, Direction 270 y Opacity 0.32.
- **Cápsula:** DropShadowEffect negro, BlurRadius 24, ShadowDepth 3, Direction 270 y Opacity 0.38.
- **Señal:** halos radiales de lima alrededor de las partículas más brillantes, dibujados en el mismo visual nativo, con radio 2.3 veces el tamaño de la partícula.

El sidecar traduce estas sombras a CSS para muestras del panel; CSS y WPF no garantizan rasterización idéntica. La entrada de cápsula anima escala horizontal 0.52 a 1 en 350 ms con BackEase, escala vertical y desplazamiento en 300 ms, y opacidad en 180 ms. Al recibir la primera transcripción se expande verticalmente en 240 ms. Solo los términos nuevos o modificados se revelan con opacidad en 220 ms, con un desfase máximo de 120 ms. La salida dura 190 ms y el pulso cuadrado alterna escala y opacidad en 800 ms con autorreversa. Se respeta ClientAreaAnimation de Windows y las animaciones continuas se detienen al ocultar, cancelar o finalizar.

## Shapes

El marco, módulos, campos, botones, filtros y navegación tienen esquinas cuadradas (rounded.square). La marca de Flow usa cinco barras rectangulares. Los iconos son geometrías vectoriales; los detalles ASCII acompañan nombres y estados. La cápsula usa rounded.island; los controles de cancelar y terminar dentro de ella no se convierten en botones circulares.

**The Capsule Exception Rule.** Conserva las esquinas cuadradas de módulos y controles principales; aplica el radio island únicamente a la cápsula de grabación.

## Components

Los componentes del frontmatter muestran la asignación oscura por defecto; en tema claro se usa el token homónimo light. La cápsula conserva sus tokens island en ambos temas. Los ejemplos HTML/CSS del sidecar son traducciones autónomas para el panel: su foco de navegador no declara una implementación nueva de foco en WPF.

### Buttons

- **Primario:** button-primary, cuadrado, SemiBold y 13.5 DIP. Hover baja opacidad a 0.92; pressed a 0.8; deshabilitado a 0.4 y cursor de flecha.
- **Secundario:** button-secondary, borde 1 en border, Medium y 12.5 DIP. Hover usa bg-card-hover y border-hover; pressed y disabled usan las mismas opacidades 0.8 y 0.4.
- Las plantillas principales no añaden un trigger explícito de foco de teclado; no se registra un anillo inventado como regla del sistema.

### Chips / Filters

Los filtros son RadioButton rectangulares: filter, borde 1 y separación derecha 6. Hover cambia borde a border-hover y texto a text-primary. Seleccionado usa accent en fondo y borde, on-accent en texto y SemiBold. El nombre histórico WisprFilterPill no describe su forma actual.

### Cards / Containers

WisprCard usa card, borde 1 en border y padding 16; no lleva sombra ni estado hover propio. Los contenedores de reunión pueden sobrescribir padding a 0 o 18. Esas excepciones no cambian el valor base.

### Inputs / Fields

WisprTextBox usa input, borde 1 en border, cursor de texto en accent y tamaño body. Hover cambia a border-hover; foco cambia borde a accent y fondo a bg-card-hover. Declara Padding 10,8, pero la plantilla no lo enlaza explícitamente al contenedor de contenido; no se promete una medida interior efectiva sin medición visual. Los ComboBox y CheckBox conservan plantillas de plataforma con colores asignados localmente; no se infiere de ellas una biblioteca de estados nueva.

### Navigation

WisprNavTab: RadioButton con padding 10,8 y margen vertical 1.5. En reposo tiene fondo transparente y text-secondary; hover usa bg-card-hover y text-primary. Seleccionado usa navigation-selected, SemiBold e indicador rectangular de 3 × 18 en on-accent. La barra lateral permite scroll; no hay variante móvil implementada.

### Recording Capsule

La cápsula de Flow usa island, borde 1 en island-border y controles de 26 × 26. Cancelar tiene núcleo cuadrado de 8 y pulso de 14; terminar usa el carácter ASCII > en Consolas 17 Bold. Hover y foco de teclado bajan su opacidad a 0.72. Los nombres accesibles y tooltips explicitan Cancelar dictado y Terminar y pegar.

El visualizador ocupa 224 × 30 DIP durante grabación: dos hebras de partículas cuadradas en lima y verde con brillo, tamaño y amplitud modulados por el nivel de audio. VoiceSignal aspira a 60 actualizaciones visuales por segundo, consume el pico más reciente sin encolar callbacks en el dispatcher, y suaviza ataque y caída con constantes de 45 y 180 ms. La duración se sitúa a su lado; el texto aparece debajo cuando ShowRecording recibe previsualización activa. MainWindow activa esa opción para transcripción local. La última palabra permanece en island-pending, las anteriores usan island-text; el contenedor permite wrap y scroll, sin exigir una única línea.

Procesamiento detiene el reloj, reduce la señal a 124 DIP, aplana su amplitud y mantiene una luz que recorre la hebra, muestra Procesando o Transcribiendo y cambia el control cuadrado a warning. Éxito oculta la señal, muestra [OK], usa success y espera 750 ms antes de salir. La ventana es topmost, no se activa al mostrarse y aplica WS_EX_NOACTIVATE y WS_EX_TOOLWINDOW; se puede arrastrar desde la cápsula.

## Do's and Don'ts

### Do:

- Do conservar nombres funcionales y estados explícitos junto al color.
- Do usar las parejas de recursos dark y light según el tema activo.
- Do conservar la cápsula negra, su línea fina luminosa y el texto debajo.
- Do permitir wrap y desplazamiento vertical para la previsualización larga.
- Do mantener el dictado global disponible desde la aplicación activa.

### Don't:

- Don't introducir lemas ni encabezados promocionales.
- Don't convertir los filtros rectangulares en cápsulas por el nombre histórico de su estilo.
- Don't sustituir la línea de puntos y destellos por barras gruesas o una onda sólida.
- Don't aplicar el tema claro al fondo negro de la cápsula.
- Don't presentar una rampa tonal del panel como un recurso de color implementado.

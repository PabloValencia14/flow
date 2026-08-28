# Flow

Primera rebanada vertical de la arquitectura local-first descrita en el
diseño: `FlowHub` ya proporciona almacenamiento SQLite en modo WAL, registro
de dispositivos, eventos de sincronización idempotentes, lectura incremental,
WebSocket y exportación de reuniones a Knowledge.

En el portátil ya existe `Wispr Flow 1.6.606` como instalación temporal de
prueba. No se debe desinstalar hasta que Flow.Windows supere los criterios de
aceptación documentados. La situación de la instalación de prueba está
documentada en
[`wispr-flow-integration.md`](wispr-flow-integration.md).

La captura de audio propia, la edición contextual, la integración WASAPI y la
APK todavía no están declaradas como terminadas. Wispr Flow se mantiene
temporalmente para comparar latencia, precisión, formato y comportamiento de
pegado; solo se retirará después de una sustitución verificada.

## Estado seguro actual

- No modifica Tailscale, Windows Firewall, Syncthing, MediaPortal, AceStream ni
  ningún flujo de streaming.
- Por defecto escucha en `127.0.0.1:8790`, nunca en la LAN.
- La base de datos está fuera de `C:\Knowledge` y fuera de Syncthing.
- La exportación de reuniones sí escribe únicamente bajo la raíz de Knowledge
  configurada.
- Las credenciales de Wispr/Groq no forman parte del hub ni de los archivos del
  proyecto.

## Instalación reproducible de Windows

`installer\Build-FlowInstaller.ps1` genera una publicación `win-x64`
autocontenida y un ZIP que se puede llevar a otro ordenador Windows sin
instalar .NET. Al ejecutar `install-flow.bat` desde la raíz del proyecto (o el
que hay en `installer`) se instala Flow.Windows, se registra el inicio en
segundo plano y se solicita la clave de Groq. La clave se guarda en el
Administrador de credenciales de Windows; nunca se incluye en el repositorio.

Tailscale no forma parte de la instalación predeterminada. No se instala ni se
configura. `Install-Flow.ps1 -EnableTailscale` existe como opción explícita
para comprobar un cliente Tailscale ya instalado, pero no inicia sesión ni
modifica rutas. La sincronización FlowHub se habilita aparte desde Ajustes.

## Ejecución local

Desde `flow/FlowHub`:

```powershell
dotnet restore
$env:FLOW_HUB_DATA_ROOT = "$pwd\data-local"
$env:FLOW_HUB_KNOWLEDGE_ROOT = "$pwd\knowledge-local"
dotnet run
```

Comprobación:

```powershell
Invoke-RestMethod http://127.0.0.1:8790/healthz
```

Para simular autenticación de aplicación, define `FLOW_HUB_APP_TOKEN` antes de
arrancar y añade la cabecera Bearer a las rutas `/v1/*`.

## Despliegue posterior en el homelab

El despliegue real crea `C:\FlowHub`, mantiene la base fuera de Syncthing,
instala el proceso como servicio Windows y configura Tailscale Serve solo
después de comprobar el proceso en loopback, el nombre MagicDNS y el ACL del
tailnet. `appsettings.example.json` muestra las rutas; el token de aplicación
se mantiene fuera del repositorio.

Estado de la primera instalación en el homelab (26/08/2026): `FlowHub` está
instalado y sano en `127.0.0.1:8790`, con autenticación de aplicación activa.
Serve no está habilitado todavía en el tailnet; el CLI de Tailscale entregó un
enlace de activación administrativa. Hasta completar esa acción, no existe un
endpoint remoto Flow y no se debe usar la LAN como sustituto.

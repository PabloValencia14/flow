# Instalador reproducible de Flow para Windows

Este paquete instala Flow.Windows como aplicación `win-x64` autocontenida. El
ordenador de destino no necesita tener instalado .NET ni el SDK.

## Generar el paquete

Desde la raíz del repositorio:

```powershell
.\flow\installer\Build-FlowInstaller.ps1 -Clean
```

El script genera:

- `flow\installer\release\Flow-Windows-Installer\`, carpeta que se puede
  copiar a otro ordenador.
- `flow\installer\release\Flow-Windows-Installer.zip`, paquete distribuible.

Al clonar el repositorio también se puede ejecutar directamente
`flow\installer\install-flow.bat`: el instalador usa el ZIP incluido en
`flow\installer\release` y no necesita regenerar el payload.

## Instalar en otro Windows

1. Copia y descomprime `Flow-Windows-Installer.zip`.
2. Ejecuta `install-flow.bat`.
3. Introduce la clave de Groq cuando la solicite el instalador.

La clave se guarda en el Administrador de credenciales de Windows bajo
`Flow/GroqApiKey`. No se guarda en el repositorio, en un archivo de texto ni en
los argumentos del proceso. Si se deja vacía, se puede ejecutar después:

```powershell
.\Set-FlowGroqKey.ps1
```

La instalación crea el acceso directo del menú Inicio y el arranque residente
en segundo plano. El acceso directo utiliza el `FlowLogo.ico` incluido en el
paquete para que Windows no conserve el icono de una versión anterior. La
actualización no sustituye ni borra la base local existente en
`%LOCALAPPDATA%\Flow\flow.db`.

## Tailscale

Tailscale está desactivado por defecto: el instalador no lo instala, no inicia
sesión, no cambia rutas y no configura Serve. Flow puede funcionar solo con
Groq y la sincronización FlowHub puede configurarse posteriormente desde
Ajustes.

Si el usuario ya ha instalado Tailscale y quiere comprobarlo durante la
instalación, puede ejecutar:

```powershell
.\Install-Flow.ps1 -EnableTailscale
```

Esta opción es opt-in y únicamente verifica que exista el cliente; no conecta
el equipo ni modifica la red. Para sincronizar con otro dispositivo se debe
configurar explícitamente la URL HTTPS de FlowHub. Tailscale es una opción de
transporte privado, no un requisito de Flow.

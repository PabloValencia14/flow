# Comprobaciones de Windows

Desde la raíz del repositorio, con el SDK de .NET 9 instalado en Windows:

```powershell
dotnet run --project tests/Flow.Windows.Checks -c Release
```

El ejecutable compila el cliente WPF y comprueba estadísticas, rachas, límites
del día local y el índice del historial. No añade bibliotecas de pruebas.
Usa una base temporal independiente: no abre la base del usuario ni conecta
con Groq o FlowHub. Si falla una comprobación, termina con código distinto de cero.

La comparación de rendimiento usa 50.000 dictados sintéticos y veinte lecturas
del historial reciente antes y después de crear el índice. Los tiempos son
diagnósticos; la condición estable es que SQLite use el índice y no una
ordenación temporal. No mide la latencia de dictado ni el renderizado de WPF.

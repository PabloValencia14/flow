using System.Diagnostics;
using Flow.Windows;
using Microsoft.Data.Sqlite;

var root = Path.Combine(Path.GetTempPath(), "Flow-checks-" + Guid.NewGuid().ToString("N"));
var previousRoot = Environment.GetEnvironmentVariable("FLOW_WINDOWS_DATA_ROOT");
Environment.SetEnvironmentVariable("FLOW_WINDOWS_DATA_ROOT", root);
try
{
    var store = new LocalOutbox();
    using var db = new SqliteConnection($"Data Source={store.DatabasePath}");
    db.Open();
    var today = DateTime.Today;

    void Sql(string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    void Insert(string id, DateTime localDay, int words, double seconds)
    {
        using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO dictations_history(id, text, word_count, duration_seconds, created_at)
            VALUES($id, 'Texto de prueba', $words, $seconds, $created);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$words", words);
        command.Parameters.AddWithValue("$seconds", seconds);
        command.Parameters.AddWithValue("$created", new DateTimeOffset(localDay).ToUniversalTime().ToString("O"));
        command.ExecuteNonQuery();
    }

    void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }

    Check(await store.GetStatisticsAsync() == new FlowStatistics(0, 0, 0, 0, 0, 0, 0),
        "El historial vacío muestra ceros, sin métricas de demostración");
    Insert("today", today.AddHours(12), 10, 60);
    Insert("yesterday", today.AddDays(-1).AddHours(12), 20, 60);
    Insert("older", today.AddDays(-2).AddHours(12), 30, 60);
    var stats = await store.GetStatisticsAsync();
    Check(stats == new FlowStatistics(60, 10, 180, 3, 3, 20, 0),
        "Totales reales, velocidad inferior a 210 y racha calculada");

    Sql("DELETE FROM dictations_history WHERE id='today'");
    Check((await store.GetStatisticsAsync()).DayStreak == 2, "La racha sigue activa desde ayer");
    Sql("DELETE FROM dictations_history WHERE id='yesterday'");
    Check((await store.GetStatisticsAsync()).DayStreak == 0, "Un día completo sin uso rompe la racha");
    Sql("DELETE FROM dictations_history");
    Insert("zero-duration", today.AddHours(12), 40, 0);
    Check((await store.GetStatisticsAsync()).AverageWpm == 0, "No se inventa velocidad cuando no hay duración");
    Sql("DELETE FROM dictations_history");
    Insert("fast", today.AddHours(12), 120, 30);
    Check((await store.GetStatisticsAsync()).MinutesSaved == 2.5, "El ahorro usa la hipótesis explícita de 40 palabras/min");
    Sql("DELETE FROM dictations_history");
    Insert("start", today.AddMinutes(1), 3, 1);
    Insert("end", today.AddDays(1).AddMinutes(-1), 5, 1);
    Insert("before", today.AddMinutes(-1), 7, 1);
    Insert("after", today.AddDays(1).AddMinutes(1), 11, 1);
    var todayHistory = await store.GetHistoryAsync(category: "today");
    Check(todayHistory.Select(item => item.Id).Order().SequenceEqual(new[] { "end", "start" }) &&
        (await store.GetStatisticsAsync()).WordsToday == 8,
        "Hoy y las estadísticas comparten los límites del día local");

    // Compare the same query and data; timing is diagnostic, the plan assertion is deterministic.
    Sql("DELETE FROM dictations_history; DROP INDEX ix_dictations_created_at");
    Sql("""
        WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<50000)
        INSERT INTO dictations_history(id, text, created_at)
        SELECT CAST(x AS TEXT), 'Sintético', strftime('%Y-%m-%dT%H:%M:%fZ', '2025-01-01', '+' || x || ' minutes') FROM n;
        """);
    const string query = "SELECT id, text, raw_transcript, duration_seconds, word_count, created_at, app_name, is_favorite FROM dictations_history ORDER BY created_at DESC LIMIT 100";
    double Measure()
    {
        var elapsed = Stopwatch.StartNew();
        for (var run = 0; run < 20; run++)
        {
            using var command = db.CreateCommand();
            command.CommandText = query;
            using var reader = command.ExecuteReader();
            while (reader.Read()) { }
        }
        return elapsed.Elapsed.TotalMilliseconds / 20;
    }
    var before = Measure();
    _ = new LocalOutbox();
    var after = Measure();
    using (var command = db.CreateCommand())
    {
        command.CommandText = "EXPLAIN QUERY PLAN " + query;
        using var reader = command.ExecuteReader();
        var plan = new List<string>();
        while (reader.Read()) plan.Add(reader.GetString(3));
        Check(plan.Any(line => line.Contains("ix_dictations_created_at")) &&
            plan.All(line => !line.Contains("TEMP B-TREE")),
            "El historial reciente utiliza el índice sin ordenar toda la tabla");
    }
    using (var command = db.CreateCommand())
    {
        command.CommandText = "SELECT COUNT(*) FROM dictations_history";
        Check(Convert.ToInt32(command.ExecuteScalar()) == 50000, "La migración del índice conserva los 50.000 registros");
    }
    Check((await store.GetHistoryAsync()).Count == 100, "Se conserva el límite existente de historial");
    Console.WriteLine($"BENCHMARK: 50.000 filas, media de 20 consultas: sin índice {before:F3} ms; con índice {after:F3} ms");

    static byte[] BuildTestWave(int durationMs, Func<int, double, short> sampleGenerator, int sampleRate = 16_000)
    {
        var sampleCount = sampleRate * durationMs / 1_000;
        using var stream = new MemoryStream();
        using var writer = new NAudio.Wave.WaveFileWriter(stream, new NAudio.Wave.WaveFormat(sampleRate, 16, 1));
        var pcm = new byte[sampleCount * sizeof(short)];
        for (var i = 0; i < sampleCount; i++)
        {
            var value = sampleGenerator(i, i * 1_000d / sampleRate);
            BitConverter.TryWriteBytes(pcm.AsSpan(i * sizeof(short), sizeof(short)), value);
        }
        writer.Write(pcm, 0, pcm.Length);
        writer.Flush();
        return stream.ToArray();
    }

    var pureSilenceWav = BuildTestWave(2_000, (_, _) => 0);
    Check(!AudioCapture.HasSpeechEnergy(pureSilenceWav), "La puerta de energía rechaza el silencio puro");

    var singleClickWav = BuildTestWave(2_000, (idx, ms) => ms is >= 500 and < 520 ? (short)(Math.Sin(idx * 0.25) * 4_500) : (short)0);
    Check(!AudioCapture.HasSpeechEnergy(singleClickWav), "La puerta de energía rechaza un clic aislado menor de 80 ms");

    var stationaryFanWav = BuildTestWave(2_000, (idx, _) => (short)(Math.Sin(idx * 0.12) * 320));
    Check(!AudioCapture.HasSpeechEnergy(stationaryFanWav), "La puerta de energía rechaza ruido estacionario constante de ventilador");

    var speechThenSilenceWav = BuildTestWave(4_000, (idx, ms) => ms is >= 300 and <= 1_100 ? (short)(Math.Sin(idx * 0.09) * 3_200) : (short)0);
    Check(AudioCapture.HasSpeechEnergy(speechThenSilenceWav), "La puerta de energía detecta habla sostenida normal");
    Check(AudioCapture.HasSpeechEnergyInRange(speechThenSilenceWav, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(1_100)),
        "La comprobación por rango conserva el tramo con voz");
    Check(!AudioCapture.HasSpeechEnergyInRange(speechThenSilenceWav, TimeSpan.FromMilliseconds(1_600), TimeSpan.FromMilliseconds(3_900)),
        "La comprobación por rango detecta silencio en la cola sin voz");

    Check(DictationTextProcessor.CleanAsrSegmentText("Transcribe literalmente las palabras pronunciadas.") == string.Empty,
        "El filtro ASR elimina el eco del antiguo prompt");
    Check(DictationTextProcessor.CleanAsrSegmentText("Gracias por ver el video.") == string.Empty &&
          DictationTextProcessor.CleanAsrSegmentText("Gracias por ver este vídeo.") == string.Empty &&
          DictationTextProcessor.CleanAsrSegmentText("Subtítulos realizados por la comunidad de Amara.org") == string.Empty &&
          DictationTextProcessor.CleanAsrSegmentText("[Música]") == string.Empty,
        "El filtro ASR elimina alucinaciones típicas de cierre y subtítulos");
    Check(DictationTextProcessor.CleanAsrSegmentText("Gracias.", noSpeechProbability: 0.35) == string.Empty,
        "El filtro ASR descarta 'Gracias.' cuando no_speech_prob es elevado");
    Check(DictationTextProcessor.CleanAsrSegmentText("Gracias.", noSpeechProbability: 0.03, avgLogProb: -0.15) == "Gracias.",
        "El filtro ASR conserva 'Gracias.' si se pronunció con claridad");
    Check(DictationTextProcessor.CleanAsrSegmentText("Hola equipo. Gracias por ver el video.") == "Hola equipo.",
        "El filtro ASR limpia alucinaciones pegadas al final de una frase válida");

    Check(!ThemeManager.ResolveIsLight(AppTheme.System, windowsInDarkMode: true) &&
          ThemeManager.ResolveIsLight(AppTheme.System, windowsInDarkMode: false) &&
          !ThemeManager.ResolveIsLight(AppTheme.Dark, windowsInDarkMode: false) &&
          ThemeManager.ResolveIsLight(AppTheme.Light, windowsInDarkMode: true),
        "El tema del sistema sigue el modo claro u oscuro de Windows mientras los fijos se mantienen");
}
finally
{
    Environment.SetEnvironmentVariable("FLOW_WINDOWS_DATA_ROOT", previousRoot);
    SqliteConnection.ClearAllPools();
    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
}

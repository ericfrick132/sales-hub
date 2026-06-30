using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SalesHub.Core.Abstractions;

namespace SalesHub.Infrastructure.Services;

public enum BatchItemKind { Image, Text, Audio }

/// <summary>Un mensaje del batch ya clasificado por el relay. Para Image/Audio guardamos
/// el JSON crudo del mensaje (para bajar el media al cerrar el batch); para Text guardamos
/// el texto. En Image, <see cref="Text"/> es el caption (puede ser null).</summary>
public record BatchItem(BatchItemKind Kind, string? Text, string? RawJson);

/// <summary>
/// Junta los mensajes que un número autorizado manda seguido (imágenes + texto + audios)
/// y, tras <c>windowSeconds</c> de inactividad (debounce), arma un único PDF y lo responde
/// por WhatsApp. Singleton: vive entre webhooks (cada webhook es un request aparte) y al
/// cerrar el batch abre su propio scope de DI para bajar media, transcribir y enviar.
/// </summary>
public class TranscriptionBatchAccumulator
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TranscriptionBatchAccumulator> _log;

    private readonly ConcurrentDictionary<string, BatchState> _batches = new();

    public TranscriptionBatchAccumulator(
        IServiceScopeFactory scopeFactory, ILogger<TranscriptionBatchAccumulator> log)
    {
        _scopeFactory = scopeFactory; _log = log;
    }

    private sealed class BatchState
    {
        public readonly object Lock = new();
        public readonly List<BatchItem> Items = new();
        public Timer? Timer;
        public bool Flushed;
        public DateTimeOffset LastEnqueueUtc;
        public int WindowSeconds = 10;
        public readonly string Instance;
        public readonly string ReplyTo;
        public BatchState(string instance, string replyTo) { Instance = instance; ReplyTo = replyTo; }
    }

    /// <summary>Agrega un mensaje al batch del remitente y (re)arma el timer de debounce.</summary>
    public void Enqueue(string instance, string replyTo, BatchItem item, int windowSeconds)
    {
        var window = windowSeconds < 1 ? 1 : windowSeconds;
        var key = instance + "\n" + replyTo;

        while (true)
        {
            var state = _batches.GetOrAdd(key, _ => new BatchState(instance, replyTo));
            lock (state.Lock)
            {
                // El timer ya cerró este batch entre el GetOrAdd y el lock: reintentamos
                // para que GetOrAdd cree uno nuevo (el viejo ya se quitó del diccionario).
                if (state.Flushed) continue;

                state.Items.Add(item);
                state.WindowSeconds = window;
                state.LastEnqueueUtc = DateTimeOffset.UtcNow;
                var dueMs = window * 1000;
                if (state.Timer is null)
                    state.Timer = new Timer(_ => OnTimer(key, state), null, dueMs, Timeout.Infinite);
                else
                    state.Timer.Change(dueMs, Timeout.Infinite);
                return;
            }
        }
    }

    private void OnTimer(string key, BatchState state)
    {
        List<BatchItem> items;
        lock (state.Lock)
        {
            if (state.Flushed) return;

            // Anti-carrera fire/reset: si llegó un mensaje hace menos de la ventana,
            // el timer disparó tarde respecto del último Change → re-agendamos el resto.
            var remaining = state.LastEnqueueUtc.AddSeconds(state.WindowSeconds) - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.FromMilliseconds(50))
            {
                state.Timer!.Change((int)remaining.TotalMilliseconds, Timeout.Infinite);
                return;
            }

            state.Flushed = true;
            state.Timer?.Dispose();
            items = state.Items.ToList();
            _batches.TryRemove(new KeyValuePair<string, BatchState>(key, state));
        }

        // Trabajo pesado (bajar media, transcribir, armar PDF, enviar) fuera del lock.
        _ = Task.Run(() => FlushCoreAsync(state.Instance, state.ReplyTo, items));
    }

    private async Task FlushCoreAsync(string instance, string replyTo, List<BatchItem> items)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var evo = scope.ServiceProvider.GetRequiredService<IEvolutionClient>();
            var whisper = scope.ServiceProvider.GetRequiredService<GroqWhisperClient>();

            var images = new List<RequirementPdfBuilder.ImagePart>();
            var texts = new List<string>();
            var transcripts = new List<string>();

            foreach (var item in items)
            {
                switch (item.Kind)
                {
                    case BatchItemKind.Text:
                        if (!string.IsNullOrWhiteSpace(item.Text)) texts.Add(item.Text!);
                        break;

                    case BatchItemKind.Image:
                    {
                        var bytes = item.RawJson is null ? null
                            : await evo.GetMediaBase64Async(instance, item.RawJson);
                        if (bytes is not null)
                            images.Add(new RequirementPdfBuilder.ImagePart(bytes, item.Text));
                        else
                            _log.LogWarning("Batch: no se pudo bajar una imagen (instance={I})", instance);
                        break;
                    }

                    case BatchItemKind.Audio:
                    {
                        var bytes = item.RawJson is null ? null
                            : await evo.GetMediaBase64Async(instance, item.RawJson);
                        string? transcript = null;
                        if (bytes is not null && whisper.IsConfigured)
                            transcript = await whisper.TranscribeAsync(bytes, "voice.ogg");
                        transcripts.Add(string.IsNullOrWhiteSpace(transcript)
                            ? "(no se pudo transcribir este audio)"
                            : transcript!);
                        break;
                    }
                }
            }

            if (images.Count == 0 && texts.Count == 0 && transcripts.Count == 0)
            {
                await evo.SendTextAsync(instance, replyTo,
                    "No pude armar el PDF: no llegó contenido que pueda procesar 😕.");
                return;
            }

            var now = DateTimeOffset.Now;
            var title = $"Requerimiento — {now:dd/MM/yyyy HH:mm}";
            var pdf = RequirementPdfBuilder.Build(images, texts, transcripts, title);
            var fileName = $"requerimiento-{now:yyyyMMdd-HHmm}.pdf";
            var caption = $"📄 Requerimiento armado: {images.Count} imagen(es), "
                        + $"{texts.Count} texto(s), {transcripts.Count} audio(s).";

            var ok = await evo.SendMediaAsync(instance, replyTo, pdf, "application/pdf", fileName, caption);
            _log.LogInformation(
                "Batch PDF: instance={I} to={To} imgs={Img} txt={Txt} audio={Aud} bytes={Bytes} enviado={Ok}",
                instance, replyTo, images.Count, texts.Count, transcripts.Count, pdf.Length, ok);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Batch flush falló (instance={I} to={To})", instance, replyTo);
        }
    }
}

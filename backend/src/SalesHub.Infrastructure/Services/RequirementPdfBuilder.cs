using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Arma el PDF de un "requerimiento" batch a partir de lo que mandó el número autorizado:
/// primero las imágenes (en el orden en que llegaron), debajo el texto y al final la
/// transcripción de los audios. Generación 100% en código (QuestPDF, sin browser).
/// </summary>
public static class RequirementPdfBuilder
{
    static RequirementPdfBuilder()
    {
        // Licencia Community de QuestPDF (gratis para uso personal / facturación < USD 1M).
        // Acá (no en Program.cs) para que valga en cualquier proceso que arme el PDF.
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public record ImagePart(byte[] Bytes, string? Caption);

    /// <param name="images">Imágenes en orden de llegada (bytes ya desencriptados) + caption opcional.</param>
    /// <param name="texts">Mensajes de texto en orden de llegada.</param>
    /// <param name="transcripts">Transcripciones de los audios en orden de llegada.</param>
    /// <param name="title">Encabezado del documento.</param>
    public static byte[] Build(
        IReadOnlyList<ImagePart> images,
        IReadOnlyList<string> texts,
        IReadOnlyList<string> transcripts,
        string title)
    {
        return Document.Create(doc =>
        {
            doc.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(36);
                page.DefaultTextStyle(t => t.FontSize(11).FontColor(Colors.Grey.Darken4));

                page.Header().PaddingBottom(10).Column(h =>
                {
                    h.Item().Text(title).FontSize(16).SemiBold();
                    h.Item().Text($"{images.Count} imagen(es) · {texts.Count} texto(s) · {transcripts.Count} audio(s)")
                        .FontSize(9).FontColor(Colors.Grey.Medium);
                });

                page.Content().Column(col =>
                {
                    col.Spacing(14);

                    // 1) Imágenes en orden. Cada una limitada en alto para no romper el layout
                    //    si la foto es muy vertical; QuestPDF la pagina sola si no entra.
                    foreach (var img in images)
                    {
                        col.Item().Column(block =>
                        {
                            block.Spacing(4);
                            block.Item().MaxHeight(620).AlignLeft().Image(img.Bytes).FitArea();
                            if (!string.IsNullOrWhiteSpace(img.Caption))
                                block.Item().Text(img.Caption!).Italic().FontColor(Colors.Grey.Darken1);
                        });
                    }

                    // 2) Texto suelto, debajo de las imágenes.
                    if (texts.Count > 0)
                    {
                        col.Item().Column(block =>
                        {
                            block.Spacing(4);
                            block.Item().Text("Texto").SemiBold().FontColor(Colors.Blue.Darken2);
                            foreach (var t in texts)
                                block.Item().Text(t);
                        });
                    }

                    // 3) Transcripción de los audios, al final.
                    if (transcripts.Count > 0)
                    {
                        col.Item().Column(block =>
                        {
                            block.Spacing(4);
                            block.Item().Text("Audios (transcripción)").SemiBold().FontColor(Colors.Blue.Darken2);
                            foreach (var tr in transcripts)
                                block.Item().Text(tr);
                        });
                    }
                });

                page.Footer().AlignRight().Text(t =>
                {
                    t.CurrentPageNumber();
                    t.Span(" / ");
                    t.TotalPages();
                });
            });
        }).GeneratePdf();
    }
}

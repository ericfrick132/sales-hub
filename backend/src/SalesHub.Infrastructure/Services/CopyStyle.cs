using System.Text;
using System.Text.RegularExpressions;

namespace SalesHub.Infrastructure.Services;

/// <summary>
/// Reglas de forma de TODO lo que el sistema le manda solo a un lead por WhatsApp (guiones,
/// cadencias, respuestas de la IA, remarketing): sin tildes, sin signos de apertura y sin emojis.
/// Orden de Eric (2026-09-30: "no uses acentos nunca"). Se aplica en código antes de enviar
/// porque el prompt no alcanza. La ñ se mantiene. Lo que un humano escribe a mano no pasa por acá.
/// </summary>
public static class CopyStyle
{
    private static readonly Regex Emoji = new(@"[☀-➿️‍]|\uD83C[\uDC00-\uDFFF]|\uD83D[\uDC00-\uDFFF]|\uD83E[\uDC00-\uDFFF]", RegexOptions.Compiled);

    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            sb.Append(c switch
            {
                'á' => 'a', 'é' => 'e', 'í' => 'i', 'ó' => 'o', 'ú' => 'u', 'ü' => 'u',
                'Á' => 'A', 'É' => 'E', 'Í' => 'I', 'Ó' => 'O', 'Ú' => 'U', 'Ü' => 'U',
                _ => c,
            });
        }
        var t = Emoji.Replace(sb.ToString(), "").Replace("¿", "").Replace("¡", "");
        t = Regex.Replace(t, @"[ \t]{2,}", " ");
        return Regex.Replace(t, @"[ \t]+(\n|$)", "$1").Trim();
    }
}

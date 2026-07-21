namespace SalesHub.Core.Abstractions;

public interface IPhoneNormalizer
{
    string? Normalize(string? rawPhone, string countryPrefix);

    /// <summary>
    /// Normalización ESTRICTA para celulares argentinos: devuelve el canónico
    /// 549 + área + número (13 dígitos) solo si las transformaciones determinísticas
    /// (00/0 inicial, 54 duplicado, 9 doble, "15" después del área) llegan a un número
    /// estructuralmente válido. Null si habría que adivinar dígitos.
    /// </summary>
    string? NormalizeArStrict(string? rawPhone);

    /// <summary>
    /// Candidatos de reparación ordenados por plausibilidad para un número que NO pasa
    /// la normalización estricta (ej. un dígito de más → probar sacando de a uno).
    /// Pensados para verificarse contra WhatsApp (CheckNumbers) y quedarse con el que existe.
    /// </summary>
    IReadOnlyList<string> ArRepairCandidates(string? rawPhone);

    /// <summary>True si ya es un celular argentino canónico (549 + 10 dígitos, área válida).</summary>
    bool IsCanonicalAr(string? phone);
}

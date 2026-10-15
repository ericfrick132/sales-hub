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
    /// Teléfono de un negocio (Maps, su web): si es un celular seguro (traía el 15 o el 9) queda
    /// canónico 549…; si no, como <see cref="Normalize"/> (54 + área + número), que sirve para llamar
    /// a un fijo. No se le agrega el 9 a ciegas: un fijo con 9 no existe en WhatsApp ni se puede llamar.
    /// </summary>
    string? NormalizeBusiness(string? rawPhone, string countryPrefix);

    /// <summary>El número crudo es seguro un celular argentino (trae el 9 después del 54 o el 15 después del área).</summary>
    bool IsMarkedArMobile(string? rawPhone);

    /// <summary>
    /// Candidatos de reparación ordenados por plausibilidad para un número que NO pasa
    /// la normalización estricta (ej. un dígito de más → probar sacando de a uno).
    /// Pensados para verificarse contra WhatsApp (CheckNumbers) y quedarse con el que existe.
    /// </summary>
    IReadOnlyList<string> ArRepairCandidates(string? rawPhone);

    /// <summary>True si ya es un celular argentino canónico (549 + 10 dígitos, área válida).</summary>
    bool IsCanonicalAr(string? phone);
}

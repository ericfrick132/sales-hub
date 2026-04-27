namespace SalesHub.Core.Abstractions;

public interface IPhoneNormalizer
{
    string? Normalize(string? rawPhone, string countryPrefix);
}

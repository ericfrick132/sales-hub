namespace SalesHub.Core.Domain.Enums;

public enum DeviceStatus
{
    Offline = 0,
    Online = 1,
    Pairing = 2,     // esperando que el device ingrese el token
    Error = 3
}

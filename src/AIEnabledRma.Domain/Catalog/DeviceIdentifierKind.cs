namespace AIEnabledRma.Domain.Catalog;

/// <summary>
/// Kind of identifier a customer can paste into the device-lookup box.
/// Extend this enum (and the matching <c>DeviceIdentifierRule</c> configuration) to support
/// a new identifier scheme for your own product line; no code change is required as long as
/// you can normalise the value to a canonical form.
/// </summary>
public enum DeviceIdentifierKind
{
    Unknown = 0,

    /// <summary>Vendor-assigned device serial number, the primary system of record.</summary>
    SerialNumber = 1,

    /// <summary>Hardware / Bluetooth MAC address, 12 hex digits.</summary>
    MacAddress = 2,

    /// <summary>IMEI or MEID of the cellular module fitted to the device.</summary>
    Imei = 3,

    /// <summary>Third-party host serial (for example the serial printed by an OEM partner).</summary>
    HostSerial = 4,
}

/// <summary>
/// A device identifier the user typed, together with what we concluded about it.
/// Produced by the deterministic classifier in
/// <see cref="AIEnabledRma.Domain.Devices.DeviceIdentifierClassifier"/> — never by the AI.
/// </summary>
public sealed record DeviceIdentifierInput
{
    public required string RawValue { get; init; }

    public required DeviceIdentifierKind Kind { get; init; }

    /// <summary>Normalised form used for exact lookup (upper-case, separator-stripped).</summary>
    public required string NormalizedValue { get; init; }

    /// <summary>True when the value passed the checksum/format check for its kind.</summary>
    public bool PassedFormatValidation { get; init; }
}

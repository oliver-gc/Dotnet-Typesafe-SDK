namespace TypesafeSdk;

/// <summary>Ordered by verbosity; a client logs every level up to and including the configured one.</summary>
public enum TypeSafeLogLevel
{
    Off = 0,
    Error = 1,
    Warning = 2,
    Info = 3,
    Debug = 4,
}

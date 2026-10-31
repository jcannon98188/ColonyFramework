namespace Colony.Testing;

/// <summary>A harness result that did not pass, thrown by the <c>ThrowIfFailed</c> methods with the full <c>Describe()</c> text as the message.</summary>
public sealed class HarnessException : Exception
{
    public HarnessException()
    {
    }

    public HarnessException(string message)
        : base(message)
    {
    }

    public HarnessException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

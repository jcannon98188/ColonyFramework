// netstandard2.0 lacks the runtime support type that records with init accessors compile against.
#pragma warning disable IDE0130 // The namespace must match the runtime's.
namespace System.Runtime.CompilerServices;

internal static class IsExternalInit
{
}

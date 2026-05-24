// Polyfill: netstandard2.0 ships no IsExternalInit but the C# 9+ record /
// init-only feature emits a modreq on it. Declaring an internal copy in
// this assembly is the canonical fix for analyzer/source-generator
// projects.
namespace System.Runtime.CompilerServices;

internal static class IsExternalInit;

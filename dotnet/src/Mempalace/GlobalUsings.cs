global using System.Diagnostics.CodeAnalysis;
global using System.Globalization;
global using System.Text.Json;
global using System.Text.Json.Serialization;

namespace Mempalace;

/// <summary>Shared <see cref="JsonSerializerOptions" /> instances — never allocate per-call.</summary>
internal static class Json
{
    internal static readonly JsonSerializerOptions CaseInsensitive =
        new() { PropertyNameCaseInsensitive = true };

    internal static readonly JsonSerializerOptions Indented =
        new() { WriteIndented = true };
}
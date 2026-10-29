using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Tunapp.Core;

public sealed record FireDocument(string Path, JsonObject Fields, string? UpdateTime = null);
public sealed class FirestoreDeniedException() : Exception("Firebase ha rechazado la operación: revisa los permisos, las reglas o el estado de la campaña.");
public sealed class FirestoreConflictException() : Exception("El registro ha cambiado en otro móvil. Revisa el conflicto antes de sobrescribirlo.");

public static class FireFields
{
    public static JsonObject Text(string value) => new() { ["stringValue"] = value };
    public static JsonObject Number(long value) => new() { ["integerValue"] = value.ToString(CultureInfo.InvariantCulture) };
    public static JsonObject Time(DateTimeOffset value) => new() { ["timestampValue"] = value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) };
    public static string String(this FireDocument doc, string key) => doc.Fields[key]!["stringValue"]!.GetValue<string>();
    public static long Integer(this FireDocument doc, string key) => long.Parse(doc.Fields[key]!["integerValue"]!.ToString(), CultureInfo.InvariantCulture);
    public static JsonObject OptionalDate(DateOnly? value) => value is { } date ? Time(new(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))) : new() { ["nullValue"] = null };
    public static DateOnly? Date(this FireDocument doc, string key) => doc.Fields[key]?["timestampValue"] is null ? null : DateOnly.FromDateTime(DateTimeOffset.Parse(doc.Fields[key]!["timestampValue"]!.GetValue<string>(), CultureInfo.InvariantCulture).UtcDateTime);
    public static JsonObject Copy(this JsonObject value) => (JsonObject)value.DeepClone();
}

/// <summary>REST con token de usuario: todas las peticiones se evalúan contra firestore.rules.</summary>
public sealed class FirestoreRest(HttpClient http, FirebaseAuthClient auth, string accountId, string projectId, string? emulatorHost = null)
{
    public string AccountId { get; } = accountId;
    private readonly string documentRoot = $"projects/{projectId}/databases/(default)/documents";
    private readonly string endpoint = Server(emulatorHost);
    private static string Server(string? emulator)
    {
        if (emulator is null) return "https://firestore.googleapis.com/v1/";
        if (!Uri.TryCreate(emulator, UriKind.Absolute, out var uri) || !uri.IsLoopback || uri.Scheme != "http")
            throw new ArgumentException("El emulador Firestore debe ser local.");
        return emulator.TrimEnd('/') + "/v1/";
    }
    private string Name(string path) => $"{documentRoot}/{path}";
    private static string Escaped(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
    private FireDocument Parse(JsonNode value)
    {
        var name = value["name"]!.GetValue<string>();
        return new(name[(documentRoot.Length + 1)..], value["fields"]?.AsObject().Copy() ?? new(), value["updateTime"]?.GetValue<string>());
    }
    private async Task<JsonNode?> RequestAsync(HttpMethod method, string relative, JsonObject? body = null, CancellationToken cancellation = default)
    {
        if (auth.Session?.UserId != AccountId) throw new UnauthorizedAccessException("La cuenta ha cambiado. Vuelve a abrir tus datos.");
        var token = await auth.GetTokenAsync();
        if (auth.Session?.UserId != AccountId) throw new UnauthorizedAccessException("La cuenta ha cambiado.");
        using var request = new HttpRequestMessage(method, endpoint + relative);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, cancellation);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            if (method == HttpMethod.Get) return null;
            throw new FirestoreConflictException();
        }
        if (response.StatusCode == HttpStatusCode.Forbidden) throw new FirestoreDeniedException();
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed) throw new FirestoreConflictException();
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new UnauthorizedAccessException("La sesión ha caducado. Inicia sesión de nuevo.");
        if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new IOException("Firebase no está disponible. No se pudo confirmar el guardado. Comprueba tu conexión e inténtalo de nuevo.");
        var text = await response.Content.ReadAsStringAsync(cancellation);
        if (!response.IsSuccessStatusCode)
        {
            var status = JsonNode.Parse(text)?["error"]?["status"]?.GetValue<string>();
            if (status is "FAILED_PRECONDITION" or "ALREADY_EXISTS" or "ABORTED") throw new FirestoreConflictException();
            throw new InvalidOperationException("Firebase no pudo validar los datos enviados.");
        }
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }
    public async Task<FireDocument?> GetAsync(string path, CancellationToken cancellation = default)
    {
        var node = await RequestAsync(HttpMethod.Get, Escaped(Name(path)), cancellation: cancellation);
        return node is null ? null : Parse(node);
    }
    public async Task<List<FireDocument>> ListAsync(string collection, CancellationToken cancellation = default)
    {
        var result = new List<FireDocument>();
        string? next = null;
        do
        {
            var query = "?pageSize=200" + (next is null ? "" : "&pageToken=" + Uri.EscapeDataString(next));
            var page = await RequestAsync(HttpMethod.Get, Escaped(Name(collection)) + query, cancellation: cancellation);
            if (page?["documents"] is JsonArray documents) result.AddRange(documents.Select(x => Parse(x!)));
            next = page?["nextPageToken"]?.GetValue<string>();
        } while (!string.IsNullOrEmpty(next));
        return result;
    }
    public JsonObject Write(FireDocument document, bool create, string? expectedUpdateTime = null)
    {
        var fields = document.Fields.Copy(); fields.Remove("updatedAt");
        var transforms = new JsonArray();
        if (create && fields.Remove("createdAt")) transforms.Add(new JsonObject { ["fieldPath"] = "createdAt", ["setToServerValue"] = "REQUEST_TIME" });
        if (document.Fields.ContainsKey("updatedAt")) transforms.Add(new JsonObject { ["fieldPath"] = "updatedAt", ["setToServerValue"] = "REQUEST_TIME" });
        var result = new JsonObject
        {
            ["update"] = new JsonObject { ["name"] = Name(document.Path), ["fields"] = fields },
            ["currentDocument"] = create ? new JsonObject { ["exists"] = false }
                : new JsonObject { ["updateTime"] = expectedUpdateTime ?? throw new InvalidOperationException("Falta la versión del registro.") }
        };
        if (transforms.Count > 0) result["updateTransforms"] = transforms;
        return result;
    }
    public JsonObject Delete(FireDocument document) => new()
    {
        ["delete"] = Name(document.Path),
        ["currentDocument"] = new JsonObject { ["updateTime"] = document.UpdateTime ?? throw new InvalidOperationException("Falta la versión del registro.") }
    };
    public async Task CommitAsync(IEnumerable<JsonObject> writes, CancellationToken cancellation = default)
    {
        await RequestAsync(HttpMethod.Post, Escaped(documentRoot) + ":commit", new JsonObject
        { ["writes"] = new JsonArray(writes.Select(x => (JsonNode)x).ToArray()) }, cancellation);
    }
}
